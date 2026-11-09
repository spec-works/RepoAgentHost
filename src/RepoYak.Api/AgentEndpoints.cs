using A2A;
using A2A.AspNetCore;

namespace RepoYak.Api;

public static class AgentEndpoints
{
    public static void Map(WebApplication app, string? initialWorkspace)
    {
        var registry = app.Services.GetRequiredService<AgentRegistry>();
        var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (AgentRegistryException exception) when (!context.Response.HasStarted)
            {
                await Results.Problem(exception.Message, statusCode: exception.StatusCode).ExecuteAsync(context);
            }
        });
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/catalog", () => Results.Json(registry.Catalog(), contentType: "application/ai-catalog+json"));
        app.MapGet("/catalogs/{name}", (string name) =>
            Results.Json(registry.Catalog(name), contentType: "application/ai-catalog+json"));
        IResult Card(string agentId) => Results.Json(registry.GetCard(agentId),
            A2AJsonUtilities.DefaultOptions, contentType: "application/a2a-agent-card+json");
        app.MapGet("/agents/{agentId}/.well-known/agent-card.json", Card);
        app.MapGet("/agents/{agentId}", Card);
        app.MapA2A(new RoutedAgentHandler(registry, accessor), "/agents/{agentId}/a2a");
        app.MapHttpA2A(new RoutedAgentHandler(registry, accessor), "/agents/{agentId}");

        if (initialWorkspace is null)
        {
            return;
        }

        // Keep the original URLs as aliases for the initial workspace, without advertising a second agent.
        string DefaultAgentId() => registry.List().FirstOrDefault(a =>
            a.WorkspacePath.Equals(initialWorkspace, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))?.Id
            ?? throw new AgentRegistryException(404, "The initial agent is no longer registered.");
        app.MapGet("/.well-known/agent-card.json", () =>
            Results.Json(registry.GetCard(DefaultAgentId()), A2AJsonUtilities.DefaultOptions));
        app.MapA2A(new RoutedAgentHandler(registry, accessor, DefaultAgentId), "/a2a");
        app.MapHttpA2A(new RoutedAgentHandler(registry, accessor, DefaultAgentId), "");
    }
}
