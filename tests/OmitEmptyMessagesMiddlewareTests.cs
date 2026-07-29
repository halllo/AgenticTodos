using AGUI.Abstractions;
using AgenticTodos.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// The one filter in the agent pipeline that can silently delete a client's message. Bedrock rejects a
/// content block with nothing in it and one such message poisons the whole turn, so the middleware has
/// to drop them — but "empty" is a hand-written rule, and every message it gets wrong is a message the
/// model never sees.
/// <para>
/// Two contracts therefore need pinning: <b>what is dropped</b> (the shapes the AG-UI→MEAI conversion
/// actually produces, including the declined-interrupt residue) and <b>what survives</b> (content that
/// carries meaning without carrying text). The second half rests on SDK facts a package bump could
/// invalidate without any compile error here, so those are asserted directly as well.
/// </para>
/// </summary>
public class OmitEmptyMessagesMiddlewareTests
{
    // ---------------------------------------------------------------------------
    // Dropped
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n\t  ")]
    public async Task BlankUserMessage_IsDropped(string text)
    {
        // AsChatMessages falls through to an empty user message for an AGUIUserMessage whose content
        // list is empty — this is the shape that reaches the model as a contentless block.
        Assert.Empty(await FilterAsync(new ChatMessage(ChatRole.User, text)));
    }

    [Fact]
    public async Task BlankAssistantMessage_IsDropped()
    {
        // Not just the user's: the predicate this replaced only looked at ChatRole.System, and
        // AsChatMessages produces an empty *assistant* message for an AGUIAssistantMessage with neither
        // content nor tool calls — replayed history is full of them.
        Assert.Empty(await FilterAsync(new ChatMessage(ChatRole.Assistant, "   ")));
    }

    [Fact]
    public async Task BlankSystemMessage_IsStillDropped()
    {
        // The behaviour the narrower OmitEmptySystemMessagesMiddleware existed for must not regress.
        Assert.Empty(await FilterAsync(new ChatMessage(ChatRole.System, "")));
    }

    [Fact]
    public async Task MessageWithNoContentAtAll_IsDropped()
    {
        // Contents.Count == 0 is a separate case from "all contents are blank": All(...) is vacuously
        // true for an empty sequence today, but that is an implementation coincidence, and a message
        // with no content block is exactly what Bedrock rejects.
        Assert.Empty(await FilterAsync(new ChatMessage(ChatRole.User, [])));
    }

    [Fact]
    public async Task InterruptResponseOnly_IsDropped()
    {
        // A declined approval arrives as ChatMessage(User, [InterruptResponseContent]):
        // ToChatRequestContext emits one per resume entry that is not a decodable tool approval, and
        // {status:"cancelled"} with no payload is precisely that. Nothing below this middleware reads
        // the type — the provider mappers drop content they do not model — so left in place it reaches
        // Bedrock as a message with no content block at all.
        Assert.Empty(await FilterAsync(
            new ChatMessage(ChatRole.User, [new InterruptResponseContent("i1")])));
    }

    [Fact]
    public async Task BlankTextMixedWithAnInterruptResponse_IsDropped()
    {
        // Both carve-outs have to compose: a message that is blank text *and* interrupt residue is
        // still nothing the model can be shown.
        Assert.Empty(await FilterAsync(new ChatMessage(ChatRole.User,
            [new TextContent("  "), new InterruptResponseContent("i1"), new TextContent("")])));
    }

