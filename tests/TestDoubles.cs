using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AgenticTodos.Backend;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// The AG-UI JSON configuration the host applies, in one place. Deduplication is the lesser reason:
/// this has to stay in lockstep with the real pipeline, and two copies would drift.
/// </summary>
internal static class AguiJson
{
    /// <summary>
    /// Mirrors both halves of the host's configuration — the resolvers <c>AddAGUIServer()</c> chains in,
    /// and <see cref="AGUIEndpoint.ConfigureAguiJson"/>, which is the very method <c>Program.cs</c>
    /// calls. Sharing that method means a content type registered for the app cannot be missing here.
    /// </summary>
    internal static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Add(AgentAbstractionsJsonUtilities.DefaultOptions.TypeInfoResolver!);
        options.TypeInfoResolverChain.Add(AGUIJsonSerializerContext.Default.Options.TypeInfoResolver!);
        AGUIEndpoint.ConfigureAguiJson(options);
        return options;
    }
}

/// <summary>
/// An <see cref="IChatClient"/> that only has to exist — enough to build a real <see cref="AIAgent"/>
/// so a session can be created, while failing loudly if a test ever actually reaches the model.
/// </summary>
internal sealed class NoopChatClient : IChatClient
{
    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>
/// An <see cref="IChatClient"/> that answers, minimally — for the tests that do drive a real run and
/// only care about what happens around the model call.
/// </summary>
internal sealed class SilentChatClient : IChatClient
{
    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
    }
}

/// <summary>
/// An <see cref="AgentSessionStore"/> whose every member throws — for tests that need a store-shaped
/// argument but must fail loudly if anything actually touches it.
/// </summary>
internal sealed class ThrowingSessionStore : AgentSessionStore
{
    public override ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public override ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public override ValueTask DeleteSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

internal static class JsonAssert
{
    /// <summary>
    /// Fails with the path of the first <c>null</c> anywhere in <paramref name="element"/>, nested objects and
    /// arrays included — @ag-ui/client rejects an optional field written as null at any depth.
    /// </summary>
    internal static void NoNulls(JsonElement element, string path = "$")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                Assert.Fail($"null at {path}");
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    NoNulls(property.Value, $"{path}.{property.Name}");
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    NoNulls(item, $"{path}[{index++}]");
                }
                break;
        }
    }
}
