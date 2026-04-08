using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;

public class FileSystemSessionStore : AgentSessionStore
{
    private readonly string pathBase;
    private readonly ILogger<FileSystemSessionStore> logger;

    public FileSystemSessionStore(ILogger<FileSystemSessionStore> logger, string pathBase = "AgentSessions")
    {
        this.logger = logger;
        this.pathBase = pathBase;

    }

    public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        this.logger.LogInformation("Loading session for conversation {conversationId}", conversationId);
        var path = GetPath(conversationId, agent.Id);
        if (!File.Exists(path))
        {
            return await agent.CreateSessionAsync(cancellationToken);
        }

        using var stream = File.OpenRead(path);
        var sessionContent = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: cancellationToken);
        return await agent.DeserializeSessionAsync(sessionContent, cancellationToken: cancellationToken);
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
    {
        this.logger.LogInformation("Saving session for conversation {conversationId}", conversationId);
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        Directory.CreateDirectory(this.pathBase);
        using var stream = File.Create(GetPath(conversationId, agent.Id));
        await JsonSerializer.SerializeAsync(stream, serialized, cancellationToken: cancellationToken);
    }

    private string GetPath(string conversationId, string agentId) =>
        Path.Combine(this.pathBase, $"{agentId}_{conversationId}.json");
}