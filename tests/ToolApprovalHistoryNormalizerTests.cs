using AgenticTodos.Backend;
using Microsoft.Extensions.AI;

#pragma warning disable MEAI001 // Tool approval types are experimental

namespace AgenticTodos.Tests;

public class ToolApprovalHistoryNormalizerTests
{
    private static ToolApprovalRequestContent Request(string callId = "call_1", string? requestId = null)
        => new(requestId ?? $"ficc_{callId}", new FunctionCallContent(callId, "increment_counter"));

    private static ToolApprovalResponseContent Response(string callId = "call_1", bool approved = true, string? requestId = null)
        => new(requestId ?? $"ficc_{callId}", approved, new FunctionCallContent(callId, "increment_counter"));

    [Fact]
    public void CompletedPair_RequestAndResponseScrubbed_RecreatedPairKept()
    {
        // The persisted shape after a successful resume turn: request (previous turn), response,
        // recreated call + result, assistant text.
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "increment the counter"),
            new(ChatRole.Assistant, [Request()]),
            new(ChatRole.User, [Response()]),
            new(ChatRole.Assistant, [new FunctionCallContent("call_1", "increment_counter")]),
            new(ChatRole.Tool, [new FunctionResultContent("call_1", "1")]),
            new(ChatRole.Assistant, "done"),
        };

        ToolApprovalHistoryNormalizer.Normalize(history, requestMessages: []);

        Assert.Equal(4, history.Count);
        Assert.DoesNotContain(history, m => m.Contents.Any(c => c is ToolApprovalRequestContent or ToolApprovalResponseContent));
        Assert.Contains(history, m => m.Contents.Any(c => c is FunctionCallContent { CallId: "call_1" }));
        Assert.Contains(history, m => m.Contents.Any(c => c is FunctionResultContent { CallId: "call_1" }));
    }

    [Fact]
    public void CompletedPair_OtherContentInSameMessage_IsKept()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new TextContent("let me check"), Request()]),
            new(ChatRole.User, [Response()]),
            new(ChatRole.Tool, [new FunctionResultContent("call_1", "1")]),
        };

        ToolApprovalHistoryNormalizer.Normalize(history, requestMessages: []);

        Assert.Equal(2, history.Count);
        Assert.Equal("let me check", Assert.IsType<TextContent>(Assert.Single(history[0].Contents)).Text);
    }

    [Fact]
    public void OrphanedRequest_GetsRejectedResponseAppended()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "increment the counter"),
            new(ChatRole.Assistant, [Request()]),
        };

        ToolApprovalHistoryNormalizer.Normalize(history, requestMessages: [new ChatMessage(ChatRole.User, "unrelated new question")]);

        Assert.Equal(3, history.Count);
        Assert.Equal(ChatRole.User, history[2].Role);
        var response = Assert.IsType<ToolApprovalResponseContent>(Assert.Single(history[2].Contents));
        Assert.False(response.Approved);
        Assert.Equal("ficc_call_1", response.RequestId);
        // The request itself stays so the function-invocation layer can pair it with the rejection.
        Assert.Contains(history[1].Contents, c => c is ToolApprovalRequestContent);
    }

    [Fact]
    public void PendingRequest_AnsweredByCurrentTurn_IsLeftAlone()
    {
        // The normal resume turn: history holds the unanswered request, the answer arrives in the
        // current turn's request messages — nothing to repair.
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "increment the counter"),
            new(ChatRole.Assistant, [Request()]),
        };

        ToolApprovalHistoryNormalizer.Normalize(history, requestMessages: [new ChatMessage(ChatRole.User, [Response()])]);

        Assert.Equal(2, history.Count);
        Assert.Contains(history[1].Contents, c => c is ToolApprovalRequestContent);
    }

    [Fact]
    public void PendingRequest_AnsweredWithinHistory_IsLeftAlone()
    {
        // Request + response persisted, but the tool has not executed yet (e.g. crash mid-run):
        // FICC must pair and execute them, so the normalizer must not touch anything.
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [Request()]),
            new(ChatRole.User, [Response()]),
        };

        ToolApprovalHistoryNormalizer.Normalize(history, requestMessages: []);

        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void MultipleRequests_MixedStates_HandledIndependently()
    {
        var history = new List<ChatMessage>
        {
            // completed pair for call_1
            new(ChatRole.Assistant, [Request("call_1")]),
            new(ChatRole.User, [Response("call_1")]),
            new(ChatRole.Tool, [new FunctionResultContent("call_1", "1")]),
            // orphaned request for call_2
            new(ChatRole.Assistant, [Request("call_2")]),
        };

        ToolApprovalHistoryNormalizer.Normalize(history, requestMessages: []);

        Assert.DoesNotContain(history, m => m.Contents.Any(c => c is ToolApprovalRequestContent { ToolCall.CallId: "call_1" }));
        Assert.Contains(history, m => m.Contents.Any(c => c is ToolApprovalRequestContent { ToolCall.CallId: "call_2" }));
        var rejection = Assert.IsType<ToolApprovalResponseContent>(Assert.Single(history[^1].Contents));
        Assert.False(rejection.Approved);
        Assert.Equal("ficc_call_2", rejection.RequestId);
    }

    [Fact]
    public void HistoryWithoutApprovalContent_IsUntouched()
    {
        var m1 = new ChatMessage(ChatRole.User, "hi");
        var m2 = new ChatMessage(ChatRole.Assistant, "hello");
        var history = new List<ChatMessage> { m1, m2 };

        ToolApprovalHistoryNormalizer.Normalize(history, requestMessages: []);

        Assert.Equal([m1, m2], history);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [Request()]),
            new(ChatRole.User, [Response()]),
            new(ChatRole.Tool, [new FunctionResultContent("call_1", "1")]),
            new(ChatRole.Assistant, [Request("call_2")]),
        };

        ToolApprovalHistoryNormalizer.Normalize(history, requestMessages: []);
        var afterFirst = history.Count;
        ToolApprovalHistoryNormalizer.Normalize(history, requestMessages: []);

        Assert.Equal(afterFirst, history.Count);
    }
}
