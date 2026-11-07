using System.Net;
using System.Text.Json;
using A2A;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using System.IO.Pipes;

namespace RepoAgentHost.Api.Tests;

public sealed class AgentRegistryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RepoAgentRegistry-" + Guid.NewGuid().ToString("N"));
    private readonly List<AgentRegistry> _registries = [];
    private readonly Dictionary<string, FakeRuntime> _runtimes = [];
    private string StateDirectory => Path.Combine(_root, "state");
    private string InitialWorkspace => Path.Combine(_root, "Portfolio");

    public Task InitializeAsync()
    {
        CreateWorkspace("Portfolio");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var registry in _registries)
        {
            await registry.DisposeAsync();
        }
        Directory.Delete(_root, recursive: true);
    }

    private string CreateWorkspace(string name, string? parent = null)
    {
        var path = Path.Combine(parent ?? _root, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        File.WriteAllText(Path.Combine(path, "README.md"), $"# {name}\n\nKnowledge for {name}.");
        return path;
    }

    private AgentRegistry CreateRegistry(string[]? allowedRoots = null, string? failingId = null)
    {
        var registry = new AgentRegistry(InitialWorkspace, StateDirectory, allowedRoots ?? [],
            "http://127.0.0.1:5080", (registration, _) =>
            {
                var runtime = new FakeRuntime(registration.Id, registration.Id == failingId);
                _runtimes[registration.Id] = runtime;
                return runtime;
            }, NullLoggerFactory.Instance);
        _registries.Add(registry);
        return registry;
    }

    [Theory]
    [InlineData("Portfolio", "portfolio")]
    [InlineData("AI Catalog", "ai-catalog")]
    [InlineData("my_repo.v2", "my-repo-v2")]
    public void FolderNamesProduceFriendlyIds(string folder, string expected)
    {
        Assert.Equal(expected, AgentIds.FromWorkspace(Path.Combine(_root, folder)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Portfolio")]
    [InlineData("../portfolio")]
    [InlineData("two--hyphens")]
    public void InvalidExplicitIdsAreRejected(string id)
    {
        Assert.Equal(400, Assert.Throws<AgentRegistryException>(() => AgentIds.Validate(id)).StatusCode);
    }

    [Fact]
    public async Task CatalogMembershipsShareOneRuntimeAndSurviveRestart()
    {
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        var runtime = _runtimes["portfolio"];
        var handler = registry.GetHandler("portfolio");
        await handler.SendMessageAsync(Request("remember", "caller"), CancellationToken.None);
        var view = await registry.AddAsync(new(InitialWorkspace, Catalog: "support"), CancellationToken.None);
        Assert.Equal(new[] { "default", "support" }, view.Catalogs);
        Assert.Same(runtime, _runtimes["portfolio"]);
        Assert.Same(handler, registry.GetHandler("portfolio"));
        Assert.Single(runtime.History);
        await registry.AddAsync(new(InitialWorkspace, Catalog: "support"), CancellationToken.None);
        Assert.Single(registry.List("support"));
        await registry.RemoveAsync("portfolio", CancellationToken.None, "default");
        Assert.Empty(registry.List("default"));
        Assert.Single(registry.List("support"));
        Assert.False(runtime.Disposed);
        await registry.DisposeAsync();
        var restarted = CreateRegistry();
        await restarted.StartAsync(CancellationToken.None);
        Assert.Empty(restarted.List("default"));
        Assert.Equal("portfolio", Assert.Single(restarted.List("support")).Id);
        await restarted.RemoveAsync("portfolio", CancellationToken.None, "support");
        Assert.Empty(restarted.List("support"));
        Assert.Single(restarted.List());
        Assert.NotNull(restarted.GetCard("portfolio"));
        await restarted.RemoveAsync("portfolio", CancellationToken.None);
        Assert.Empty(restarted.List());
    }

    [Fact]
    public async Task GlobalRemovalRemovesMembershipFromEveryCatalog()
    {
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        await registry.AddAsync(new(InitialWorkspace, Catalog: "support"), CancellationToken.None);
        await registry.RemoveAsync("portfolio", CancellationToken.None);
        Assert.Empty(registry.List("default"));
        Assert.Empty(registry.List("support"));
        Assert.True(_runtimes["portfolio"].Disposed);
    }

    [Fact]
    public async Task MissingCatalogsAndNonmembersFailWithoutChangingRegistrations()
    {
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        Assert.Equal(404, Assert.Throws<AgentRegistryException>(() => registry.List("missing")).StatusCode);
        Assert.Equal(404, Assert.Throws<AgentRegistryException>(() => registry.Catalog("missing")).StatusCode);
        await registry.AddAsync(new(CreateWorkspace("Other"), Catalog: "support"), CancellationToken.None);
        Assert.Equal(404, (await Assert.ThrowsAsync<AgentRegistryException>(() =>
            registry.RemoveAsync("portfolio", CancellationToken.None, "support"))).StatusCode);
        Assert.Equal(2, registry.List().Count);
    }

    [Fact]
    public async Task CatalogMembershipCannotChangeAnAgentsWorkspaceOrPersona()
    {
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<AgentRegistryException>(() => registry.AddAsync(
            new(CreateWorkspace("Other"), "portfolio", Catalog: "support"), CancellationToken.None));
        await Assert.ThrowsAsync<AgentRegistryException>(() => registry.AddAsync(
            new(InitialWorkspace, Persona: "margo", Catalog: "support"), CancellationToken.None));
        Assert.Single(registry.List());
    }

    [Fact]
    public async Task LegacyRegistryIsMigratedInPlaceWithoutChangingIdentity()
    {
        Directory.CreateDirectory(StateDirectory);
        var hostId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(StateDirectory, "agents.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            hostId,
            agents = new[] { new { id = "portfolio", workspacePath = InitialWorkspace } }
        }));
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(hostId, saved.RootElement.GetProperty("hostId").GetString());
        Assert.Equal("portfolio", saved.RootElement.GetProperty("catalogs").GetProperty("default")[0].GetString());
        Assert.Single(registry.List("default"));
    }

    [Fact]
    public async Task ExplicitConfigStoresPortAndMembershipsAndIsLockedAgainstAnotherHost()
    {
        var config = Path.Combine(_root, "configuration", "host.json");
        AgentRegistry NewRegistry()
        {
            var registry = new AgentRegistry(null, StateDirectory, [], "http://127.0.0.1:5081",
                (registration, _) => new FakeRuntime(registration.Id, false), NullLoggerFactory.Instance, config, 5081);
            _registries.Add(registry);
            return registry;
        }
        var first = NewRegistry();
        await first.StartAsync(CancellationToken.None);
        await first.AddAsync(new(InitialWorkspace, Catalog: "support"), CancellationToken.None);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(config));
        Assert.Equal(5081, saved.RootElement.GetProperty("port").GetInt32());
        Assert.True(saved.RootElement.GetProperty("catalogs").TryGetProperty("support", out _));
        Assert.False(File.Exists(Path.Combine(StateDirectory, "agents.json")));
        await Assert.ThrowsAsync<IOException>(() => NewRegistry().StartAsync(CancellationToken.None));
        await first.DisposeAsync();
        var restarted = NewRegistry();
        await restarted.StartAsync(CancellationToken.None);
        Assert.Single(restarted.List("support"));
    }

    [Fact]
    public async Task InvalidCatalogReferencesAreRejectedBeforeStartingAgents()
    {
        Directory.CreateDirectory(StateDirectory);
        await File.WriteAllTextAsync(Path.Combine(StateDirectory, "agents.json"), JsonSerializer.Serialize(new
        {
            hostId = Guid.NewGuid().ToString("N"),
            agents = Array.Empty<object>(),
            catalogs = new { support = new[] { "missing-agent" } }
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRegistry().StartAsync(CancellationToken.None));
        Assert.Empty(_runtimes);
    }

    [Theory]
    [InlineData("add", "workspace", "--catalog", "support")]
    [InlineData("--catalog", "support", "add", "workspace")]
    [InlineData("list", "--catalog", "support")]
    [InlineData("remove", "portfolio", "--catalog", "support")]
    public void CliCatalogOptionWorksOnRegistrationAndMembershipCommands(params string[] arguments)
    {
        Assert.Equal("support", AgentAdmin.ParseCommand(arguments).Catalog);
    }

    [Theory]
    [InlineData("stop", "--catalog", "support")]
    [InlineData("list", "--catalog")]
    [InlineData("list", "--catalog", "../support")]
    [InlineData("list", "--catalog", "support", "--catalog", "other")]
    public void InvalidCatalogOptionsAreRejected(params string[] arguments)
    {
        Assert.Throws<AgentRegistryException>(() => AgentAdmin.ParseCommand(arguments));
    }

    [Fact]
    public void ConfigAndPortOptionsAreRemovedAndConfigSelectsHostPort()
    {
        var config = Path.Combine(_root, "host.json");
        File.WriteAllText(config, """{"port":5099}""");
        var options = RepoAgentHost.Api.HostOptions.Parse(["--config", config, "list", "--catalog", "support"]);
        Assert.Equal(5099, options.Port);
        Assert.Equal(new[] { "list", "--catalog", "support" }, options.Arguments);
        Assert.Equal(config + ".state", options.StateDirectory);
        Assert.Equal(5100, RepoAgentHost.Api.HostOptions.Parse(["start", "--port", "5100", "--config", config]).Port);
        Assert.NotEqual(AgentAdmin.PipeName(5099), AgentAdmin.PipeName(5099, config));
        Assert.NotEqual(AgentAdmin.PipeName(5099, config), AgentAdmin.PipeName(5099, config + "other"));
    }

    [Theory]
    [InlineData("start", "--port", "0")]
    [InlineData("start", "--port", "65536")]
    [InlineData("start", "--port", "abc")]
    [InlineData("start", "--config")]
    [InlineData("start", "--config", "")]
    [InlineData("start", "--config", "one", "--config", "two")]
    [InlineData("start", "--port", "5080", "--port", "5081")]
    public void InvalidCommonOptionsAreRejected(params string[] arguments)
    {
        Assert.Throws<AgentRegistryException>(() => RepoAgentHost.Api.HostOptions.Parse(arguments));
    }

    [Fact]
    public async Task HostWithoutInitialWorkspacePersistsEmptyRegistryAndRestoresRegistrations()
    {
        AgentRegistry NewRegistry()
        {
            var registry = new AgentRegistry(null, StateDirectory, [], "http://127.0.0.1:5080",
                (registration, _) => new FakeRuntime(registration.Id, false), NullLoggerFactory.Instance);
            _registries.Add(registry);
            return registry;
        }
        var first = NewRegistry();
        await first.StartAsync(CancellationToken.None);
        Assert.Empty(first.List());
        Assert.True(File.Exists(Path.Combine(StateDirectory, "agents.json")));
        var originalCatalog = JsonSerializer.Serialize(first.Catalog());
        await first.DisposeAsync();
        var second = NewRegistry();
        await second.StartAsync(CancellationToken.None);
        Assert.Equal(originalCatalog, JsonSerializer.Serialize(second.Catalog()));
        await second.AddAsync(new(InitialWorkspace), CancellationToken.None);
        await second.DisposeAsync();
        var third = NewRegistry();
        await third.StartAsync(CancellationToken.None);
        Assert.Equal("portfolio", Assert.Single(third.List()).Id);
    }

    [Fact]
    public async Task RegistrationsPersistAndRemovedAgentsStayRemovedAfterRestart()
    {
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        var secondPath = CreateWorkspace("AI Catalog");
        var view = await registry.AddAsync(new(secondPath), CancellationToken.None);
        Assert.Equal("ai-catalog", view.Id);
        Assert.Equal("ready", view.Status);
        await registry.RemoveAsync("portfolio", CancellationToken.None);
        await registry.DisposeAsync();

        var restarted = CreateRegistry();
        await restarted.StartAsync(CancellationToken.None);
        Assert.Equal("ai-catalog", Assert.Single(restarted.List()).Id);
        Assert.EndsWith("/agents/ai-catalog", restarted.GetCard("ai-catalog").SupportedInterfaces[0].Url);
        Assert.Equal("HTTP+JSON", restarted.GetCard("ai-catalog").SupportedInterfaces[0].ProtocolBinding);
        Assert.True(restarted.GetCard("ai-catalog").Capabilities.Streaming);
    }

    [Fact]
    public async Task DuplicateIdsAndWorkspacesAreRejectedWithoutRenaming()
    {
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        var sameName = CreateWorkspace("Portfolio", Path.Combine(_root, "another"));
        Assert.Equal(409, (await Assert.ThrowsAsync<AgentRegistryException>(() =>
            registry.AddAsync(new(sameName), CancellationToken.None))).StatusCode);
        var explicitId = await registry.AddAsync(new(sameName, "another-portfolio"), CancellationToken.None);
        Assert.Equal("another-portfolio", explicitId.Id);
        Assert.Equal(409, (await Assert.ThrowsAsync<AgentRegistryException>(() =>
            registry.AddAsync(new(InitialWorkspace, "duplicate-workspace"), CancellationToken.None))).StatusCode);
    }

    [Fact]
    public async Task AllowedRootsUsePathBoundaries()
    {
        var registry = CreateRegistry([InitialWorkspace]);
        await registry.StartAsync(CancellationToken.None);
        var outside = CreateWorkspace("Portfolio-other");
        Assert.Equal(403, (await Assert.ThrowsAsync<AgentRegistryException>(() =>
            registry.AddAsync(new(outside), CancellationToken.None))).StatusCode);
    }

    [Fact]
    public async Task RebrandingPreservesLegacyManagementAndCatalogIdentifiers()
    {
        Assert.Matches("^agentdock-[0-9A-F]{16}-5080$", AgentAdmin.PipeName(5080));
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        using var catalog = JsonDocument.Parse(JsonSerializer.Serialize(registry.Catalog()));
        var host = catalog.RootElement.GetProperty("host");
        Assert.Equal("RepoAgentHost", host.GetProperty("displayName").GetString());
        var hostId = host.GetProperty("identifier").GetString()!;
        Assert.StartsWith("urn:agentdock:host:", hostId);
        var entry = Assert.Single(catalog.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal($"urn:agentdock:{hostId["urn:agentdock:host:".Length..]}:agent:portfolio",
            entry.GetProperty("identifier").GetString());
    }

    [Fact]
    public async Task FailedAgentsAreVisibleToManagementButNotAdvertised()
    {
        var registry = CreateRegistry(failingId: "broken");
        await registry.StartAsync(CancellationToken.None);
        var failed = await registry.AddAsync(new(CreateWorkspace("Broken")), CancellationToken.None);
        Assert.Equal("failed", failed.Status);
        Assert.Contains("Simulated startup failure", failed.Error);
        Assert.Equal(503, Assert.Throws<AgentRegistryException>(() => registry.GetHandler("broken")).StatusCode);
        using var catalog = JsonDocument.Parse(JsonSerializer.Serialize(registry.Catalog()));
        Assert.Single(catalog.RootElement.GetProperty("entries").EnumerateArray());
        Assert.True(_runtimes["broken"].Disposed);
    }

    [Fact]
    public async Task AgentsHaveSeparateHistoryTaskStoresAndSessionRotation()
    {
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        await registry.AddAsync(new(CreateWorkspace("Other")), CancellationToken.None);
        var first = registry.GetHandler("portfolio");
        var other = registry.GetHandler("other");
        var response = await first.SendMessageAsync(Request("remember-one", "shared-context"), CancellationToken.None);
        var task = Assert.IsType<AgentTask>(response.Task);
        Assert.Equal(TaskState.Completed, task.Status.State);
        await first.SendMessageAsync(Request("remember-two", "different-context"), CancellationToken.None);
        Assert.Equal(2, _runtimes["portfolio"].History.Count);
        Assert.Empty(_runtimes["other"].History);
        await Assert.ThrowsAsync<A2AException>(() => other.GetTaskAsync(new GetTaskRequest { Id = task.Id }, CancellationToken.None));
        await other.SendMessageAsync(Request("other-work", "shared-context"), CancellationToken.None);
        var reset = await first.SendMessageAsync(Request("/new-session", "control"), CancellationToken.None);
        Assert.NotNull(reset.Message);
        Assert.Empty(_runtimes["portfolio"].History);
        Assert.Single(_runtimes["other"].History);
        Assert.Equal(task.Id, (await first.GetTaskAsync(new GetTaskRequest { Id = task.Id }, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task DynamicHttpRoutesResolveCardsAndUnknownAgentsAndExposeNoAdminApi()
    {
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(registry);
        builder.Services.AddHttpContextAccessor();
        await using var app = builder.Build();
        AgentEndpoints.Map(app, InitialWorkspace);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            await registry.AddAsync(new(CreateWorkspace("Dynamic")), CancellationToken.None);
            var card = await http.GetAsync("/agents/dynamic/.well-known/agent-card.json");
            Assert.Equal(HttpStatusCode.OK, card.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/agents/dynamic")).StatusCode);
            using var json = JsonDocument.Parse(await card.Content.ReadAsStringAsync());
            Assert.Equal("Dynamic", json.RootElement.GetProperty("name").GetString());
            var client = new A2AClient(new Uri(address + "/agents/dynamic/a2a"), http);
            var result = await client.SendMessageAsync(Request("dynamic-work", "caller"), CancellationToken.None);
            var task = Assert.IsType<AgentTask>(result.Task);
            Assert.Equal(TaskState.Completed, task.Status.State);
            Assert.Equal("Delivered by dynamic", task.Artifacts![0].Parts[0].Text);
            var otherClient = new A2AClient(new Uri(address + "/agents/portfolio/a2a"), http);
            await Assert.ThrowsAsync<A2AException>(() =>
                otherClient.GetTaskAsync(new GetTaskRequest { Id = task.Id }, CancellationToken.None));
            var restClient = new A2AHttpJsonClient(new Uri(address + "/agents/dynamic"), http);
            var streamed = new List<StreamResponse>();
            await foreach (var item in restClient.SendStreamingMessageAsync(Request("stream-work", "caller"), CancellationToken.None))
            {
                streamed.Add(item);
            }
            Assert.Contains(streamed, item => item.Task?.Status.State == TaskState.Submitted);
            Assert.Contains(streamed, item => item.StatusUpdate?.Status.Message?.Parts[0].Text == "Working on stream-work");
            Assert.Contains(streamed, item => item.ArtifactUpdate?.Artifact.Parts[0].Text == "Delivered by dynamic");
            Assert.Equal(TaskState.Completed, streamed.Last().StatusUpdate!.Status.State);
            var restTaskId = streamed.First(item => item.Task is not null).Task!.Id;
            Assert.Equal(restTaskId, (await restClient.GetTaskAsync(new GetTaskRequest { Id = restTaskId }, CancellationToken.None)).Id);
            var catalog = await http.GetAsync("/catalog");
            Assert.Equal("application/ai-catalog+json", catalog.Content.Headers.ContentType?.MediaType);
            await registry.AddAsync(new(Path.Combine(_root, "Dynamic"), Catalog: "support"), CancellationToken.None);
            await registry.AddAsync(new(Path.Combine(_root, "Dynamic"), Catalog: "engineering"), CancellationToken.None);
            foreach (var name in new[] { "support", "engineering" })
            {
                var named = await http.GetAsync("/catalogs/" + name);
                Assert.Equal("application/ai-catalog+json", named.Content.Headers.ContentType?.MediaType);
                using var namedJson = JsonDocument.Parse(await named.Content.ReadAsStringAsync());
                var entry = Assert.Single(namedJson.RootElement.GetProperty("entries").EnumerateArray());
                Assert.EndsWith("/agents/dynamic/.well-known/agent-card.json", entry.GetProperty("url").GetString());
            }
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/catalogs/missing")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/admin/agents")).StatusCode);
            await registry.RemoveAsync("dynamic", CancellationToken.None);
            Assert.Equal(HttpStatusCode.NotFound,
                (await http.GetAsync("/agents/dynamic/.well-known/agent-card.json")).StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task StoppingAgentCancelsAndJoinsOutstandingWork()
    {
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        var runtime = _runtimes["portfolio"];
        runtime.WaitForCancellation = true;
        var pending = registry.GetHandler("portfolio").SendMessageAsync(Request("long-work", "caller"), CancellationToken.None);
        await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await registry.RemoveAsync("portfolio", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(runtime.CancellationObserved);
        Assert.True(runtime.Disposed);
        Assert.Empty(registry.List());
        // Disposal can interrupt the waiting request or return its terminal task, depending on timing.
        var error = await Record.ExceptionAsync(async () =>
        {
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(Assert.IsType<AgentTask>(response.Task).Status.State,
                new[] { TaskState.Canceled, TaskState.Failed });
        });
        Assert.True(error is null or OperationCanceledException or A2AException, error?.ToString());
    }

    [Fact]
    public async Task PersonaPersistsWithRegistrationAndIsExposedByList()
    {
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        var path = CreateWorkspace("Assistant");
        var view = await registry.AddAsync(new(path, Persona: "margo"), CancellationToken.None);
        Assert.Equal("margo", view.Persona);
        await registry.DisposeAsync();
        var restarted = CreateRegistry();
        await restarted.StartAsync(CancellationToken.None);
        Assert.Equal("margo", restarted.List().Single(a => a.Id == "assistant").Persona);
        Assert.Null(restarted.List().Single(a => a.Id == "portfolio").Persona);
    }

    [Theory]
    [InlineData("add", "workspace", "--persona", "margo", "--id", "assistant")]
    [InlineData("add", "workspace", "--id", "assistant", "--persona", "margo")]
    public void PersonaAndFriendlyIdOptionsWorkInEitherOrder(params string[] arguments)
    {
        var command = AgentAdmin.ParseCommand(arguments);
        Assert.Equal("margo", command.Persona);
        Assert.Equal("assistant", command.Id);
        Assert.Equal("workspace", command.WorkspacePath);
    }

    [Theory]
    [InlineData("add", "workspace", "--persona")]
    [InlineData("add", "workspace", "--persona", "margo", "--persona", "margo")]
    [InlineData("add", "workspace", "--persona", "")]
    [InlineData("add", "workspace", "--persona", "..\\margo.agent.md")]
    public void InvalidPersonaArgumentsAreRejected(params string[] arguments)
    {
        Assert.Throws<AgentRegistryException>(() => AgentAdmin.ParseCommand(arguments));
    }

    [Fact]
    public async Task CliCommandsAddListAndRemoveAgentsWithoutStoppingTheHost()
    {
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        using var lifetime = new FakeLifetime();
        using var admin = new AgentAdmin(registry, "unused", NullLogger<AgentAdmin>.Instance, lifetime);
        var command = AgentAdmin.ParseCommand(["add", CreateWorkspace("New Agent"), "--id", "friendly"]);
        Assert.True((await admin.DispatchAsync(command, CancellationToken.None)).Success);
        var list = await admin.DispatchAsync(AgentAdmin.ParseCommand(["list"]), CancellationToken.None);
        Assert.Equal(2, list.Agents!.Length);
        await admin.DispatchAsync(AgentAdmin.ParseCommand(["remove", "friendly"]), CancellationToken.None);
        Assert.Equal("portfolio", Assert.Single(registry.List()).Id);
        Assert.True(Directory.Exists(command.WorkspacePath));
        Assert.True(_runtimes["friendly"].Disposed);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Theory]
    [InlineData("start", "workspace")]
    [InlineData("stop", "portfolio")]
    [InlineData("remove")]
    [InlineData("add")]
    [InlineData("add", "--background")]
    public void OldAgentCommandsAndIncompleteRegistrationCommandsAreRejected(params string[] arguments)
    {
        Assert.Throws<AgentRegistryException>(() => AgentAdmin.ParseCommand(arguments));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("serve")]
    [InlineData("start", "workspace")]
    public void HostStartsInForegroundByDefault(params string[] arguments)
    {
        var start = HostCommands.ParseStart(arguments);
        Assert.False(start.Background);
        Assert.Equal(arguments.Length == 2 ? Path.GetFullPath("workspace") : null, start.WorkspacePath);
    }

    [Theory]
    [InlineData("start", "--background", "workspace")]
    [InlineData("start", "workspace", "--background")]
    public void BackgroundSwitchAndWorkspaceWorkInEitherOrder(params string[] arguments)
    {
        var start = HostCommands.ParseStart(arguments);
        Assert.True(start.Background);
        Assert.Equal(Path.GetFullPath("workspace"), start.WorkspacePath);
    }

    [Theory]
    [InlineData("start", "--background", "--background")]
    [InlineData("start", "workspace", "--id", "agent")]
    [InlineData("start", "workspace", "--persona", "margo")]
    [InlineData("start", "one", "two")]
    [InlineData("start", "")]
    [InlineData("serve", "--background")]
    public void InvalidHostStartArgumentsAreRejected(params string[] arguments)
    {
        Assert.Throws<AgentRegistryException>(() => HostCommands.ParseStart(arguments));
    }

    [Fact]
    public async Task HostStopRepliesBeforeRequestingShutdownAndPreservesRegistrations()
    {
        var registry = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        using var lifetime = new FakeLifetime();
        var pipeName = "RepoAgentHost-test-" + Guid.NewGuid().ToString("N");
        using var admin = new AgentAdmin(registry, pipeName, NullLogger<AgentAdmin>.Instance, lifetime);
        await admin.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("""{"command":"stop"}""");
            var reply = JsonSerializer.Deserialize<AgentAdmin.AdminReply>(
                (await reader.ReadLineAsync(timeout.Token))!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.True(reply!.Success);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Task.Delay(Timeout.InfiniteTimeSpan, lifetime.ApplicationStopping).WaitAsync(timeout.Token));
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.Equal("portfolio", Assert.Single(registry.List()).Id);
            await registry.DisposeAsync();
            var restarted = CreateRegistry();
            await restarted.StartAsync(CancellationToken.None);
            Assert.Equal("portfolio", Assert.Single(restarted.List()).Id);
        }
        finally
        {
            await admin.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }

    private static SendMessageRequest Request(string text, string context) => new()
    {
        Message = new Message
        {
            MessageId = Guid.NewGuid().ToString("N"),
            ContextId = context,
            Role = Role.User,
            Parts = [Part.FromText(text)]
        }
    };

    private sealed class FakeRuntime(string id, bool failStart) : IHostedAgentRuntime
    {
        public List<string> History { get; } = [];
        public bool Disposed { get; private set; }
        public bool WaitForCancellation { get; set; }
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken) =>
            failStart ? Task.FromException(new InvalidOperationException("Simulated startup failure")) : Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
        public async Task<string> AskAsync(string taskId, string question, CancellationToken cancellationToken,
            Func<string, CancellationToken, Task>? reportProgress = null)
        {
            History.Add(question);
            Entered.TrySetResult();
            if (reportProgress is not null)
            {
                await reportProgress($"Working on {question}", cancellationToken);
            }
            if (WaitForCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }
            return $"Delivered by {id}";
        }
        public Task StartNewSessionAsync(CancellationToken cancellationToken)
        {
            History.Clear();
            return Task.CompletedTask;
        }
    }
}
