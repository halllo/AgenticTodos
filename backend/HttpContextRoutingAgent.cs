using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
/// Suggested way to do per-request agent selection (https://github.com/microsoft/agent-framework/pull/3162#issuecomment-3754459882).
/// </summary>
public class HttpContextRoutingAgent(IHttpContextAccessor httpContextAccessor, Func<HttpContext, ValueTask<AIAgent>> resolveAgent) : AIAgent
{
    public override async ValueTask<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        var agent = await GetAgent();
        return await agent.CreateSessionAsync(cancellationToken);
    }

    public override async ValueTask<AgentSession> DeserializeSessionAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        var agent = await GetAgent();
        return await agent.DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken);
    }

    public override JsonElement SerializeSession(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var agent = GetAgent().GetAwaiter().GetResult();
        return agent.SerializeSession(session, jsonSerializerOptions);
    }

    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var agent = await GetAgent();
        return await agent.RunAsync(messages, session, options, cancellationToken);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var agent = await GetAgent();
        await foreach (var update in agent.RunStreamingAsync(messages, session, options, cancellationToken))
        {
            yield return update;
        }
    }

    private ValueTask<AIAgent> GetAgent()
    {
        var httpContext = httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No HttpContext available");
        return resolveAgent(httpContext);
    }
}