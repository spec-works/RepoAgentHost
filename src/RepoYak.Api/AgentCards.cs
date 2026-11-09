using A2A;

namespace RepoYak.Api;

public static class AgentCards
{
    public static AgentCard Create(string baseUrl, string id, RepositoryIdentity identity) => new()
    {
        Name = identity.Name,
        Description = identity.Description,
        Version = "0.2.0",
        SupportedInterfaces =
        [
            new AgentInterface
            {
                Url = $"{baseUrl}/agents/{id}",
                ProtocolBinding = "HTTP+JSON",
                ProtocolVersion = "1.0"
            },
            new AgentInterface
            {
                Url = $"{baseUrl}/agents/{id}/a2a",
                ProtocolBinding = "JSONRPC",
                ProtocolVersion = "1.0"
            }
        ],
        Capabilities = new AgentCapabilities { Streaming = true },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills =
        [
            new AgentSkill
            {
                Id = "agentic-workspace",
                Name = identity.Name,
                Description = "An agentic assistant grounded in this workspace's knowledge, instructions, and skills.",
                Tags = ["agent", "knowledge", "skills", "tools"],
                InputModes = ["text/plain"],
                OutputModes = ["text/plain"]
            }
        ]
    };
}
