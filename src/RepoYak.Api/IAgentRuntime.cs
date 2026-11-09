namespace RepoYak.Api;

public interface IAgentRuntime
{
    Task<string> AskAsync(string taskId, string question, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? reportProgress = null);
    Task StartNewSessionAsync(CancellationToken cancellationToken);
}
