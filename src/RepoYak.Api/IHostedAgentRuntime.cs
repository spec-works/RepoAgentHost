using Microsoft.Extensions.Hosting;

namespace RepoYak.Api;

public interface IHostedAgentRuntime : IAgentRuntime, IHostedService, IAsyncDisposable;
