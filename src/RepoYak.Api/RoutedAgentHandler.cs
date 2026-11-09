using A2A;

namespace RepoYak.Api;

public sealed class RoutedAgentHandler(AgentRegistry registry, IHttpContextAccessor accessor, Func<string>? defaultAgentId = null)
    : IA2ARequestHandler
{
    private string ResolveAgentId() =>
        defaultAgentId?.Invoke() ?? accessor.HttpContext?.Request.RouteValues["agentId"]?.ToString()
        ?? throw new InvalidOperationException("Missing agent route ID.");

    private IA2ARequestHandler Handler => registry.GetHandler(ResolveAgentId());

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken) =>
        Handler.SendMessageAsync(request, cancellationToken);
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken cancellationToken) =>
        Handler.SendStreamingMessageAsync(request, cancellationToken);
    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken) =>
        Handler.GetTaskAsync(request, cancellationToken);
    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken) =>
        Handler.ListTasksAsync(request, cancellationToken);
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken cancellationToken) =>
        Handler.CancelTaskAsync(request, cancellationToken);
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken cancellationToken) =>
        Handler.SubscribeToTaskAsync(request, cancellationToken);
    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, CancellationToken cancellationToken) =>
        Handler.CreateTaskPushNotificationConfigAsync(config, cancellationToken);
    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken cancellationToken) =>
        Handler.GetTaskPushNotificationConfigAsync(request, cancellationToken);
    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, CancellationToken cancellationToken) =>
        Handler.ListTaskPushNotificationConfigsAsync(request, cancellationToken);
    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken cancellationToken) =>
        Handler.DeleteTaskPushNotificationConfigAsync(request, cancellationToken);
    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken cancellationToken) =>
        Handler.GetExtendedAgentCardAsync(request, cancellationToken);
}
