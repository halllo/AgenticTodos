using System.Runtime.CompilerServices;
using System.Text.Json;
using AgenticTodos.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MEAI001, MAAI001 // Tool approval types are experimental

namespace AgenticTodos.Tests;

public class ToolApprovalBridgeMiddlewareTests
{
    // ---------------------------------------------------------------------------
    // Outbound — ToolApprovalRequestContent → synthetic request_approval tool call
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Outbound_ApprovalRequest_ConvertedToSyntheticToolCall()
    {
        var request = new ToolApprovalRequestContent(
            "ficc_call_1",
            new FunctionCallContent("call_1", "increment_counter", new Dictionary<string, object?> { ["amount"] = 1 }));
        var inner = new StubAgent { UpdatesToYield = [new AgentResponseUpdate(ChatRole.Assistant, [request])] };

        var updates = await RunBridge(inner, []);

        var fcc = Assert.IsType<FunctionCallContent>(Assert.Single(Assert.Single(updates).Contents));
        Assert.Equal(ToolApprovalBridgeMiddleware.ApprovalToolName, fcc.Name);
        Assert.Equal("ficc_call_1", fcc.CallId);
        Assert.Equal("ficc_call_1", fcc.Arguments?["id"]);
        var toolCall = Assert.IsType<Dictionary<string, object?>>(fcc.Arguments?["tool_call"]);
        Assert.Equal("call_1", toolCall["id"]);
        Assert.Equal("increment_counter", toolCall["name"]);
        var wrappedArgs = Assert.IsAssignableFrom<IDictionary<string, object?>>(toolCall["arguments"]);
        Assert.Equal(1, wrappedArgs["amount"]);
    }

    [Fact]
    public async Task Outbound_ApprovalUpdateWithoutMessageId_GetsOneStamped()
    {
        // ToolApprovalAgent re-emits queued approval requests without a MessageId; the AGUI layer
        // would serialize TOOL_CALL_START.parentMessageId = null, which @ag-ui/client rejects.
        var request = new ToolApprovalRequestContent("ficc_call_1", new FunctionCallContent("call_1", "increment_counter"));
        var inner = new StubAgent { UpdatesToYield = [new AgentResponseUpdate(ChatRole.Assistant, [request])] };

        var updates = await RunBridge(inner, []);

        Assert.False(string.IsNullOrEmpty(Assert.Single(updates).MessageId));
    }

    [Fact]
    public async Task Outbound_ApprovalUpdateWithMessageId_KeepsIt()
    {
        var request = new ToolApprovalRequestContent("ficc_call_1", new FunctionCallContent("call_1", "increment_counter"));
        var update = new AgentResponseUpdate(ChatRole.Assistant, [request]) { MessageId = "msg_42" };
        var inner = new StubAgent { UpdatesToYield = [update] };

        var updates = await RunBridge(inner, []);

        Assert.Equal("msg_42", Assert.Single(updates).MessageId);
    }

    [Fact]
    public async Task Outbound_NonApprovalUpdate_PassesThroughUnchanged()
    {
        var update = new AgentResponseUpdate(ChatRole.Assistant, [new TextContent("hello")]);
        var inner = new StubAgent { UpdatesToYield = [update] };

        var updates = await RunBridge(inner, []);

        Assert.Same(update, Assert.Single(updates));
        Assert.Equal("hello", Assert.IsType<TextContent>(Assert.Single(updates[0].Contents)).Text);
    }

    [Fact]
    public async Task Outbound_MixedContents_ConvertsOnlyApprovalRequestAndPreservesOrder()
    {
        var request = new ToolApprovalRequestContent(
            "ficc_call_1",
            new FunctionCallContent("call_1", "increment_counter"));
        var inner = new StubAgent
        {
            UpdatesToYield = [new AgentResponseUpdate(ChatRole.Assistant, [new TextContent("before"), request])]
        };

        var updates = await RunBridge(inner, []);

        var contents = Assert.Single(updates).Contents;
        Assert.Equal(2, contents.Count);
        Assert.Equal("before", Assert.IsType<TextContent>(contents[0]).Text);
        Assert.Equal(ToolApprovalBridgeMiddleware.ApprovalToolName, Assert.IsType<FunctionCallContent>(contents[1]).Name);
        Assert.Equal(ChatRole.Assistant, updates[0].Role);
    }

