using System.Collections.Concurrent;
using System.Text.Json;
using A2A;

namespace RepoAgentHost.Api;

public sealed class AgentRegistry : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, HostedAgent> _agents = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly string? _initialWorkspace;
    private readonly string _stateDirectory;
    private readonly string[] _allowedRoots;
    private readonly string _baseUrl;
    private readonly Func<AgentRegistration, RepositoryKnowledge, IHostedAgentRuntime> _factory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentRegistry> _logger;
    private FileStream? _registryLock;
    private string _hostId = "";
    private readonly string _statePath;
    private readonly int? _port;
    private volatile Dictionary<string, string[]> _catalogs = new(StringComparer.Ordinal);
    private bool _stopped;

    public AgentRegistry(string? initialWorkspace, string stateDirectory, IEnumerable<string> allowedRoots,
        string baseUrl, Func<AgentRegistration, RepositoryKnowledge, IHostedAgentRuntime> factory,
        ILoggerFactory loggerFactory, string? configPath = null, int? port = null)
    {
        _initialWorkspace = initialWorkspace is null ? null : NormalizePath(initialWorkspace);
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _allowedRoots = allowedRoots.Select(NormalizePath).ToArray();
        _baseUrl = baseUrl.TrimEnd('/');
        _factory = factory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AgentRegistry>();
        _statePath = configPath is null ? Path.Combine(_stateDirectory, "agents.json") : Path.GetFullPath(configPath);
        _port = port;
    }

    private string StatePath => _statePath;
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stateDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        _registryLock = new FileStream(StatePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var existingState = File.Exists(StatePath);
            var state = existingState
                ? JsonSerializer.Deserialize<HostConfiguration>(
                    await File.ReadAllTextAsync(StatePath, cancellationToken), JsonOptions)
                    ?? throw new InvalidOperationException("Agent registry state is null.")
                : new HostConfiguration(Guid.NewGuid().ToString("N"), []);
            if (!Guid.TryParseExact(state.HostId, "N", out _) || state.Agents is null)
            {
                throw new InvalidOperationException("Agent registry state is invalid.");
            }
            _hostId = state.HostId;
            _catalogs = state.Catalogs ?? new Dictionary<string, string[]>
            {
                ["default"] = state.Agents.Select(a => a.Id).ToArray()
            };
            foreach (var (name, members) in _catalogs)
            {
                AgentIds.Validate(name);
                if (members is null || members.Distinct().Count() != members.Length
                    || members.Any(id => !state.Agents.Any(a => a.Id == id)))
                {
                    throw new InvalidOperationException("Catalog memberships are invalid.");
                }
            }
            if (!_catalogs.ContainsKey("default"))
            {
                _catalogs = new Dictionary<string, string[]>(_catalogs) { ["default"] = [] };
            }
            foreach (var registration in state.Agents)
            {
                AgentIds.Validate(registration.Id);
                if (registration.Persona is not null)
                {
                    PersonaNames.Validate(registration.Persona);
                }
                if (_agents.ContainsKey(registration.Id) || _agents.Values.Any(
                    a => a.Registration.WorkspacePath.Equals(NormalizePath(registration.WorkspacePath), PathComparison)))
                {
                    throw new InvalidOperationException("Agent registry contains duplicate IDs or repository paths.");
                }
                var normalized = registration with { WorkspacePath = NormalizePath(registration.WorkspacePath) };
                _agents[registration.Id] = await StartAgentAsync(normalized, cancellationToken);
            }
            if (!existingState)
            {
                if (_initialWorkspace is not null)
                {
                    await AddAsync(new RegisterAgentRequest(_initialWorkspace), cancellationToken);
                }
                else
                {
                    await SaveAsync([], cancellationToken);
                }
            }
            else if (state.Catalogs is null || !state.Catalogs.ContainsKey("default")
                || (_port is not null && state.Port != _port))
            {
                await SaveAsync(_agents.Values.Select(a => a.Registration), cancellationToken);
            }
        }
        catch
        {
            await StopAsync(CancellationToken.None);
            throw;
        }
    }

    public IReadOnlyList<AgentRegistrationView> List(string? catalog = null)
    {
        var members = catalog is null ? null : RequireCatalog(catalog);
        return _agents.Values.Where(a => members is null || members.Contains(a.Registration.Id))
            .OrderBy(a => a.Registration.Id, StringComparer.Ordinal).Select(View).ToArray();
    }

    private AgentRegistrationView View(HostedAgent agent) => new(
        agent.Registration.Id, agent.Registration.WorkspacePath, agent.Status, agent.Error,
        $"{_baseUrl}/agents/{agent.Registration.Id}/.well-known/agent-card.json", agent.Registration.Persona,
        _catalogs.Where(c => c.Value.Contains(agent.Registration.Id)).Select(c => c.Key).Order().ToArray());

    public AgentCard GetCard(string id) => RequireReady(id).Card
        ?? throw new InvalidOperationException("Ready agent has no card.");

    public IA2ARequestHandler GetHandler(string id) => RequireReady(id).Server
        ?? throw new InvalidOperationException("Ready agent has no A2A server.");

    private HostedAgent RequireReady(string id)
    {
        if (!_agents.TryGetValue(id, out var agent))
        {
            throw new AgentRegistryException(404, $"Agent '{id}' is not registered.");
        }
        if (agent.Status != "ready")
        {
            throw new AgentRegistryException(503, $"Agent '{id}' is not ready. Inspect the management endpoint for details.");
        }
        return agent;
    }

    private string[] RequireCatalog(string name)
    {
        AgentIds.Validate(name);
        return _catalogs.TryGetValue(name, out var members) ? members
            : throw new AgentRegistryException(404, $"Catalog '{name}' does not exist.");
    }

    public object Catalog(string name = "default")
    {
        var members = RequireCatalog(name);
        return new
        {
            specVersion = "1.0",
            host = new { identifier = $"urn:agentdock:host:{_hostId}", displayName = "RepoAgentHost" },
            entries = _agents.Values.Where(a => a.Status == "ready" && members.Contains(a.Registration.Id))
                .OrderBy(a => a.Registration.Id).Select(a => new
                {
                    identifier = $"urn:agentdock:{_hostId}:agent:{a.Registration.Id}",
                    displayName = a.Card!.Name,
                    description = a.Card.Description,
                    type = "application/a2a-agent-card+json",
                    tags = new[] { a.Registration.Id, "agent", "knowledge", "skills", "tools" },
                    url = View(a).CardUrl
                }).ToArray()
        };
    }

    public async Task<AgentRegistrationView> AddAsync(RegisterAgentRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            throw new AgentRegistryException(400, "A repository path is required.");
        }
        var path = NormalizePath(request.WorkspacePath);
        var id = request.Id is null ? AgentIds.FromWorkspace(path) : AgentIds.Validate(request.Id);
        var catalog = AgentIds.Validate(request.Catalog ?? "default");
        if (request.Persona is not null)
        {
            PersonaNames.Validate(request.Persona);
        }
        ValidateWorkspace(path);
        // Read the profile before reserving or persisting an invalid registration.
        var knowledge = new RepositoryKnowledge(path);
        _ = knowledge.Identity;
        await _changes.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_agents.TryGetValue(id, out var existing))
            {
                if (request.Catalog is not null && existing.Registration.WorkspacePath.Equals(path, PathComparison)
                    && (request.Persona is null || request.Persona == existing.Registration.Persona))
                {
                    var memberships = WithMembership(catalog, id);
                    await SaveAsync(_agents.Values.Select(a => a.Registration), cancellationToken, memberships);
                    _catalogs = memberships;
                    return View(existing);
                }
                throw new AgentRegistryException(409, $"Agent ID '{id}' is already registered. Supply a different ID.");
            }
            if (_agents.Values.Any(a => a.Registration.WorkspacePath.Equals(path, PathComparison)))
            {
                throw new AgentRegistryException(409, "This repository folder is already registered.");
            }
            var registration = new AgentRegistration(id, path, request.Persona);
            var agent = await StartAgentAsync(registration, cancellationToken);
            var catalogs = WithMembership(catalog, id);
            try
            {
                await SaveAsync(_agents.Values.Select(a => a.Registration).Append(registration), cancellationToken, catalogs);
            }
            catch
            {
                await agent.DisposeRuntimeAsync();
                throw;
            }
            _agents[id] = agent;
            _catalogs = catalogs;
            return View(agent);
        }
        finally
        {
            _changes.Release();
        }
    }

    private Dictionary<string, string[]> WithMembership(string catalog, string id) =>
        new(_catalogs, StringComparer.Ordinal)
        {
            [catalog] = (_catalogs.GetValueOrDefault(catalog) ?? []).Append(id).Distinct().Order().ToArray()
        };

    public async Task RemoveAsync(string id, CancellationToken cancellationToken, string? catalog = null)
    {
        await _changes.WaitAsync(cancellationToken);
        try
        {
            if (!_agents.TryGetValue(id, out var agent))
            {
                throw new AgentRegistryException(404, $"Agent '{id}' is not registered.");
            }
            if (catalog is not null)
            {
                var members = RequireCatalog(catalog);
                if (!members.Contains(id))
                {
                    throw new AgentRegistryException(404, $"Agent '{id}' is not in catalog '{catalog}'.");
                }
                var catalogs = new Dictionary<string, string[]>(_catalogs)
                {
                    [catalog] = members.Where(member => member != id).ToArray()
                };
                await SaveAsync(_agents.Values.Select(a => a.Registration), cancellationToken, catalogs);
                _catalogs = catalogs;
                return;
            }
            agent.Status = "stopping";
            try
            {
                await agent.DisposeRuntimeAsync();
                var catalogs = _catalogs.ToDictionary(c => c.Key, c => c.Value.Where(member => member != id).ToArray());
                await SaveAsync(_agents.Values.Where(a => a != agent).Select(a => a.Registration), CancellationToken.None, catalogs);
                _agents.TryRemove(id, out _);
                _catalogs = catalogs;
            }
            catch (Exception exception)
            {
                agent.Status = "failed";
                agent.Error = exception.Message;
                _logger.LogError(exception, "Failed to unregister agent {AgentId}", id);
                throw;
            }
        }
        finally
        {
            _changes.Release();
        }
    }

    private async Task<HostedAgent> StartAgentAsync(AgentRegistration registration, CancellationToken cancellationToken)
    {
        var agent = new HostedAgent(registration);
        IHostedAgentRuntime? runtime = null;
        try
        {
            ValidateWorkspace(registration.WorkspacePath);
            var knowledge = new RepositoryKnowledge(registration.WorkspacePath);
            var card = AgentCards.Create(_baseUrl, registration.Id, knowledge.Identity);
            runtime = _factory(registration, knowledge);
            await runtime.StartAsync(cancellationToken);
            agent.Runtime = runtime;
            agent.Card = card;
            agent.Server = new A2AServer(new RepositoryAgentHandler(runtime,
                _loggerFactory.CreateLogger<RepositoryAgentHandler>()), new InMemoryTaskStore(),
                new ChannelEventNotifier(), _loggerFactory.CreateLogger<A2AServer>(),
                new A2AServerOptions { SupportsStreaming = true });
            agent.Status = "ready";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }
            agent.Status = "failed";
            agent.Error = exception.Message;
            _logger.LogError(exception, "Failed to start agent {AgentId}", registration.Id);
        }
        catch
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }
            throw;
        }
        return agent;
    }

    private void ValidateWorkspace(string path)
    {
        if (_allowedRoots.Length != 0 && !_allowedRoots.Any(root => path.Equals(root, PathComparison)
            || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison)))
        {
            throw new AgentRegistryException(403, "The repository folder is outside the configured allowed roots.");
        }
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new AgentRegistryException(400, "Repository paths through symbolic links or junctions are not supported.");
            }
        }
    }

    private static string NormalizePath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new AgentRegistryException(400, "Repository paths must be absolute.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private async Task SaveAsync(IEnumerable<AgentRegistration> registrations, CancellationToken cancellationToken,
        Dictionary<string, string[]>? catalogs = null)
    {
        var state = new HostConfiguration(_hostId, registrations.OrderBy(a => a.Id).ToArray(), catalogs ?? _catalogs, _port);
        await File.WriteAllTextAsync(StatePath + ".tmp", JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
        File.Move(StatePath + ".tmp", StatePath, overwrite: true);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken);
        try
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
            var errors = new List<Exception>();
            foreach (var agent in _agents.Values)
            {
                agent.Status = "stopping";
                try
                {
                    await agent.DisposeRuntimeAsync();
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Failed to stop agent {AgentId}", agent.Registration.Id);
                    errors.Add(exception);
                }
            }
            _registryLock?.Dispose();
            _registryLock = null;
            if (errors.Count != 0)
            {
                throw new AggregateException("One or more agents failed to stop.", errors);
            }
        }
        finally
        {
            _changes.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    private sealed class HostedAgent(AgentRegistration registration)
    {
        public AgentRegistration Registration { get; } = registration;
        public string Status { get; set; } = "starting";
        public string? Error { get; set; }
        public AgentCard? Card { get; set; }
        public A2AServer? Server { get; set; }
        public IHostedAgentRuntime? Runtime { get; set; }

        public async ValueTask DisposeRuntimeAsync()
        {
            try
            {
                if (Server is not null)
                {
                    await Server.DisposeAsync();
                }
            }
            finally
            {
                if (Runtime is not null)
                {
                    await Runtime.DisposeAsync();
                    Runtime = null;
                }
            }
        }
    }
}
