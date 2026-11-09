using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;

namespace RepoYak.Api;

public static class HostCommands
{
    internal const string BackgroundWorkerArgument = "--background-worker";

    public static (string? WorkspacePath, bool Background) ParseStart(string[] args)
    {
        if (args.Length == 0)
        {
            throw new AgentRegistryException(400, AgentAdmin.Usage);
        }
        if (args.Length == 1 && args[0] is not ("start" or "serve") && !args[0].StartsWith('-'))
        {
            return (ValidatePath(args[0]), false);
        }
        if (args[0] is not ("start" or "serve"))
        {
            throw new AgentRegistryException(400, AgentAdmin.Usage);
        }
        string? path = null;
        var background = false;
        foreach (var argument in args.Skip(1))
        {
            if (argument == "--background" && args[0] == "start" && !background)
            {
                background = true;
            }
            else if (!argument.StartsWith('-') && path is null)
            {
                path = ValidatePath(argument);
            }
            else
            {
                throw new AgentRegistryException(400, AgentAdmin.Usage);
            }
        }
        return (path, background);
    }

    private static string ValidatePath(string path) =>
        !string.IsNullOrWhiteSpace(path) ? Path.GetFullPath(path)
            : throw new AgentRegistryException(400, "A repository path cannot be empty.");

    public static async Task<int> StartBackgroundAsync(string? workspacePath, int port,
        string pipeName, string stateDirectory, string? configPath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--background currently supports Windows only.");
            return 1;
        }
        var start = new ProcessStartInfo(Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the current executable."))
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.CurrentDirectory
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        }
        start.ArgumentList.Add("start");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString());
        if (configPath is not null)
        {
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(configPath);
        }
        if (workspacePath is not null)
        {
            start.ArgumentList.Add(workspacePath);
        }
        start.ArgumentList.Add(BackgroundWorkerArgument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the background host.");
        var logPath = Path.Combine(stateDirectory, "host.log");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            while (!process.HasExited)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.ConnectAsync(250, timeout.Token);
                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync("""{"command":"list"}""");
                    var reply = await reader.ReadLineAsync(timeout.Token);
                    var status = JsonSerializer.Deserialize<AgentAdmin.AdminReply>(reply ?? "",
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (status is { Success: true } && status.HostProcessId == process.Id)
                    {
                        using var health = await http.GetAsync($"http://127.0.0.1:{port}/healthz", timeout.Token);
                        if (health.IsSuccessStatusCode && !process.HasExited)
                        {
                            Console.WriteLine($"Started host in the background (PID {process.Id}). Log: {logPath}");
                            return 0;
                        }
                    }
                }
                catch (Exception exception) when (exception is TimeoutException or HttpRequestException
                    or IOException or OperationCanceledException && !timeout.IsCancellationRequested)
                {
                }
                await Task.Delay(100, timeout.Token);
            }
            Console.Error.WriteLine($"Background host exited with code {process.ExitCode}. Check {logPath}.");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
            Console.Error.WriteLine($"Background host did not become ready within 30 seconds. Check {logPath}.");
        }
        return 1;
    }
}
