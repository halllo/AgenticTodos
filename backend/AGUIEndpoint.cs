using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

public static class AGUIEndpoint
{
    /// <summary>
    /// Name the routing agent reports, and therefore the DI key MapAGUIServer looks its session store
    /// up under — it resolves the store as <c>GetKeyedService&lt;AgentSessionStore&gt;(agent.Name)</c>.
    /// </summary>
    internal const string RoutedAgentName = "routed";

    /// <summary>
    /// Path prefix the routed endpoint lives under. <see cref="AguiRunErrorMiddleware"/> is scoped to
    /// it, so the two must not be able to drift apart: moving the route while leaving the middleware
    /// behind would silently turn every eager failure back into the invisible HTTP 500 it exists to
    /// prevent.
    /// </summary>
    internal const string RoutedPathPrefix = $"/agents/{RoutedAgentName}";

    private const string RoutedRoutePattern = $"{RoutedPathPrefix}/{{alias}}/agui";

    /// <summary>
    /// AGUIEndpointRouteBuilderExtensions.MapAGUIServer() does not allow per-request agent selection,
    /// so we need a special agent that forwards the request to the actually requested agent.
    /// </summary>
    public static IEndpointConventionBuilder MapAGUIViaHttpRoutingAgent(this WebApplication app)
    {
        return app.MapAGUIServer(RoutedRoutePattern, new HttpContextRoutingAgent(
            httpContextAccessor: app.Services.GetRequiredService<IHttpContextAccessor>(),
            // Asynchronous by design: resolving the agent is a per-request lookup that an
            // implementation may serve from a database or a remote config service.
            resolveAgent: async httpContext =>
            {
                var alias = GetAlias(httpContext);
                var agents = httpContext.RequestServices.GetRequiredService<IAgentProvider>();

                // Resolved per request, so the store may have any lifetime — see AddAGUISessionStore.
                var sessionStore = httpContext.RequestServices.GetRequiredService<AgentSessionStore>();

                // An unknown alias is a client error, not a server fault: throwing here lets
                // AguiRunErrorMiddleware report it as RUN_ERROR with a readable message rather than
                // letting a DI resolution failure become an HTTP 500. AguiClientException specifically,
                // because that is the one exception type whose message the middleware puts on the wire.
                var agent = await agents.GetAsync(alias, httpContext.RequestAborted)
                    ?? throw new AguiClientException($"Unknown agent alias '{alias}'.");

                return new AIHostAgent(agent, sessionStore);
            }))
            // The SDK reads the stream configuration from endpoint metadata (falling back to
            // IOptions<AGUIStreamOptions>), so this is where the app teaches it about the events it
            // cannot derive from Microsoft.Extensions.AI content on its own.
            .WithMetadata(CreateStreamOptions());
    }

    /// <summary>
    /// Maps the app's own <see cref="AIContent"/> types (see <c>AguiClientContent.cs</c>) onto AG-UI
    /// events. This is the extension point the AG-UI server SDK offers for content it does not model
    /// itself — no post-processing of the serialized SSE stream involved.
    /// </summary>
    internal static AGUIStreamOptions CreateStreamOptions() =>
        new AGUIStreamOptions().MapContent(MapClientContent);

    /// <summary>
    /// Applies <see cref="ConfigureAguiJson"/> to the JSON options the AG-UI endpoint (de)serializes
    /// with — <c>Microsoft.AspNetCore.Http.Json.JsonOptions</c>, the minimal-API one the SSE result
    /// flows through, not the MVC namesake.
    /// <para>
    /// An extension rather than two lines in <c>Program.cs</c> because this half is the one that can go
    /// missing unnoticed: without it every <c>STATE_SNAPSHOT</c> and <c>ACTIVITY_SNAPSHOT</c> throws on
    /// <c>rawEvent</c> serialization at request time, and no test that only calls
    /// <see cref="ConfigureAguiJson"/> would notice. Registering through a seam a test can call lets the
    /// app's own wiring be asserted, by resolving <c>IOptions&lt;JsonOptions&gt;</c> from a container
    /// built the same way.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Composes with <c>AddAGUIServer()</c> rather than competing with it: that call registers its own
    /// <c>IConfigureOptions&lt;JsonOptions&gt;</c> (adding the Agent Framework and AG-UI type info
    /// resolvers), and the options system runs every registration in order.
    /// </remarks>
    public static IServiceCollection AddAGUIJson(this IServiceCollection services)
    {
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(
            options => ConfigureAguiJson(options.SerializerOptions));
        return services;
    }

