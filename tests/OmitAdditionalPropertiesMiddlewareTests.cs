using AGUI.Abstractions;
using AgenticTodos.Backend;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// Two app-internal objects ride on <see cref="ChatOptions.AdditionalProperties"/> by the time a request
/// reaches the provider: the whole <c>RunAgentInput</c> (stashed by the AG-UI server SDK under a key
/// that is its own internal detail, hence matching by value type) and this app's
/// <c>ConversationState</c>. An adapter that forwarded them as <c>AdditionalModelRequestFields</c> makes
/// Claude reject the call with <i>"Extra inputs are not permitted"</i>.
/// </summary>
public class OmitAdditionalPropertiesMiddlewareTests
{
    [Fact]
    public async Task MatchingValueTypes_AreStripped()
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new()
            {
                { "agui_input", new RunAgentInput { ThreadId = "t", RunId = "r" } },
                { "my_state", new StateSnapshotMiddleware.ConversationState() },
            },
        };

        var seen = await CaptureAsync(options);

        Assert.Empty(seen!);
    }

    [Fact]
    public async Task UnrelatedProperties_Survive()
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new()
            {
                { "agui_input", new RunAgentInput { ThreadId = "t", RunId = "r" } },
                { "keep_me", "a provider-specific option" },
            },
        };

        var seen = await CaptureAsync(options);

        Assert.Equal(["keep_me"], seen!.Keys);
    }

    [Fact]
    public async Task NullValues_Survive()
    {
        // Load-bearing for StateSnapshotMiddleware, which only writes its key when there IS state
        // precisely because this filter matches on the value's type and a null cannot have one.
        var options = new ChatOptions
        {
            AdditionalProperties = new() { { "my_state", null } },
        };

        var seen = await CaptureAsync(options);

        Assert.Equal(["my_state"], seen!.Keys);
    }

    [Fact]
    public async Task NoAdditionalProperties_IsNotAProblem()
    {
        var seen = await CaptureAsync(new ChatOptions());

        Assert.Null(seen);
    }

    [Fact]
    public async Task StreamingPath_StripsTheSameWay()
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new()
            {
                { "agui_input", new RunAgentInput { ThreadId = "t", RunId = "r" } },
                { "keep_me", 1 },
            },
        };

        var capturing = new CapturingChatClient();
        using var client = new OmitAdditionalPropertiesMiddleware(
            capturing, [typeof(RunAgentInput), typeof(StateSnapshotMiddleware.ConversationState)]);

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options))
        {
        }

        Assert.Equal(["keep_me"], capturing.SeenProperties!.Keys);
    }

    private static async Task<AdditionalPropertiesDictionary?> CaptureAsync(ChatOptions options)
    {
        var capturing = new CapturingChatClient();
        using var client = new OmitAdditionalPropertiesMiddleware(
            capturing, [typeof(RunAgentInput), typeof(StateSnapshotMiddleware.ConversationState)]);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        return capturing.SeenProperties;
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public AdditionalPropertiesDictionary? SeenProperties { get; private set; }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            SeenProperties = options?.AdditionalProperties;
            return Task.FromResult(new ChatResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SeenProperties = options?.AdditionalProperties;
            await Task.Yield();
            yield return new ChatResponseUpdate();
        }
    }
}
