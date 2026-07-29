using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using AgenticTodos.Backend;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticTodos.Tests;

/// <summary>
/// The stand-in agent the AG-UI endpoint is mapped with. It resolves the real agent per request, and
/// that resolution is asynchronous on purpose — an implementation may load agent definitions from a
/// database. These tests pin the two properties that makes it depend on: the lookup is awaited, and it
/// happens once per request however many times the SDK calls in.
/// </summary>
public class HttpContextRoutingAgentTests
{
    [Fact]
    public async Task AsyncLookup_IsAwaited_AndResolvedOncePerRequest()
    {
        var provider = BuildProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var lookups = 0;

        var routing = CreateRoutingAgent(accessor, async httpContext =>
        {
            Interlocked.Increment(ref lookups);
            await Task.Delay(20, httpContext.RequestAborted);  // a real round-trip
            return new AIHostAgent(NoopAgent(), NoopStore());
        });

        accessor.HttpContext = NewRequest(provider, alias: "openai");

        // Several calls in one request, across two of the entry points the SDK uses per run.
        await routing.CreateSessionAsync();
        await routing.CreateSessionAsync();
        var session = await routing.CreateSessionAsync();
        await routing.SerializeSessionAsync(session);

        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task ConcurrentCallsInOneRequest_ShareASingleLookup()
    {
        // The cache holds the in-flight Task, so overlapping callers must not each start a lookup.
        var provider = BuildProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var lookups = 0;
        var release = new TaskCompletionSource();

        var routing = CreateRoutingAgent(accessor, async _ =>
        {
            Interlocked.Increment(ref lookups);
            await release.Task;
            return new AIHostAgent(NoopAgent(), NoopStore());
        });

        accessor.HttpContext = NewRequest(provider, alias: "openai");

        var first = routing.CreateSessionAsync();
        var second = routing.CreateSessionAsync();
        release.SetResult();
        await Task.WhenAll(first.AsTask(), second.AsTask());

        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task EachRequest_GetsItsOwnLookup()
    {
        var provider = BuildProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var seenAliases = new List<string>();

        var routing = CreateRoutingAgent(accessor, httpContext =>
        {
            seenAliases.Add(httpContext.Request.RouteValues["alias"]?.ToString() ?? "");
            return new ValueTask<AIHostAgent>(new AIHostAgent(NoopAgent(), NoopStore()));
        });

        accessor.HttpContext = NewRequest(provider, alias: "openai");
        await routing.CreateSessionAsync();
        accessor.HttpContext = NewRequest(provider, alias: "amazonbedrock");
        await routing.CreateSessionAsync();

        Assert.Equal(["openai", "amazonbedrock"], seenAliases);
    }

    [Fact]
    public async Task FailedLookup_SurfacesToTheCaller()
    {
        // AguiRunErrorMiddleware turns this into RUN_ERROR, so it must propagate rather than be swallowed.
        var provider = BuildProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();

        var routing = CreateRoutingAgent(accessor, async _ =>
        {
            await Task.Yield();
            throw new AguiClientException("Unknown agent alias 'nope'.");
        });

        accessor.HttpContext = NewRequest(provider, alias: "nope");

        var ex = await Assert.ThrowsAsync<AguiClientException>(async () => await routing.CreateSessionAsync());
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Id_IsDerivedFromTheRoute_SoSessionsAreStableAndPerAlias()
    {
        var provider = BuildProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var routing = CreateRoutingAgent(accessor, _ => new(new AIHostAgent(NoopAgent(), NoopStore())));

        accessor.HttpContext = NewRequest(provider, alias: "openai");
        var openai = routing.Id;
        accessor.HttpContext = NewRequest(provider, alias: "amazonbedrock");
        var bedrock = routing.Id;
        accessor.HttpContext = NewRequest(provider, alias: "openai");

        // Re-evaluated per request (not cached by the base class), distinct per alias, and stable.
        Assert.Equal("routed-openai", openai);
        Assert.Equal("routed-amazonbedrock", bedrock);
        Assert.Equal(openai, routing.Id);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Id_RejectsAliasesThatAreNotAliasShaped(string alias)
    {
        // The id reaches a file name via FileSystemSessionStore, so it must not be composed from
        // arbitrary request input.
        var provider = BuildProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var routing = CreateRoutingAgent(accessor, _ => new(new AIHostAgent(NoopAgent(), NoopStore())));

        accessor.HttpContext = NewRequest(provider, alias);

        Assert.Equal("routed", routing.Id);
    }

    [Fact]
    public void Name_IsTheSessionStoreDiKey()
    {
        // MapAGUIServer resolves the store as GetKeyedService<AgentSessionStore>(agent.Name).
        var provider = BuildProvider();
        var routing = CreateRoutingAgent(
            provider.GetRequiredService<IHttpContextAccessor>(),
            _ => new(new AIHostAgent(NoopAgent(), NoopStore())));

        Assert.Equal(AGUIEndpoint.RoutedAgentName, routing.Name);
    }

    // ---------------------------------------------------------------------------
    // The run paths, which only direct callers (the tests) reach — the AG-UI endpoint always hands in
    // a session it resolved from the thread id itself.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RunWithoutASession_UsesTheThreadIdFromTheAguiInput_AndSavesAfterwards()
    {
        var provider = BuildProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var store = new RecordingSessionStore();
        var routing = CreateRoutingAgent(accessor, _ => new(new AIHostAgent(RespondingAgent(), store)));

        accessor.HttpContext = NewRequest(provider, alias: "openai");

        await routing.RunAsync([new ChatMessage(ChatRole.User, "hi")], session: null, RunWithThreadId("thread_abc"));

        Assert.Equal("thread_abc", store.LastGetSessionId);
        Assert.Equal("thread_abc", store.LastSaveSessionId);
    }

    [Fact]
    public async Task StreamingRunWithoutASession_UsesTheThreadIdFromTheAguiInput_AndSavesAfterwards()
    {
        // The streaming twin of the test above, and the only thing that pins the save at the end of
        // RunCoreStreamingAsync: deleting that block leaves the negative test below green, because
        // "nothing was saved yet" is also what no save at all looks like.
        var provider = BuildProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var store = new RecordingSessionStore();
        var routing = CreateRoutingAgent(accessor, _ => new(new AIHostAgent(RespondingAgent(), store)));

        accessor.HttpContext = NewRequest(provider, alias: "openai");

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in routing.RunStreamingAsync(
            [new ChatMessage(ChatRole.User, "hi")], session: null, RunWithThreadId("thread_abc")))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        Assert.Equal("thread_abc", store.LastGetSessionId);
        Assert.Equal("thread_abc", store.LastSaveSessionId);
    }

    [Fact]
    public async Task StreamingRunWithoutASession_SavesOnlyOnceTheStreamIsDrained()
    {
        var provider = BuildProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var store = new RecordingSessionStore();
        var routing = CreateRoutingAgent(accessor, _ => new(new AIHostAgent(RespondingAgent(), store)));

        accessor.HttpContext = NewRequest(provider, alias: "openai");

        var stream = routing.RunStreamingAsync([new ChatMessage(ChatRole.User, "hi")], session: null, RunWithThreadId("thread_abc"));
        await using (var enumerator = stream.GetAsyncEnumerator())
        {
            await enumerator.MoveNextAsync();
        }

        // Documented consequence of saving after the loop: abandoning the stream drops the session.
        Assert.Null(store.LastSaveSessionId);
    }

    [Fact]
    public async Task RunWithAnExplicitSession_DoesNotTouchTheStore()
    {
        var provider = BuildProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var store = new RecordingSessionStore();
        var inner = RespondingAgent();
        var routing = CreateRoutingAgent(accessor, _ => new(new AIHostAgent(inner, store)));

        accessor.HttpContext = NewRequest(provider, alias: "openai");

        await routing.RunAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            await inner.CreateSessionAsync(),
            RunWithThreadId("thread_abc"));

        Assert.Null(store.LastGetSessionId);
        Assert.Null(store.LastSaveSessionId);
    }

    [Fact]
    public async Task RunWithoutASessionAndWithoutAnAguiInput_Fails()
    {
        var provider = BuildProvider();
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        var routing = CreateRoutingAgent(accessor, _ => new(new AIHostAgent(NoopAgent(), NoopStore())));

        accessor.HttpContext = NewRequest(provider, alias: "openai");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => routing.RunAsync([new ChatMessage(ChatRole.User, "hi")], session: null));

        Assert.Contains("ThreadId", ex.Message);
    }

    /// <summary>Run options shaped the way an AG-UI request arrives: the whole input on ChatOptions.</summary>
    private static AgentRunOptions RunWithThreadId(string threadId)
    {
        var input = new RunAgentInput { ThreadId = threadId, RunId = "r1" };
        var context = input.ToChatRequestContext(AguiJson.Options);
        return new ChatClientAgentRunOptions { ChatOptions = context.ChatOptions };
    }

    private sealed class RecordingSessionStore : AgentSessionStore
    {
        public string? LastGetSessionId { get; private set; }
        public string? LastSaveSessionId { get; private set; }

        public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
        {
            LastGetSessionId = sessionStoreId;
            return await agent.CreateSessionAsync(cancellationToken);
        }

        public override ValueTask SaveSessionAsync(AIAgent agent, string sessionStoreId, AgentSession session, CancellationToken cancellationToken = default)
        {
            LastSaveSessionId = sessionStoreId;
            return ValueTask.CompletedTask;
        }

        public override ValueTask DeleteSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    /// <summary>Builds the internal routing agent the endpoint uses.</summary>
    private static AIAgent CreateRoutingAgent(
        IHttpContextAccessor accessor,
        Func<HttpContext, ValueTask<AIHostAgent>> resolveAgent)
        => new AGUIEndpoint.HttpContextRoutingAgent(accessor, resolveAgent);

    private static DefaultHttpContext NewRequest(IServiceProvider provider, string alias)
    {
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.RouteValues["alias"] = alias;
        return context;
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        return services.BuildServiceProvider();
    }

    private static AIAgent NoopAgent() => new NoopChatClient().AsAIAgent();

    /// <summary>For the run-path tests, which do reach the model.</summary>
    private static AIAgent RespondingAgent() => new SilentChatClient().AsAIAgent();

    private static AgentSessionStore NoopStore() => new ThrowingSessionStore();
}
