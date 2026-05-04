using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;

public static class AGUIEndpoint
{
    /// <summary>
    /// AGUIEndpointRouteBuilderExtensions.MapAGUI() does not allow per-requrest agent selection, 
    /// so we need a special agent that forwards the request to the actually requested agent.
    /// </summary>
    public static IEndpointConventionBuilder MapAGUIViaHttpRoutingAgent(this WebApplication app)
    {
        return app.MapAGUI("/agents/routed/{alias}/agui", new HttpContextRoutingAgent(
            httpContextAccessor: app.Services.GetRequiredService<IHttpContextAccessor>(), 
            resolveAgent: async httpContext =>
            {
                var alias = httpContext.Request.RouteValues["alias"]?.ToString() ?? string.Empty;
                var agents = httpContext.RequestServices.GetRequiredService<IAgentProvider>();
                var sessionStore = httpContext.RequestServices.GetRequiredService<AgentSessionStore>();

                await Task.Yield();//simulating loading
                var agent = agents.Get(alias);
                var hostedAgent = new AIHostAgent(agent!, sessionStore);
                return hostedAgent;
            }));
    }

    /// <summary>
    /// AGUIEndpointRouteBuilderExtensions.MapAGUI() captures a single instance, 
    /// so we need to also forward to a request-scoped session store.
    /// </summary>
    public static IServiceCollection AddAGUISessionStore(this IServiceCollection services)
    {
        services.AddKeyedSingleton<AgentSessionStore, HttpContextRoutingSessionStore>("HttpContextRoutingAgent");
        return services;
    }

    class HttpContextRoutingSessionStore(IHttpContextAccessor httpContextAccessor) : AgentSessionStore
    {
        public override ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
        {
            var httpContext = httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No HttpContext available");
            var requestSessionStore = httpContext.RequestServices.GetRequiredService<AgentSessionStore>();
            return requestSessionStore.GetSessionAsync(agent, conversationId, cancellationToken);
        }

        public override ValueTask SaveSessionAsync(AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
        {
            var httpContext = httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No HttpContext available");
            var requestSessionStore = httpContext.RequestServices.GetRequiredService<AgentSessionStore>();
            return requestSessionStore.SaveSessionAsync(agent, conversationId, session, cancellationToken);
        }
    }

    /// <summary>
    /// Suggested way to do per-request agent selection (https://github.com/microsoft/agent-framework/pull/3162#issuecomment-3754459882).
    /// </summary>
    class HttpContextRoutingAgent(IHttpContextAccessor httpContextAccessor, Func<HttpContext, ValueTask<AIHostAgent>> resolveAgent) : AIAgent
    {
        protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            var agent = await GetAgent();
            return await agent.CreateSessionAsync(cancellationToken);
        }

        protected override async ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        {
            var agent = await GetAgent();
            return await agent.DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken);
        }

        protected override async ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        {
            var agent = await GetAgent();
            return await agent.SerializeSessionAsync(session, jsonSerializerOptions, cancellationToken);
        }

        protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            var agent = await GetAgent();
            var conversationId = GetConversationId(options);
            var dedicatedSession = session is null ? await agent.GetOrCreateSessionAsync(conversationId, cancellationToken) : null;

            var response = await agent.RunAsync(
                messages,
                session ?? dedicatedSession,
                options,
                cancellationToken);

            if (dedicatedSession is not null)
            {
                await agent.SaveSessionAsync(conversationId, dedicatedSession, cancellationToken);
            }
            return response;
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var agent = await GetAgent();
            var conversationId = GetConversationId(options);
            var dedicatedSession = session is null ? await agent.GetOrCreateSessionAsync(conversationId, cancellationToken) : null;

            await foreach (var update in agent.RunStreamingAsync(
                messages,
                session ?? dedicatedSession,
                options,
                cancellationToken))
            {
                yield return update;
            }

            if (dedicatedSession is not null)
            {
                await agent.SaveSessionAsync(conversationId, dedicatedSession, cancellationToken);
            }
        }

        private ValueTask<AIHostAgent> GetAgent()
        {
            var httpContext = httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No HttpContext available");
            return resolveAgent(httpContext);
        }

        private static string GetConversationId(AgentRunOptions? options)
        {
            var conversationId = (options as ChatClientAgentRunOptions)?.ChatOptions?.AdditionalProperties?["ag_ui_thread_id"]?.ToString()
                ?? throw new ArgumentNullException("No conversation ID provided ('ag_ui_thread_id').");
            return conversationId;
        }
    }
}