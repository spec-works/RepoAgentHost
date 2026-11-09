using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RepoYak.Api;

public sealed class AgentAdmin(AgentRegistry registry, string pipeName, ILogger<AgentAdmin> logger,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const string Usage = """
        Usage:
          repoyak start [repository-path] [--background]
          repoyak stop
          repoyak add <repository-path> [--id <friendly-id>] [--persona <custom-agent-name>] [--catalog <name>]
          repoyak remove <friendly-id> [--catalog <name>]
          repoyak list [--catalog <name>]
        All commands accept --config <json-path> and --port <port>.
        Catalogs are served at /catalogs/{name}; /catalog is the default catalog.
        Agents are shared at /agents/{id}. Remove with --catalog changes only membership.
        'start' runs the host in the foreground by default; --background detaches it on Windows.
        'stop' shuts down the host and its agents without removing saved registrations.
        'add', 'remove', and 'list' manage agents in the running host.
        Commands target the current user and REPOSITORY_AGENT_PORT (default 5080).
        'serve' and a single repository-path argument remain foreground host aliases.
        """;

    public static string PipeName(int port, string? configPath = null)
    {
        var account = $"{Environment.UserDomainName}\\{Environment.UserName}";
        if (!OperatingSystem.IsWindows())
        {
            // .NET stores Unix pipes in the temp directory, subject to socket path length limits.
            var identity = $"{account}\0{port}\0{(configPath is null ? "" : Path.GetFullPath(configPath))}";
            return "agentdock-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        }
        var accountHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)))[..16];
        var configSuffix = configPath is null ? "" : "-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? Path.GetFullPath(configPath).ToUpperInvariant()
                : Path.GetFullPath(configPath))))[..16];
        return $"agentdock-{accountHash}-{port}{configSuffix}";
    }

    public static AdminCommand ParseCommand(string[] args)
    {
        string? catalog = null;
        var filtered = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--catalog")
            {
                if (catalog is not null || ++index >= args.Length)
                {
                    throw new AgentRegistryException(400, "A single catalog name is required.");
                }
                catalog = AgentIds.Validate(args[index]);
            }
            else
            {
                filtered.Add(args[index]);
            }
        }
        args = filtered.ToArray();
        if (catalog is not null && (args.Length == 0 || args[0] is not ("add" or "remove" or "list")))
        {
            throw new AgentRegistryException(400, "--catalog applies to add, remove, or list.");
        }
        if (args is ["stop"])
        {
            return new("stop");
        }
        if (args is ["remove", var removeId])
        {
            return new("remove", Id: AgentIds.Validate(removeId), Catalog: catalog);
        }
        if (args is ["list"])
        {
            return new("list", Catalog: catalog);
        }
        if (args.Length < 2 || args[0] != "add" || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith('-'))
        {
            throw new AgentRegistryException(400, Usage);
        }
        string? id = null;
        string? persona = null;
        for (var index = 2; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new AgentRegistryException(400, Usage);
            }
            switch (args[index])
            {
                case "--id" when id is null:
                    id = AgentIds.Validate(args[index + 1]);
                    break;
                case "--persona" when persona is null:
                    persona = PersonaNames.Validate(args[index + 1]);
                    break;
                default:
                    throw new AgentRegistryException(400, "Unknown or repeated option.\n" + Usage);
            }
        }
        return new("add", args[1], id, persona, catalog);
    }

    public static async Task<int> RunCommandAsync(string[] args, string pipeName)
    {
        try
        {
            var command = ParseCommand(args);
            if (command.WorkspacePath is not null)
            {
                command = command with { WorkspacePath = Path.GetFullPath(command.WorkspacePath) };
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000, timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions));
            var line = await reader.ReadLineAsync(timeout.Token)
                ?? throw new IOException("The host closed its management connection without a response.");
            var reply = JsonSerializer.Deserialize<AdminReply>(line, JsonOptions)
                ?? throw new JsonException("The host returned an invalid management response.");
            if (!reply.Success)
            {
                Console.Error.WriteLine(reply.Error);
                return 1;
            }
            if (reply.Agents is not null)
            {
                Console.WriteLine(JsonSerializer.Serialize(reply.Agents, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
            }
            else if (command.Command == "stop")
            {
                if (reply.HostProcessId is int processId)
                {
                    Process? host;
                    try { host = Process.GetProcessById(processId); }
                    catch (ArgumentException) { host = null; }
                    if (host is not null)
                    {
                        using (host)
                        {
                            await host.WaitForExitAsync(timeout.Token);
                        }
                    }
                }
                Console.WriteLine("Stopped the host. Agent registrations and saved sessions were not deleted.");
            }
            else
            {
                Console.WriteLine(command.Catalog is null
                    ? $"Removed agent '{command.Id}'. Its repository folder and saved session were not deleted."
                    : $"Removed agent '{command.Id}' from catalog '{command.Catalog}'. The agent remains registered.");
            }
            return 0;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("No running host answered. Run 'start' first.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("The management command timed out. Its outcome is unknown; check host status before retrying.");
            return 1;
        }
        catch (Exception exception) when (exception is AgentRegistryException or IOException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                // An idle client must not permanently occupy the management interface.
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var line = await reader.ReadLineAsync(readTimeout.Token);
                AdminReply reply;
                AdminCommand? command = null;
                try
                {
                    command = JsonSerializer.Deserialize<AdminCommand>(
                        line ?? throw new JsonException("A management command is required."), JsonOptions)
                        ?? throw new JsonException("A management command is required.");
                    reply = await DispatchAsync(command, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Agent management command failed");
                    reply = new AdminReply(false, exception.Message);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, JsonOptions));
                if (reply.Success && command?.Command == "stop")
                {
                    lifetime.StopApplication();
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("An idle management connection timed out");
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Agent management connection was closed");
            }
        }
    }

    public async Task<AdminReply> DispatchAsync(AdminCommand command, CancellationToken cancellationToken)
    {
        switch (command.Command)
        {
            case "add":
                var agent = await registry.AddAsync(new RegisterAgentRequest(
                    command.WorkspacePath ?? throw new AgentRegistryException(400, "A repository path is required."),
                    command.Id, command.Persona, command.Catalog), cancellationToken);
                return new AdminReply(agent.Status == "ready", agent.Error, [agent]);
            case "remove":
                await registry.RemoveAsync(AgentIds.Validate(
                    command.Id ?? throw new AgentRegistryException(400, "An agent ID is required.")), cancellationToken, command.Catalog);
                return new AdminReply(true);
            case "stop" when command.WorkspacePath is null && command.Id is null && command.Persona is null && command.Catalog is null:
                return new AdminReply(true, HostProcessId: Environment.ProcessId);
            case "list":
                return new AdminReply(true, Agents: registry.List(command.Catalog).ToArray(), HostProcessId: Environment.ProcessId);
            default:
                throw new AgentRegistryException(400, Usage);
        }
    }

    public sealed record AdminCommand(string Command, string? WorkspacePath = null, string? Id = null,
        string? Persona = null, string? Catalog = null);
    public sealed record AdminReply(bool Success, string? Error = null, AgentRegistrationView[]? Agents = null,
        int? HostProcessId = null);
}
