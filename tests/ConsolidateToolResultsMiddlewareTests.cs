using System.Runtime.CompilerServices;
using AgenticTodos.Backend;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// Bedrock requires every <c>toolResult</c> block answering one assistant turn to sit in a <b>single</b>
/// user message: split them and the call is rejected outright with <i>"Expected toolResult blocks at
/// messages.N.content for the following Ids: ..."</i>. The AG-UI protocol carries one <c>toolCallId</c>
/// per <c>tool</c> message and <c>AsChatMessages</c> emits one <see cref="ChatMessage"/> each, so parallel
/// tool results always arrive split and this middleware is what makes them legal again.
/// <para>
/// It is the last stage before the provider and it rewrites the message list, so both halves matter: the
/// merge itself, and that nothing else moves. Driven through <c>GetResponseAsync</c> and
/// <c>GetStreamingResponseAsync</c> with a capturing inner client, because the two overrides are separate
/// one-liners and only the streaming one runs in production — a transform applied to just one of them
/// would look correct in every non-streaming test.
/// </para>
/// The only other coverage is <c>AmazonBedrockToolCallTests.ChatWithTwoSeparatedToolCallsConsolidatesThem</c>,
/// which is <c>[LiveLlmFact]</c> and therefore skipped unless Bedrock credentials are configured.
/// </summary>
public class ConsolidateToolResultsMiddlewareTests
{
    // ---------------------------------------------------------------------------
    // The merge
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ConsecutiveToolMessages_MergeIntoOne_ContentsInOrder()
    {
        // Order is not cosmetic: the blocks are matched to the assistant's tool calls by CallId, and a
        // reordering middleware is exactly the kind of thing that works until a model happens to care.
        var seen = await SendAsync(
            ToolMessage("m1", "call_1"),
            ToolMessage("m2", "call_2"),
            ToolMessage("m3", "call_3"));

        Assert.Equal(["tool:call_1+call_2+call_3"], Describe(seen));
    }

    [Fact]
    public async Task OneToolMessageAlone_IsUnchangedInSubstance()
    {
        // The single-result case is the common one; it must survive the same code path untouched.
        var seen = await SendAsync(ToolMessage("m1", "call_1"));

        Assert.Equal(["tool:call_1"], Describe(seen));
    }

    [Fact]
    public async Task TheFirstToolMessageOfAGroup_IsTheTemplateForTheMergedOne()
    {
        // A merged message is a new instance, so everything that is not content has to be copied over
        // deliberately. MessageId in particular is what the AG-UI layer and the history provider use to
        // identify a message; a merged result carrying a null or foreign id is indistinguishable from a
        // correct one right up to the point where something tries to correlate it.
        var first = ToolMessage("m1", "call_1");
        first.AuthorName = "todo-tools";
        first.AdditionalProperties = new AdditionalPropertiesDictionary { ["origin"] = "mcp" };

        var second = ToolMessage("m2", "call_2");
        second.AuthorName = "someone-else";
        second.AdditionalProperties = new AdditionalPropertiesDictionary { ["origin"] = "elsewhere" };

        var merged = Assert.Single(await SendAsync(first, second));

        Assert.Equal(ChatRole.Tool, merged.Role);
        Assert.Equal("m1", merged.MessageId);
        Assert.Equal("todo-tools", merged.AuthorName);

        // Carried by reference, not rebuilt: whatever else rode along on the template's dictionary goes
        // with it.
        Assert.Same(first.AdditionalProperties, merged.AdditionalProperties);
    }

    [Fact]
    public async Task TheInputMessagesAreNotMutated()
    {
        // This middleware sits on the IChatClient pipeline, below the ChatHistoryProvider that owns these
        // instances — the rewrite is for the provider call only. Merging into the first message in place
        // would duplicate every parallel tool result into the persisted history.
        var first = ToolMessage("m1", "call_1");
        var second = ToolMessage("m2", "call_2");

        var merged = Assert.Single(await SendAsync(first, second));

        Assert.NotSame(first, merged);
        Assert.Single(first.Contents);
        Assert.Single(second.Contents);
    }

