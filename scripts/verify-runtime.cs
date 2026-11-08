#:project ../src/RepoAgentHost.Api/RepoAgentHost.Api.csproj
#:property PublishAot=false

using GitHub.Copilot;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

if (args is not [var runtimePath, var stateDirectory, var hostPath])
{
    throw new ArgumentException("Provide the installed runtime path, disposable state directory, and host path.");
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
await using (var client = new CopilotClient(new CopilotClientOptions
{
    Mode = CopilotClientMode.Empty,
    BaseDirectory = stateDirectory,
    UseLoggedInUser = false,
    Connection = RuntimeConnection.ForStdio(path: runtimePath)
}))
{
    await client.StartAsync(timeout.Token);
    var response = await client.PingAsync("repo-agent-host-package-check", timeout.Token);
    if (response.Message != "pong: repo-agent-host-package-check")
    {
        throw new InvalidOperationException($"Unexpected runtime ping response: '{response.Message}', protocol {response.ProtocolVersion}.");
    }
    Console.WriteLine($"Packaged runtime SDK handshake succeeded (protocol {response.ProtocolVersion}).");
}

using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();

var start = new ProcessStartInfo(hostPath)
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
start.ArgumentList.Add("start");
var configPath = Path.Combine(stateDirectory, "host.json");
start.ArgumentList.Add("--config");
start.ArgumentList.Add(configPath);
start.ArgumentList.Add("--port");
start.ArgumentList.Add(port.ToString());
start.Environment["REPOSITORY_AGENT_PORT"] = port.ToString();
start.Environment["REPOAGENT_STATE_DIRECTORY"] = Path.Combine(stateDirectory, "host");
using var host = Process.Start(start) ?? throw new InvalidOperationException("Could not start the installed host.");
var stdout = host.StandardOutput.ReadToEndAsync();
var stderr = host.StandardError.ReadToEndAsync();
try
{
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(1) };
    while (true)
    {
        timeout.Token.ThrowIfCancellationRequested();
        if (host.HasExited)
        {
            throw new InvalidOperationException($"Installed host exited ({host.ExitCode}): {await stdout}\n{await stderr}");
        }
        try
        {
            using var health = JsonDocument.Parse(await http.GetStringAsync("/healthz", timeout.Token));
            if (health.RootElement.GetProperty("status").GetString() != "ok")
            {
                throw new InvalidOperationException("Installed host reported an unhealthy status.");
            }
            break;
        }
        catch (HttpRequestException) when (!timeout.IsCancellationRequested)
        {
            await Task.Delay(200, timeout.Token);
        }
        catch (TaskCanceledException) when (!timeout.IsCancellationRequested)
        {
            await Task.Delay(200, timeout.Token);
        }
    }
    using var catalog = await http.GetAsync("/catalog", timeout.Token);
    catalog.EnsureSuccessStatusCode();
    if (catalog.Content.Headers.ContentType?.MediaType != "application/ai-catalog+json")
    {
        throw new InvalidOperationException("Installed host did not expose the AI Catalog media type.");
    }
    Console.WriteLine("Installed RepoAgentHost host health and catalog checks succeeded.");

    var workspace = Path.Combine(stateDirectory, "workspace with spaces");
    Directory.CreateDirectory(Path.Combine(workspace, ".git"));
    await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "# Package check\n\nDisposable workspace.");
    await RunCommandAsync("add", workspace, "--id", "package-check");
    await VerifyRegistrationAsync(present: true);
    await RunCommandAsync("stop");
    await host.WaitForExitAsync(timeout.Token);
    if (host.ExitCode != 0)
    {
        throw new InvalidOperationException($"Host shutdown failed ({host.ExitCode}): {await stderr}");
    }

    using var restarted = Process.Start(start) ?? throw new InvalidOperationException("Could not restart the installed host.");
    var restartedStdout = restarted.StandardOutput.ReadToEndAsync();
    var restartedStderr = restarted.StandardError.ReadToEndAsync();
    try
    {
        await VerifyRegistrationAsync(present: true);
        await RunCommandAsync("remove", "package-check");
        await VerifyRegistrationAsync(present: false);
        await RunCommandAsync("stop");
        await restarted.WaitForExitAsync(timeout.Token);
        if (restarted.ExitCode != 0)
        {
            throw new InvalidOperationException($"Restarted host shutdown failed ({restarted.ExitCode}): {await restartedStderr}");
        }
    }
    finally
    {
        if (!restarted.HasExited)
        {
            restarted.Kill(entireProcessTree: true);
        }
        await restarted.WaitForExitAsync();
        await Task.WhenAll(restartedStdout, restartedStderr);
    }
    Console.WriteLine("Installed CLI registration, persistence, removal, and graceful shutdown checks succeeded.");
}
finally
{
    if (!host.HasExited)
    {
        host.Kill(entireProcessTree: true);
    }
    await host.WaitForExitAsync();
    await Task.WhenAll(stdout, stderr);
}

async Task<string> RunCommandAsync(params string[] arguments)
{
    var command = new ProcessStartInfo(hostPath)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in arguments)
    {
        command.ArgumentList.Add(argument);
    }
    command.ArgumentList.Add("--config");
    command.ArgumentList.Add(configPath);
    command.ArgumentList.Add("--port");
    command.ArgumentList.Add(port.ToString());
    using var process = Process.Start(command) ?? throw new InvalidOperationException("Could not run an installed CLI command.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    try
    {
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Installed CLI '{arguments[0]}' failed ({process.ExitCode}): {await error}");
        }
        return await output;
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
    }
}

async Task VerifyRegistrationAsync(bool present)
{
    using var agents = JsonDocument.Parse(await RunCommandAsync("list"));
    var registrations = agents.RootElement.EnumerateArray().ToArray();
    if (registrations.Length != (present ? 1 : 0)
        || (present && (registrations[0].GetProperty("id").GetString() != "package-check"
            || registrations[0].GetProperty("status").GetString() != "ready")))
    {
        throw new InvalidOperationException("Installed CLI returned unexpected registrations.");
    }
}
