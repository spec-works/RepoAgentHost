using A2A;

namespace RepoYak.Api;

public sealed class RepositoryAgentHandler(IAgentRuntime agent, ILogger<RepositoryAgentHandler> logger) : IAgentHandler
{
    public async Task ExecuteAsync(
        RequestContext context,
        AgentEventQueue eventQueue,
        CancellationToken cancellationToken)
    {
        var userText = context.UserText;
        if (string.IsNullOrWhiteSpace(userText))
        {
            await new MessageResponder(eventQueue, context.ContextId)
                .ReplyAsync("Send a text question or request for this agent.", cancellationToken: cancellationToken);
            return;
        }

        if (userText.Trim().Equals("/new-session", StringComparison.OrdinalIgnoreCase))
        {
            await agent.StartNewSessionAsync(cancellationToken);
            await new MessageResponder(eventQueue, context.ContextId)
                .ReplyAsync("Started a new agent session. Previous task records are unchanged.", cancellationToken: cancellationToken);
            return;
        }

        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        if (!context.IsContinuation)
        {
            await updater.SubmitAsync(cancellationToken: cancellationToken);
        }
        await updater.StartWorkAsync(cancellationToken: cancellationToken);
        string answer;
        try
        {
            answer = await agent.AskAsync(context.TaskId, userText, cancellationToken,
                async (text, token) => await updater.StartWorkAsync(new Message
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                    TaskId = context.TaskId,
                    ContextId = context.ContextId,
                    Role = Role.Agent,
                    Parts = [Part.FromText(text)]
                }, cancellationToken: token));
        }
        catch (PersonaSelectionException exception)
        {
            await updater.FailAsync(new Message
            {
                MessageId = Guid.NewGuid().ToString("N"),
                TaskId = context.TaskId,
                ContextId = context.ContextId,
                Role = Role.Agent,
                Parts = [Part.FromText(exception.Message)]
            }, cancellationToken: cancellationToken);
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Agent execution failed for task {TaskId}", context.TaskId);
            await updater.FailAsync(new Message
            {
                MessageId = Guid.NewGuid().ToString("N"),
                TaskId = context.TaskId,
                ContextId = context.ContextId,
                Role = Role.Agent,
                Parts = [Part.FromText("Agent execution failed. Check the host logs for details.")]
            }, cancellationToken: cancellationToken);
            return;
        }
        await updater.AddArtifactAsync(
            [Part.FromText(answer)], name: "Result", cancellationToken: cancellationToken);
        await updater.CompleteAsync(cancellationToken: cancellationToken);
    }
}