    [Fact]
    public async Task ATrailingToolGroup_IsFlushed()
    {
        // The merge buffers and only emits when it sees a non-tool message, so the final flush after the
        // loop is the only thing that gets the last group out. A tool group at the end of the list is the
        // normal shape of a resumed turn — the results the model has not answered yet.
        var seen = await SendAsync(
            new ChatMessage(ChatRole.User, "what time is it, and go green"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "get_time"), new FunctionCallContent("call_2", "set_color")]),
            ToolMessage("m1", "call_1"),
            ToolMessage("m2", "call_2"));

        Assert.Equal(["user:text", "assistant:call_1+call_2", "tool:call_1+call_2"], Describe(seen));
    }

    [Fact]
    public async Task InterleavedGroups_EachMergeSeparately()
    {
        // One buffer, reset at every non-tool message: two rounds of parallel tool calls must not collapse
        // into one message spanning both assistant turns.
        var seen = await SendAsync(
            ToolMessage("m1", "call_1"),
            ToolMessage("m2", "call_2"),
            new ChatMessage(ChatRole.Assistant, "one moment"),
            ToolMessage("m3", "call_3"),
            ToolMessage("m4", "call_4"),
            new ChatMessage(ChatRole.Assistant, "done"),
            ToolMessage("m5", "call_5"));

        Assert.Equal(
            ["tool:call_1+call_2", "assistant:text", "tool:call_3+call_4", "assistant:text", "tool:call_5"],
            Describe(seen));

        // Each group keeps its own template.
        Assert.Equal(["m1", null, "m3", null, "m5"], seen.Select(m => m.MessageId));
    }

    // ---------------------------------------------------------------------------
    // What must not move
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NonToolMessages_PassThroughUntouched_AndInPosition()
    {
        // The system prompt and the conversation are forwarded as the same instances, in the same order.
        // A yield-based rewrite that lost or reordered one of these would break the turn without ever
        // touching a tool result.
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "you are helpful"),
            new ChatMessage(ChatRole.User, "add milk"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "add_todo")]),
            ToolMessage("m1", "call_1"),
            new ChatMessage(ChatRole.Assistant, "added"),
        };

        var seen = await SendAsync(messages);

        Assert.Equal(messages.Length, seen.Count);
        Assert.Same(messages[0], seen[0]);
        Assert.Same(messages[1], seen[1]);
        Assert.Same(messages[2], seen[2]);
        Assert.Same(messages[4], seen[4]);
    }

    [Fact]
    public async Task NoMessagesAtAll_IsNotAProblem()
    {
        Assert.Empty(await SendAsync());
    }

    [Fact]
    public async Task AConversationWithNoToolResults_IsForwardedAsIs()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "you are helpful"),
            new ChatMessage(ChatRole.User, "hello"),
        };

        Assert.Equal(messages, await SendAsync(messages));
    }

    // ---------------------------------------------------------------------------
    // Both overrides
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TheStreamingPath_ConsolidatesTheSameWay()
    {
        // The path production actually uses: the AG-UI endpoint streams, so a transform wired only into
        // GetResponseAsync would leave the real Bedrock call as split as it arrived.
        var capturing = new CapturingChatClient();
        using var client = new ConsolidateToolResultsMiddleware(capturing);

        await foreach (var _ in client.GetStreamingResponseAsync(
            [
                new ChatMessage(ChatRole.User, "go"),
                ToolMessage("m1", "call_1"),
                ToolMessage("m2", "call_2"),
            ]))
        {
        }

        Assert.NotNull(capturing.Seen);
        Assert.Equal(["user:text", "tool:call_1+call_2"], Describe(capturing.Seen));
        Assert.Equal("m1", capturing.Seen[1].MessageId);
    }

    // ---------------------------------------------------------------------------

    private static ChatMessage ToolMessage(string messageId, string callId) =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, $"result of {callId}")]) { MessageId = messageId };

    /// <summary>Sends through the non-streaming override and returns what the inner client was handed.</summary>
    private static async Task<List<ChatMessage>> SendAsync(params ChatMessage[] messages)
    {
        var capturing = new CapturingChatClient();
        using var client = new ConsolidateToolResultsMiddleware(capturing);

        await client.GetResponseAsync(messages);

        Assert.NotNull(capturing.Seen);
        return capturing.Seen;
    }

    /// <summary>
    /// <c>role:CallId+CallId</c> per message (<c>text</c> for prose), so a failure names the messages that
    /// were merged wrongly rather than printing object graphs.
    /// </summary>
    private static string[] Describe(IEnumerable<ChatMessage> messages) =>
    [
        .. messages.Select(m => $"{m.Role.Value}:{string.Join("+", m.Contents.Select(c => c switch
        {
            FunctionResultContent result => result.CallId,
            FunctionCallContent call => call.CallId,
            _ => "text",
        }))}"),
    ];

    private sealed class CapturingChatClient : IChatClient
    {
        /// <summary>Materialized on capture: the middleware forwards a lazy iterator, so nothing is
        /// merged until the inner client enumerates.</summary>
        public List<ChatMessage>? Seen { get; private set; }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen = [.. messages];
            return Task.FromResult(new ChatResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Seen = [.. messages];
            await Task.Yield();
            yield return new ChatResponseUpdate();
        }
    }
}