    /// <summary>
    /// Registers every <see cref="AIContent"/> subtype that has to survive JSON polymorphism on the
    /// AG-UI wire: the protocol's own interrupt content types, and this app's client-facing ones.
    /// <para>
    /// This is the other half of the two-step contract in <c>AguiClientContent.cs</c> — the SDK
    /// serializes each response update into the event's <c>rawEvent</c> field, so a content type that
    /// is mapped in <see cref="MapClientContent"/> but missing here throws
    /// <c>NotSupportedException: Runtime type '…' is not supported by polymorphic type 'AIContent'</c>
    /// at request time rather than at build time. Kept next to the mapping, and reachable on its own via
    /// <see cref="AddAGUIJson"/>, so a test can assert both halves agree.
    /// </para>
    /// </summary>
    internal static void ConfigureAguiJson(JsonSerializerOptions options)
    {
        AGUIJsonUtilities.RegisterInterruptContentTypes(options);
        options.AddAIContentType<ConversationStateContent>("agenticTodos.conversationState");
        options.AddAIContentType<McpAppActivityContent>("agenticTodos.mcpAppActivity");
        options.AddAIContentType<EUAIActRiskActivityContent>("agenticTodos.euAiActRiskActivity");
    }

    internal static IEnumerable<BaseEvent>? MapClientContent(AIContent content) => content switch
    {
        ConversationStateContent state =>
        [
            new StateSnapshotEvent { Snapshot = state.Snapshot },
        ],

        McpAppActivityContent app =>
        [
            new ActivitySnapshotEvent
            {
                MessageId = app.MessageId,
                ActivityType = McpAppsActivityType,
                Replace = true,
                // Anonymous types keep the property names verbatim, which is what the frontend reads.
                Content = JsonSerializer.SerializeToElement(new
                {
                    resourceUri = app.ResourceUri,
                    result = app.Result,
                    toolInput = app.ToolInput,
                }),
            },
        ],

        EUAIActRiskActivityContent risk =>
        [
            new ActivitySnapshotEvent
            {
                MessageId = risk.MessageId,
                ActivityType = EUAIActRiskActivityType,
                Replace = true,
                Content = JsonSerializer.SerializeToElement(new
                {
                    risk = risk.Risk,
                    category = risk.Category,
                    reason = risk.Reason,
                }),
            },
        ],

        // Not ours — let the SDK fall through to its own handling.
        _ => null,
    };

    internal const string McpAppsActivityType = "mcp-apps";
    internal const string EUAIActRiskActivityType = "eu-ai-act-risk";

    private static string GetAlias(HttpContext httpContext) =>
        httpContext.Request.RouteValues["alias"]?.ToString() ?? string.Empty;

    /// <summary>
    /// Registers the session store the AG-UI endpoint uses, keyed by <see cref="RoutedAgentName"/>.
    /// <para>
    /// MapAGUIServer resolves the store <b>once at map time</b>, from the root provider
    /// (<c>endpoints.ServiceProvider.GetKeyedService&lt;AgentSessionStore&gt;(agent.Name)</c>), wraps it in
    /// an <c>IsolationKeyScopedAgentSessionStore</c> and hands that to the <c>AIHostAgent</c> it
    /// captures. A store registered as anything other than a singleton therefore cannot be used
    /// directly: a scoped one fails outright with <i>"Cannot resolve scoped service
    /// 'AgentSessionStore' from root provider"</i> at startup.
    /// </para>
    /// <para>
    /// So what the endpoint gets is a singleton stand-in that forwards every call to whatever the
    /// <i>current request's</i> container provides. The real store can then be registered with any
    /// lifetime — scoped included — even though the one in use today is a singleton.
    /// </para>
    /// <para>
    /// The SDK's wrapper is a pass-through here: it only rewrites the session id when a
    /// <c>SessionIsolationKeyProvider</c> is registered, and this app registers none. That also fixes
    /// the app's threat model — sessions are keyed by the bare <c>ThreadId</c> the client sends, so any
    /// caller who knows a thread id can resume it. That is fine for a single-user sample; a multi-user
    /// host would add <c>UseClaimsBasedSessionIsolation(...)</c>, after which the id reaching
    /// <see cref="FileSystemSessionStore"/> becomes <c>{key}::{threadId}</c>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddAGUISessionStore(this IServiceCollection services)
    {
        services.AddKeyedSingleton<AgentSessionStore, HttpContextRoutingSessionStore>(RoutedAgentName);
        return services;
    }

    /// <summary>
    /// Resolves the request's <see cref="AgentSessionStore"/> per call instead of capturing one. See
    /// <see cref="AddAGUISessionStore"/> for why the endpoint cannot hold the real store itself.
    /// </summary>
    /// <remarks>
    /// It forwards to the <i>non-keyed</i> registration, so there is no recursion back into this type.
    /// </remarks>
    internal sealed class HttpContextRoutingSessionStore(IHttpContextAccessor httpContextAccessor) : AgentSessionStore
    {
        public override ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
            => Current().GetSessionAsync(agent, sessionStoreId, cancellationToken);

        public override ValueTask SaveSessionAsync(AIAgent agent, string sessionStoreId, AgentSession session, CancellationToken cancellationToken = default)
            => Current().SaveSessionAsync(agent, sessionStoreId, session, cancellationToken);

        public override ValueTask DeleteSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
            => Current().DeleteSessionAsync(agent, sessionStoreId, cancellationToken);

        private AgentSessionStore Current()
        {
            var httpContext = httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException(
                    "No HttpContext available: the AG-UI session store is only reachable inside a request.");

            return httpContext.RequestServices.GetRequiredService<AgentSessionStore>();
        }
    }

