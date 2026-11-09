using System.Net;
using A2A;
using A2A.AspNetCore;
using RepoYak.Api;

if (args is ["--help"] or ["-h"] or ["help"])
{
    Console.WriteLine(AgentAdmin.Usage);
    return;
}
if (args is ["--version"])
{
    Console.WriteLine($"RepoYak {typeof(AgentAdmin).Assembly.GetName().Version?.ToString(3)}");
    return;
}

var hostOptions = RepoYak.Api.HostOptions.Parse(args);
args = hostOptions.Arguments;
var port = hostOptions.Port;
var pipeName = AgentAdmin.PipeName(port, hostOptions.ConfigPath);
if (args.Length > 0 && args[0] is "add" or "remove" or "stop" or "list")
{
    Environment.ExitCode = await AgentAdmin.RunCommandAsync(args, pipeName);
    return;
}

var backgroundWorker = args.LastOrDefault() == HostCommands.BackgroundWorkerArgument;
if (backgroundWorker)
{
    args = args[..^1];
}
var (workspaceArgument, background) = HostCommands.ParseStart(args);
var initialWorkspace = workspaceArgument is null ? null : Path.TrimEndingDirectorySeparator(workspaceArgument);
var builder = WebApplication.CreateBuilder([]);
var baseUrl = $"http://127.0.0.1:{port}";
var stateDirectory = hostOptions.StateDirectory ?? builder.Configuration["REPOAGENT_STATE_DIRECTORY"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentDock", "hosts", $"port-{port}");
stateDirectory = Path.GetFullPath(stateDirectory);
if (background)
{
    Environment.ExitCode = await HostCommands.StartBackgroundAsync(initialWorkspace, port, pipeName, stateDirectory,
        hostOptions.ConfigPath);
    return;
}
using var backgroundLog = backgroundWorker
    ? OpenBackgroundLog(stateDirectory) : null;
if (backgroundLog is not null)
{
    Console.SetOut(backgroundLog);
    Console.SetError(backgroundLog);
}
var allowedRoots = builder.Configuration["REPOAGENT_ALLOWED_ROOTS"]?.Split(
    ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<SharedCopilotRuntime>();
builder.Services.AddSingleton(provider => new AgentRegistry(
    initialWorkspace, stateDirectory, allowedRoots, baseUrl,
    (registration, knowledge) => new CopilotAgentService(knowledge, builder.Configuration,
        provider.GetRequiredService<ILoggerFactory>().CreateLogger<CopilotAgentService>(),
        registration.Id, stateDirectory, provider.GetRequiredService<SharedCopilotRuntime>(), registration.Persona),
    provider.GetRequiredService<ILoggerFactory>(), hostOptions.ConfigPath, port));
builder.Services.AddHostedService(provider => provider.GetRequiredService<AgentRegistry>());
builder.Services.AddHostedService(provider => new AgentAdmin(
    provider.GetRequiredService<AgentRegistry>(), pipeName, provider.GetRequiredService<ILogger<AgentAdmin>>(),
    provider.GetRequiredService<IHostApplicationLifetime>()));
var app = builder.Build();
AgentEndpoints.Map(app, initialWorkspace);
app.Run();

static StreamWriter OpenBackgroundLog(string stateDirectory)
{
    Directory.CreateDirectory(stateDirectory);
    return new StreamWriter(new FileStream(Path.Combine(stateDirectory, "host.log"),
        FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
}

public partial class Program;