    [Fact]
    public async Task Outbound_MultipleApprovalRequests_AllConverted()
    {
        var inner = new StubAgent
        {
            UpdatesToYield =
            [
                new AgentResponseUpdate(ChatRole.Assistant,
                [
                    new ToolApprovalRequestContent("ficc_call_1", new FunctionCallContent("call_1", "increment_counter")),
                    new ToolApprovalRequestContent("ficc_call_2", new FunctionCallContent("call_2", "get_current_time")),
                ])
            ]
        };

        var updates = await RunBridge(inner, []);

        var calls = Assert.Single(updates).Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal(ToolApprovalBridgeMiddleware.ApprovalToolName, c.Name));
        Assert.Equal(["ficc_call_1", "ficc_call_2"], calls.Select(c => c.CallId));
    }

    // ---------------------------------------------------------------------------
    // Inbound — request_approval tool result → ToolApprovalResponseContent
    // ---------------------------------------------------------------------------

    [Fact]
    public void Inbound_ApprovedResult_ConvertedToApprovalResponseOnUserMessage()
    {
        var messages = ToolResultMessage("ficc_call_1", ResponseJson(approved: true));

        var result = ToolApprovalBridgeMiddleware.ConvertApprovalResultsToApprovalResponses(messages);

        var message = Assert.Single(result);
        Assert.Equal(ChatRole.User, message.Role);
        var response = Assert.IsType<ToolApprovalResponseContent>(Assert.Single(message.Contents));
        Assert.Equal("ficc_call_1", response.RequestId);
        Assert.True(response.Approved);
        var fcc = Assert.IsType<FunctionCallContent>(response.ToolCall);
        Assert.Equal("call_1", fcc.CallId);
        Assert.Equal("increment_counter", fcc.Name);
        Assert.True(fcc.Arguments?.ContainsKey("amount"));
    }

    [Fact]
    public void Inbound_RejectedResultWithReason_ConvertedToRejection()
    {
        var messages = ToolResultMessage("ficc_call_1", ResponseJson(approved: false, reason: "too risky"));

        var result = ToolApprovalBridgeMiddleware.ConvertApprovalResultsToApprovalResponses(messages);

        var response = Assert.IsType<ToolApprovalResponseContent>(Assert.Single(Assert.Single(result).Contents));
        Assert.False(response.Approved);
        Assert.Equal("too risky", response.Reason);
    }

    [Fact]
    public void Inbound_AlwaysApproveTool_ConvertedToAlwaysApproveResponse()
    {
        var messages = ToolResultMessage("ficc_call_1", ResponseJson(approved: true, alwaysApprove: "tool"));

        var result = ToolApprovalBridgeMiddleware.ConvertApprovalResultsToApprovalResponses(messages);

        var always = Assert.IsType<AlwaysApproveToolApprovalResponseContent>(Assert.Single(Assert.Single(result).Contents));
        Assert.True(always.AlwaysApproveTool);
        Assert.False(always.AlwaysApproveToolWithArguments);
        Assert.True(always.InnerResponse.Approved);
        Assert.Equal("ficc_call_1", always.InnerResponse.RequestId);
    }

    [Fact]
    public void Inbound_AlwaysApproveToolWithArguments_ConvertedToArgumentScopedResponse()
    {
        var messages = ToolResultMessage("ficc_call_1", ResponseJson(approved: true, alwaysApprove: "tool_with_arguments"));

        var result = ToolApprovalBridgeMiddleware.ConvertApprovalResultsToApprovalResponses(messages);

        var always = Assert.IsType<AlwaysApproveToolApprovalResponseContent>(Assert.Single(Assert.Single(result).Contents));
        Assert.False(always.AlwaysApproveTool);
        Assert.True(always.AlwaysApproveToolWithArguments);
    }

    [Fact]
    public void Inbound_AlwaysApproveCombinedWithRejection_ProducesPlainRejection()
    {
        var messages = ToolResultMessage("ficc_call_1", ResponseJson(approved: false, alwaysApprove: "tool"));

        var result = ToolApprovalBridgeMiddleware.ConvertApprovalResultsToApprovalResponses(messages);

        var response = Assert.IsType<ToolApprovalResponseContent>(Assert.Single(Assert.Single(result).Contents));
        Assert.False(response.Approved);
    }

    [Fact]
    public void Inbound_ResultAsJsonString_Converted()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Tool, [new FunctionResultContent("ficc_call_1", ResponseJson(approved: true).GetRawText())])
        };

        var result = ToolApprovalBridgeMiddleware.ConvertApprovalResultsToApprovalResponses(messages);

        Assert.IsType<ToolApprovalResponseContent>(Assert.Single(Assert.Single(result).Contents));
    }

    [Fact]
    public void Inbound_MultipleApprovalResults_AllConverted()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Tool, [new FunctionResultContent("ficc_call_1", ResponseJson(approved: true))]),
            new(ChatRole.Tool, [new FunctionResultContent("ficc_call_2", ResponseJson(approved: false, id: "ficc_call_2", callId: "call_2"))]),
        };

        var result = ToolApprovalBridgeMiddleware.ConvertApprovalResultsToApprovalResponses(messages);

        Assert.Equal(2, result.Count);
        var responses = result.Select(m => Assert.IsType<ToolApprovalResponseContent>(Assert.Single(m.Contents))).ToList();
        Assert.Equal(["ficc_call_1", "ficc_call_2"], responses.Select(r => r.RequestId));
        Assert.Equal([true, false], responses.Select(r => r.Approved));
    }

    // ---------------------------------------------------------------------------
    // Inbound — pass-through of everything that is not an approval response
    // ---------------------------------------------------------------------------

    [Fact]
    public void Inbound_RegularToolResult_PassesThroughUnchanged()
    {
        var frontendToolResult = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_9", "42")]);
        var userMessage = new ChatMessage(ChatRole.User, "hello");

        var result = ToolApprovalBridgeMiddleware.ConvertApprovalResultsToApprovalResponses([userMessage, frontendToolResult]);

        Assert.Equal(2, result.Count);
        Assert.Same(userMessage, result[0]);
        Assert.Same(frontendToolResult, result[1]);
    }

    [Fact]
    public void Inbound_NearMissJson_MissingApproved_PassesThroughUnchanged()
    {
        var nearMiss = JsonSerializer.SerializeToElement(new { id = "ficc_call_1", tool_call = new { id = "call_1", name = "x" } });
        var message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("ficc_call_1", nearMiss)]);

        var result = ToolApprovalBridgeMiddleware.ConvertApprovalResultsToApprovalResponses([message]);

        Assert.Same(message, Assert.Single(result));
    }

    [Fact]
    public void Inbound_CallIdMismatch_PassesThroughUnchanged()
    {
        var message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("some_other_call", ResponseJson(approved: true))]);

        var result = ToolApprovalBridgeMiddleware.ConvertApprovalResultsToApprovalResponses([message]);

        Assert.Same(message, Assert.Single(result));
    }

    [Fact]
    public void Inbound_MalformedJsonString_PassesThroughUnchanged()
    {
        var message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("ficc_call_1", "{not json")]);

        var result = ToolApprovalBridgeMiddleware.ConvertApprovalResultsToApprovalResponses([message]);

        Assert.Same(message, Assert.Single(result));
    }

    [Fact]
    public void Inbound_ResentApprovalToolCall_IsStripped_MessageDroppedWhenEmpty()
    {
        var resent = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("ficc_call_1", ToolApprovalBridgeMiddleware.ApprovalToolName)]);

        var result = ToolApprovalBridgeMiddleware.ConvertApprovalResultsToApprovalResponses([resent]);

        Assert.Empty(result);
    }

    [Fact]
    public void Inbound_ResentApprovalToolCall_IsStripped_OtherContentsKept()
    {
        var resent = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("let me ask"),
            new FunctionCallContent("ficc_call_1", ToolApprovalBridgeMiddleware.ApprovalToolName),
        ]);

        var result = ToolApprovalBridgeMiddleware.ConvertApprovalResultsToApprovalResponses([resent]);

        var message = Assert.Single(result);
        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal("let me ask", Assert.IsType<TextContent>(Assert.Single(message.Contents)).Text);
    }

    // ---------------------------------------------------------------------------
    // Roundtrip through the streaming middleware — inner agent sees converted messages
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RunStreaming_InnerAgentReceivesConvertedMessages()
    {
        var inner = new StubAgent();
        var messages = ToolResultMessage("ficc_call_1", ResponseJson(approved: true));

        await RunBridge(inner, messages);

        var received = Assert.Single(inner.ReceivedMessages!);
        Assert.Equal(ChatRole.User, received.Role);
        Assert.IsType<ToolApprovalResponseContent>(Assert.Single(received.Contents));
    }

    [Fact]
    public async Task RunAsync_DelegatesToDownstreamNonStreamingPath()
    {
        var request = new ToolApprovalRequestContent(
            "ficc_call_1",
            new FunctionCallContent("call_1", "increment_counter", new Dictionary<string, object?> { ["amount"] = 1 }));
        var inner = new StubAgent { UpdatesToYield = [new AgentResponseUpdate(ChatRole.Assistant, [request])] };

        var response = await ToolApprovalBridgeMiddleware.RunAsync([], session: null, options: null, inner, CancellationToken.None);

        Assert.True(inner.RunAsyncCalled);
        Assert.False(inner.RunStreamingAsyncCalled);
        var fcc = Assert.IsType<FunctionCallContent>(Assert.Single(response.Messages.Single().Contents));
        Assert.Equal(ToolApprovalBridgeMiddleware.ApprovalToolName, fcc.Name);
        Assert.Equal("ficc_call_1", fcc.CallId);
    }

    // ---------------------------------------------------------------------------
    // Wire contract — DTO property names
    // ---------------------------------------------------------------------------

    [Fact]
    public void ApprovalResponseDto_UsesWireContractPropertyNames()
    {
        var json = JsonSerializer.Serialize(new ToolApprovalBridgeMiddleware.ApprovalResponse
        {
            Id = "ficc_call_1",
            Approved = true,
            Reason = "ok",
            AlwaysApprove = "tool",
            ToolCall = new ToolApprovalBridgeMiddleware.ApprovalToolCall { Id = "call_1", Name = "increment_counter" },
        });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ficc_call_1", doc.RootElement.GetProperty("id").GetString());
        Assert.True(doc.RootElement.GetProperty("approved").GetBoolean());
        Assert.Equal("ok", doc.RootElement.GetProperty("reason").GetString());
        Assert.Equal("tool", doc.RootElement.GetProperty("always_approve").GetString());
        Assert.Equal("call_1", doc.RootElement.GetProperty("tool_call").GetProperty("id").GetString());
        Assert.Equal("increment_counter", doc.RootElement.GetProperty("tool_call").GetProperty("name").GetString());
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static async Task<List<AgentResponseUpdate>> RunBridge(StubAgent inner, List<ChatMessage> messages)
    {
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in ToolApprovalBridgeMiddleware.RunStreamingAsync(messages, session: null, options: null, inner, CancellationToken.None))
        {
            updates.Add(update);
        }
        return updates;
    }

    private static List<ChatMessage> ToolResultMessage(string callId, JsonElement result)
        => [new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result)])];

    private static JsonElement ResponseJson(
        bool approved,
        string? reason = null,
        string? alwaysApprove = null,
        string id = "ficc_call_1",
        string callId = "call_1")
        => JsonSerializer.SerializeToElement(new
        {
            id,
            approved,
            reason,
            always_approve = alwaysApprove,
            tool_call = new
            {
                id = callId,
                name = "increment_counter",
                arguments = new { amount = 1 },
            },
        });

    private sealed class StubAgent : AIAgent
    {
        public AgentResponseUpdate[] UpdatesToYield { get; init; } = [];
        public List<ChatMessage>? ReceivedMessages { get; private set; }
        public bool RunAsyncCalled { get; private set; }
        public bool RunStreamingAsyncCalled { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            RunAsyncCalled = true;
            ReceivedMessages = messages.ToList();
            return BuildResponseAsync(UpdatesToYield, cancellationToken);
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RunStreamingAsyncCalled = true;
            ReceivedMessages = messages.ToList();
            foreach (var update in UpdatesToYield)
            {
                await Task.Yield();
                yield return update;
            }
        }

        private static async Task<AgentResponse> BuildResponseAsync(IEnumerable<AgentResponseUpdate> updates, CancellationToken cancellationToken)
        {
            return await GetUpdates(updates, cancellationToken).ToAgentResponseAsync().ConfigureAwait(false);

            static async IAsyncEnumerable<AgentResponseUpdate> GetUpdates(
                IEnumerable<AgentResponseUpdate> updates,
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                foreach (var update in updates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                    yield return update;
                }
            }
        }
    }
}