    /// <summary>
    /// Suggested way to do per-request agent selection (https://github.com/microsoft/agent-framework/pull/3162#issuecomment-3754459882).
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than private so the tests can construct it directly — the backend grants
    /// them access via <c>InternalsVisibleTo</c>, same as for <see cref="HttpContextRoutingSessionStore"/>.
    /// </remarks>
    internal sealed class HttpContextRoutingAgent(IHttpContextAccessor httpContextAccessor, Func<HttpContext, ValueTask<AIHostAgent>> resolveAgent) : AIAgent
    {
        private const string ResolvedAgentKey = "AgenticTodos.RoutedAgent";

        /// <summary>
        /// MapAGUIServer resolves the endpoint's session store as
        /// <c>GetKeyedService&lt;AgentSessionStore&gt;(agent.Name)</c>, so this name <b>is</b> the DI key —
        /// leaving it null makes the lookup fall through to the non-keyed registration, which is what
        /// silently defeats <see cref="AddAGUISessionStore"/>. Nothing else in the hosting package reads
        /// <see cref="AIAgent.Name"/>.
        /// </summary>
        public override string? Name => RoutedAgentName;

        /// <summary>
        /// The session store keys persisted sessions by <see cref="AIAgent.Id"/>. The base
        /// implementation returns a fresh <c>Guid</c> per instance, which would (a) orphan every
        /// persisted session on restart and (b) give both aliases the same id — one shared session
        /// file per thread across models. Deriving it from the route keeps sessions stable and
        /// separate per alias.
        /// </summary>
        /// <remarks>
        /// The alias reaches a file name via <see cref="FileSystemSessionStore"/>, so only the shape a
        /// real alias has is accepted; anything else falls back rather than composing a path from
        /// request input. An unknown-but-well-formed alias still fails later, in <c>resolveAgent</c>.
        /// </remarks>
        protected override string? IdCore =>
            httpContextAccessor.HttpContext is { } httpContext &&
            GetAlias(httpContext) is { Length: > 0 } alias &&
            alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                ? $"routed-{alias}"
                : "routed";

        protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => await (await GetAgentAsync()).CreateSessionAsync(cancellationToken);

        protected override async ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => await (await GetAgentAsync()).DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken);

        protected override async ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => await (await GetAgentAsync()).SerializeSessionAsync(session, jsonSerializerOptions, cancellationToken);

        protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            var agent = await GetAgentAsync();
            var conversationId = session is null ? GetConversationId(options) : null;
            var dedicatedSession = conversationId is null ? null : await agent.GetOrCreateSessionAsync(conversationId, cancellationToken);

            var response = await agent.RunAsync(
                messages,
                session ?? dedicatedSession,
                options,
                cancellationToken);

            if (dedicatedSession is not null)
            {
                await agent.SaveSessionAsync(conversationId!, dedicatedSession, cancellationToken);
            }
            return response;
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var agent = await GetAgentAsync();
            var conversationId = session is null ? GetConversationId(options) : null;
            var dedicatedSession = conversationId is null ? null : await agent.GetOrCreateSessionAsync(conversationId, cancellationToken);

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
                await agent.SaveSessionAsync(conversationId!, dedicatedSession, cancellationToken);
            }
        }

        /// <summary>
        /// Resolves the agent the current request addresses, once per request: the SDK calls into this
        /// instance several times per run (session create/serialize plus the run itself), and the
        /// resolution may be a real lookup — a database round-trip, say — not just a DI read.
        /// </summary>
        /// <remarks>
        /// The <see cref="Task{TResult}"/> is cached rather than its result, so a second caller arriving
        /// while the first lookup is still in flight awaits that same lookup instead of starting another.
        /// </remarks>
        private ValueTask<AIHostAgent> GetAgentAsync()
        {
            var httpContext = httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No HttpContext available");
            if (httpContext.Items.TryGetValue(ResolvedAgentKey, out var cached) && cached is Task<AIHostAgent> inFlight)
            {
                return new ValueTask<AIHostAgent>(inFlight);
            }

            var resolved = resolveAgent(httpContext).AsTask();
            httpContext.Items[ResolvedAgentKey] = resolved;
            return new ValueTask<AIHostAgent>(resolved);
        }

        /// <summary>
        /// Conversation id for callers that run this agent without handing in a session. The AG-UI
        /// endpoint is not one of them — it resolves the session from the thread id and persists it
        /// after the run — so this only covers direct invocations, which today means the tests.
        /// </summary>
        private static string GetConversationId(AgentRunOptions? options)
        {
            // The whole AG-UI request rides on ChatOptions.AdditionalProperties; TryGetRunAgentInput
            // is the supported way for agents and delegating chat clients to read it.
            if (options is ChatClientAgentRunOptions { ChatOptions: { } chatOptions } &&
                chatOptions.TryGetRunAgentInput(out var input) &&
                !string.IsNullOrWhiteSpace(input.ThreadId))
            {
                return input.ThreadId;
            }

            throw new InvalidOperationException("No conversation ID provided (AG-UI RunAgentInput.ThreadId).");
        }
    }
}