    [Fact]
    public async Task OnlyTheEmptyMessagesAreDropped_AndTheOrderIsPreserved()
    {
        // The middleware rewrites the sequence it forwards, so it must not reorder or duplicate what it
        // keeps — the model reads the turn in order.
        var forwarded = await FilterAsync(
            new ChatMessage(ChatRole.System, "you are helpful"),
            new ChatMessage(ChatRole.User, "   "),
            new ChatMessage(ChatRole.User, "add milk"),
            new ChatMessage(ChatRole.User, [new InterruptResponseContent("i1")]),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "add_todo")]));

        Assert.Equal(
            ["system:TextContent", "user:TextContent", "assistant:FunctionCallContent"],
            forwarded);
    }

    // ---------------------------------------------------------------------------
    // Kept — content that means something without meaning text
    // ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(MeaningfulContents))]
    public async Task MeaningfulContent_Survives_AloneAndAlongsideBlankText(AIContent content)
    {
        var kind = content.GetType().Name;

        Assert.Equal([$"assistant:{kind}"], await FilterAsync(new ChatMessage(ChatRole.Assistant, [content])));

        // And blank text does not drag the rest of the message down with it: All(...) fails on the
        // first item that is not inert, so a mixed message survives whole.
        Assert.Equal(
            [$"assistant:TextContent+{kind}"],
            await FilterAsync(new ChatMessage(ChatRole.Assistant, [new TextContent("   "), content])));
    }

    public static TheoryData<AIContent> MeaningfulContents() =>
    [
        // A pending tool call is the whole point of the turn even with no prose around it.
        new FunctionCallContent("call_1", "add_todo"),

        // A tool that legitimately returned nothing. Serializing to "" must not read as "blank text" —
        // see AToolResultThatSerializedToEmpty_IsNotTextContent for why it cannot.
        new FunctionResultContent("call_1", ""),

        // An image or a file the user attached carries no text at all.
        new DataContent("data:application/json;base64,e30="),

        // Extended thinking: a reasoning block may be nothing but the provider's opaque signature, and
        // Bedrock rejects the *next* request if the signature it sent is not echoed back.
        new TextReasoningContent(string.Empty) { ProtectedData = "signature-abc123" },

        // The ordinary case, last, so the theory would notice a predicate inverted wholesale.
        new TextContent("add milk"),
    ];

    [Fact]
    public async Task RedactedReasoning_Survives()
    {
        // The other shape a signature-only reasoning block takes on this pipeline: AWS's adapter puts
        // the blob in AdditionalProperties["RedactedContent"] (see RedactedReasoningNormalizer), leaving
        // Text empty and ProtectedData null. Still not text, still must be echoed back.
        var reasoning = new TextReasoningContent(string.Empty)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["RedactedContent"] = new byte[] { 1, 2, 3 } },
        };

        Assert.Equal(
            ["assistant:TextReasoningContent"],
            await FilterAsync(new ChatMessage(ChatRole.Assistant, [reasoning])));
    }

    // ---------------------------------------------------------------------------
    // The SDK facts the carve-outs rest on. None of them is enforced by the compiler here, so a
    // package bump could invalidate a carve-out while every other test stays green.
    // ---------------------------------------------------------------------------

    [Fact]
    public void TextContent_IsSealed_SoThePatternCannotMatchAPayloadCarryingType()
    {
        // `TextContent { Text: var text }` matches subtypes too. Sealed is what guarantees that a match
        // means "this content is nothing but its text" — an unsealed TextContent carrying extra payload
        // with an empty Text would be dropped silently.
        Assert.True(typeof(TextContent).IsSealed);
    }

    [Fact]
    public void TextReasoningContent_DoesNotDeriveFromTextContent()
    {
        // "Neither types derives from the other" — TextReasoningContent's own XML doc, verified against
        // Microsoft.Extensions.AI 10.8.3. If it ever did derive from TextContent, the blank-text arm
        // would start swallowing signature-only reasoning blocks.
        Assert.False(typeof(TextContent).IsAssignableFrom(typeof(TextReasoningContent)));
        Assert.False(typeof(TextReasoningContent).IsAssignableFrom(typeof(TextContent)));
        Assert.True(typeof(AIContent).IsAssignableFrom(typeof(TextReasoningContent)));
    }

    [Fact]
    public async Task AToolResultThatSerializedToEmpty_IsNotTextContent()
    {
        // The conversion the AG-UI server SDK runs on the way in: an AGUIToolMessage becomes
        // FunctionResultContent, never TextContent. That is what keeps a tool result of "" out of the
        // blank-text arm — verified against AGUI.Abstractions 0.0.3.
        var converted = new AGUIMessage[]
        {
            new AGUIToolMessage { Id = "m1", ToolCallId = "call_1", Content = "" },
        }.AsChatMessages().ToList();

        Assert.IsType<FunctionResultContent>(Assert.Single(Assert.Single(converted).Contents));
        Assert.Single(await FilterAsync([.. converted]));
    }

    // ---------------------------------------------------------------------------

    /// <summary>
    /// Drives the middleware exactly as <c>AIAgentBuilder.Use(sharedFunc:)</c> does and returns a
    /// readable description of what reached the next stage — <c>role:Content+Content</c> per message —
    /// so a failure names the messages that were wrongly kept or dropped.
    /// </summary>
    private static async Task<string[]> FilterAsync(params ChatMessage[] messages)
    {
        string[]? forwarded = null;

        await OmitEmptyMessagesMiddleware.Invoke(
            messages,
            session: null,
            options: null,
            next: (received, _, _, _) =>
            {
                // Materialized here on purpose: the middleware forwards a lazy Where(...), so nothing
                // is filtered until the next stage enumerates.
                forwarded = [.. received.Select(Describe)];
                return Task.CompletedTask;
            },
            cancellationToken: default);

        Assert.NotNull(forwarded);
        return forwarded;
    }

    private static string Describe(ChatMessage message) =>
        $"{message.Role.Value}:{string.Join("+", message.Contents.Select(c => c.GetType().Name))}";
}
