using System.Text.Json;
using AgenticTodos.Backend;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticTodos.Tests;

/// <summary>
/// MapAGUIServer resolves the endpoint's <see cref="AgentSessionStore"/> once at map time from the root
/// provider, so the endpoint can only ever hold a singleton. These tests pin the indirection that keeps
/// the app's own store free to use any lifetime — a scoped store would otherwise fail at startup with
/// <c>"Cannot resolve scoped service 'AgentSessionStore' from root provider"</c>.
/// </summary>
public class AguiSessionStoreLifetimeTests
{
    [Fact]
    public void ForwardingStore_IsResolvableFromTheRootProvider_EvenWhenTheRealStoreIsScoped()
    {
        // This is the startup-time lookup MapAGUIServer performs, against a scoped real store.
        var provider = BuildProvider(ServiceLifetime.Scoped);

        var endpointStore = provider.GetKeyedService<AgentSessionStore>(AGUIEndpoint.RoutedAgentName);

        Assert.IsType<AGUIEndpoint.HttpContextRoutingSessionStore>(endpointStore);
    }

    [Fact]
    public async Task ScopedStore_IsResolvedPerRequest()
    {
        var provider = BuildProvider(ServiceLifetime.Scoped);
        var endpointStore = provider.GetRequiredKeyedService<AgentSessionStore>(AGUIEndpoint.RoutedAgentName);
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();

        var first = await UseInRequestAsync(provider, accessor, endpointStore, "thread-1");
        var second = await UseInRequestAsync(provider, accessor, endpointStore, "thread-2");

        // Two requests, two scopes, two distinct store instances — that is what "scoped" has to mean.
        Assert.NotSame(first, second);
        Assert.Equal(["thread-1"], first.SeenSessionStoreIds);
        Assert.Equal(["thread-2"], second.SeenSessionStoreIds);
    }

    [Fact]
    public async Task SingletonStore_IsShared()
    {
        // The lifetime actually registered today; the indirection must be transparent for it.
        var provider = BuildProvider(ServiceLifetime.Singleton);
        var endpointStore = provider.GetRequiredKeyedService<AgentSessionStore>(AGUIEndpoint.RoutedAgentName);
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();

        var first = await UseInRequestAsync(provider, accessor, endpointStore, "thread-1");
        var second = await UseInRequestAsync(provider, accessor, endpointStore, "thread-2");

        Assert.Same(first, second);
        Assert.Equal(["thread-1", "thread-2"], first.SeenSessionStoreIds);
    }

    [Fact]
    public async Task WithoutARequest_FailsWithAReadableMessage()
    {
        var provider = BuildProvider(ServiceLifetime.Scoped);
        var endpointStore = provider.GetRequiredKeyedService<AgentSessionStore>(AGUIEndpoint.RoutedAgentName);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await endpointStore.GetSessionAsync(StubAgent(), "thread-1"));
        Assert.Contains("HttpContext", ex.Message);
    }

    /// <summary>Runs one "request": its own DI scope, with HttpContext.RequestServices pointing at it.</summary>
    private static async ValueTask<RecordingSessionStore> UseInRequestAsync(
        IServiceProvider provider,
        IHttpContextAccessor accessor,
        AgentSessionStore endpointStore,
        string sessionStoreId)
    {
        using var scope = provider.CreateScope();
        accessor.HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        try
        {
            await endpointStore.GetSessionAsync(StubAgent(), sessionStoreId);
            return (RecordingSessionStore)scope.ServiceProvider.GetRequiredService<AgentSessionStore>();
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private static ServiceProvider BuildProvider(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        if (lifetime == ServiceLifetime.Scoped)
        {
            services.AddScoped<AgentSessionStore, RecordingSessionStore>();
        }
        else
        {
            services.AddSingleton<AgentSessionStore, RecordingSessionStore>();
        }
        services.AddAGUISessionStore();

        // Matches how the app builds its container, so resolving a scoped service from the root throws
        // rather than silently producing a captive singleton.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class RecordingSessionStore : AgentSessionStore
    {
        public List<string> SeenSessionStoreIds { get; } = [];

        public override ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
        {
            SeenSessionStoreIds.Add(sessionStoreId);
            return agent.CreateSessionAsync(cancellationToken);
        }

        public override ValueTask SaveSessionAsync(AIAgent agent, string sessionStoreId, AgentSession session, CancellationToken cancellationToken = default)
        {
            SeenSessionStoreIds.Add(sessionStoreId);
            return ValueTask.CompletedTask;
        }

        public override ValueTask DeleteSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
        {
            SeenSessionStoreIds.Add(sessionStoreId);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A real agent, so the store gets a real AgentSession; no model call is ever made.</summary>
    private static AIAgent StubAgent() => new NoopChatClient().AsAIAgent();
}
