using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using AgenticTodos.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

public class ToolApprovalInterruptMiddlewareTests
{
    // ---------------------------------------------------------------------------
    // Outbound — ToolApprovalRequestContent → AG-UI InterruptRequestContent
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Outbound_ApprovalRequest_ConvertedToInterruptRequest()
    {
        var request = new ToolApprovalRequestContent(
            "ficc_call_1",
            new FunctionCallContent("call_1", "increment_counter", new Dictionary<string, object?> { ["amount"] = 1 }));
        var inner = new StubAgent { UpdatesToYield = [new AgentResponseUpdate(ChatRole.Assistant, [request])] };

        var updates = await RunMiddleware(inner, []);

        var interrupt = Assert.IsType<InterruptRequestContent>(Assert.Single(Assert.Single(updates).Contents));
        Assert.Equal("ficc_call_1", interrupt.RequestId);
        // Not InterruptReasons.ToolCall — see the middleware's remarks on client correlation.
        Assert.Equal(InterruptReasons.Confirmation, interrupt.Reason);
        Assert.Equal("call_1", interrupt.ToolCallId);
        Assert.Contains("increment_counter", interrupt.Message);

        var toolCall = interrupt.Metadata!.Value.GetProperty("toolCall");
        Assert.Equal("call_1", toolCall.GetProperty("callId").GetString());
        Assert.Equal("increment_counter", toolCall.GetProperty("name").GetString());
        Assert.Equal(1, toolCall.GetProperty("arguments").GetProperty("amount").GetInt32());
    }

    [Fact]
    public async Task Outbound_Interrupt_AdvertisesResponseSchema()
    {
        var request = new ToolApprovalRequestContent("ficc_call_1", new FunctionCallContent("call_1", "increment_counter"));
        var inner = new StubAgent { UpdatesToYield = [new AgentResponseUpdate(ChatRole.Assistant, [request])] };

        var updates = await RunMiddleware(inner, []);

        var interrupt = Assert.IsType<InterruptRequestContent>(Assert.Single(Assert.Single(updates).Contents));
        var required = interrupt.ResponseSchema!.Value.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("approved", required);
        Assert.Contains("toolCall", required);
    }

    [Fact]
    public async Task Outbound_NonApprovalUpdate_PassesThroughUnchanged()
    {
        var update = new AgentResponseUpdate(ChatRole.Assistant, [new TextContent("hello")]);
        var inner = new StubAgent { UpdatesToYield = [update] };

        var updates = await RunMiddleware(inner, []);

        Assert.Same(update, Assert.Single(updates));
        Assert.Equal("hello", Assert.IsType<TextContent>(Assert.Single(updates[0].Contents)).Text);
    }

    [Fact]
    public async Task Outbound_MixedContents_ConvertsOnlyApprovalRequestAndPreservesOrder()
    {
        var request = new ToolApprovalRequestContent("ficc_call_1", new FunctionCallContent("call_1", "increment_counter"));
        var inner = new StubAgent
        {
            UpdatesToYield = [new AgentResponseUpdate(ChatRole.Assistant, [new TextContent("let me ask"), request])]
        };

        var updates = await RunMiddleware(inner, []);

        var contents = Assert.Single(updates).Contents;
        Assert.Equal(2, contents.Count);
        Assert.Equal("let me ask", Assert.IsType<TextContent>(contents[0]).Text);
        Assert.IsType<InterruptRequestContent>(contents[1]);
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
                    new ToolApprovalRequestContent("ficc_call_2", new FunctionCallContent("call_2", "increment_counter")),
                ])
            ]
        };

        var updates = await RunMiddleware(inner, []);

        var interrupts = Assert.Single(updates).Contents.OfType<InterruptRequestContent>().ToList();
        Assert.Equal(["ficc_call_1", "ficc_call_2"], interrupts.Select(i => i.RequestId));
    }

    [Fact]
    public async Task Outbound_ApprovalRequestForClientDeclaredTool_PassesThroughUnchanged()
    {
        // A client-side (WebMCP) tool must keep travelling as an ordinary tool call so the SDK maps it
        // to TOOL_CALL_* and the client executes it. Converting its approval request would show an
        // approval card for a frontend tool and, once approved, replay the stale result the SDK's
        // continuation proxy returns instead of calling the tool again — see
        // Sdk_ContinuationTurn_ResolvedClientToolBecomesApprovalRequiredProxy for where those requests
        // actually come from.
        var request = new ToolApprovalRequestContent(
            "ficc_call_1",
            new FunctionCallContent("call_1", "add_todo"));
        var inner = new StubAgent { UpdatesToYield = [new AgentResponseUpdate(ChatRole.Assistant, [request])] };

        var updates = await RunMiddleware(inner, [], RunWithClientTools("add_todo"));

        Assert.Same(request, Assert.Single(Assert.Single(updates).Contents));
    }

    [Fact]
    public async Task Outbound_ServerToolApprovalRequest_StillConvertedWhenClientToolsDeclared()
    {
        // The mirror of the test above, and the reason this middleware exists: with client tools in
        // play the SDK will not surface a gated server-side tool at all.
        var request = new ToolApprovalRequestContent(
            "ficc_call_1",
            new FunctionCallContent("call_1", "increment_counter"));
        var inner = new StubAgent { UpdatesToYield = [new AgentResponseUpdate(ChatRole.Assistant, [request])] };

        var updates = await RunMiddleware(inner, [], RunWithClientTools("add_todo"));

        var interrupt = Assert.IsType<InterruptRequestContent>(Assert.Single(Assert.Single(updates).Contents));
        Assert.Equal("ficc_call_1", interrupt.RequestId);
    }

    [Fact]
    public async Task Outbound_ConvertedUpdate_DropsRawRepresentation()
    {
        // AsChatResponseUpdate() returns RawRepresentation verbatim when it holds a ChatResponseUpdate,
        // which would discard the converted Contents on the way to the event stream.
        var request = new ToolApprovalRequestContent("ficc_call_1", new FunctionCallContent("call_1", "increment_counter"));
        var update = new AgentResponseUpdate(ChatRole.Assistant, [request])
        {
            RawRepresentation = new ChatResponseUpdate(ChatRole.Assistant, [request]),
        };
        var inner = new StubAgent { UpdatesToYield = [update] };

        var updates = await RunMiddleware(inner, []);

        var converted = Assert.Single(updates);
        Assert.Null(converted.RawRepresentation);
        Assert.IsType<InterruptRequestContent>(Assert.Single(converted.AsChatResponseUpdate().Contents));
    }

    [Fact]
    public async Task RunAsync_DelegatesToDownstreamNonStreamingPath()
    {
        var request = new ToolApprovalRequestContent("ficc_call_1", new FunctionCallContent("call_1", "increment_counter"));
        var inner = new StubAgent { UpdatesToYield = [new AgentResponseUpdate(ChatRole.Assistant, [request])] };

        var response = await ToolApprovalInterruptMiddleware.RunAsync([], session: null, options: null, inner, CancellationToken.None);

        Assert.True(inner.RunAsyncCalled);
        Assert.False(inner.RunStreamingAsyncCalled);
        var interrupt = Assert.IsType<InterruptRequestContent>(Assert.Single(response.Messages.Single().Contents));
        Assert.Equal("ficc_call_1", interrupt.RequestId);
    }

    // ---------------------------------------------------------------------------
    // SDK assumptions — where a client tool's approval request can come from at all
    // ---------------------------------------------------------------------------

    [Fact]
    public void Sdk_FirstTurn_ClientToolsInstalledAsDeclarationsNotApprovalRequired()
    {
        // Bounds the problem the client-tool exclusion solves. AGUITool.AsAITools() goes through
        // AIFunctionFactory.CreateDeclaration, so a client tool is an AIFunctionDeclaration and not an
        // AIFunction — and ConfigureForMixedInvocation only wraps AIFunctions. FICC therefore cannot
        // invoke it and hands the FunctionCallContent straight back, so an ordinary first-turn WebMCP
        // call never becomes a ToolApprovalRequestContent in the first place.
        var input = new RunAgentInput
        {
            ThreadId = "t1",
            RunId = "r1",
            Messages = [new AGUIUserMessage { Id = "m1", Content = "add a todo" }],
            Tools = [new AGUITool { Name = "add_todo" }],
        };

        var tool = Assert.Single(input.ToChatRequestContext(AguiJson.Options).ChatOptions.Tools!);

        Assert.Equal("add_todo", tool.Name);
        Assert.Null(tool.GetService<ApprovalRequiredAIFunction>());
        Assert.IsNotAssignableFrom<AIFunction>(tool);
    }

    [Fact]
    public async Task Sdk_ContinuationTurn_ResolvedClientToolBecomesApprovalRequiredProxy()
    {
        // The case the exclusion actually guards. Once a client tool has returned a result this turn,
        // AGUI.Server.ProcessContinuation replaces it with an ApprovalRequiredAIFunction over a proxy
        // that replays that result, so a *repeat* call to the same tool does reach FICC as an approval
        // request. Converting it would both show an approval card for a frontend tool and, on approval,
        // return the previous result instead of calling the tool again.
        var input = new RunAgentInput
        {
            ThreadId = "t1",
            RunId = "r1",
            Messages =
            [
                new AGUIUserMessage { Id = "m1", Content = "add a todo" },
                new AGUIAssistantMessage
                {
                    Id = "m2",
                    ToolCalls =
                    [
                        new AGUIToolCall
                        {
                            Id = "call_1",
                            Function = new AGUIToolCallFunction { Name = "add_todo", Arguments = """{"title":"milk"}""" },
                        }
                    ],
                },
                new AGUIToolMessage { Id = "m3", ToolCallId = "call_1", Content = "added todo 7" },
            ],
            Tools = [new AGUITool { Name = "add_todo" }],
        };

        var tool = Assert.Single(input.ToChatRequestContext(AguiJson.Options).ChatOptions.Tools!);

        Assert.Equal("add_todo", tool.Name);
        Assert.NotNull(tool.GetService<ApprovalRequiredAIFunction>());
        // The stale replay: the proxy ignores the new arguments and returns the previous result.
        var result = await Assert.IsAssignableFrom<AIFunction>(tool)
            .InvokeAsync(new AIFunctionArguments { ["title"] = "bread" });
        Assert.Contains("added todo 7", result?.ToString());
    }

    // ---------------------------------------------------------------------------
    // Inbound — "always allow" upgrades on the approval response the SDK decoded
    // ---------------------------------------------------------------------------

    [Fact]
    public void Inbound_SdkDecodesResumePayloadIntoApprovalPair()
    {
        // Guards the assumption the middleware is built on: a resume payload carrying a toolCall is
        // turned into an approval request/response pair by the SDK, so only the "always allow"
        // upgrade is left to do here.
        var (messages, _) = ResumeRequest(approved: true);

        Assert.Single(messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());
        var response = Assert.Single(messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>());
        Assert.True(response.Approved);
        Assert.Equal("ficc_call_1", response.RequestId);
    }

    [Fact]
    public void Inbound_WithoutAlwaysApprove_ResponsePassesThroughUnchanged()
    {
        var (messages, options) = ResumeRequest(approved: true);

        var result = ToolApprovalInterruptMiddleware.ApplyAlwaysApproveRules(messages, options).ToList();

        var response = Assert.Single(Responses(result));
        Assert.True(response.Approved);
        Assert.Empty(AlwaysApproveResponses(result));
    }

    [Fact]
    public void Inbound_AlwaysApproveTool_UpgradesResponse()
    {
        var (messages, options) = ResumeRequest(approved: true, alwaysApprove: "tool");

        var result = ToolApprovalInterruptMiddleware.ApplyAlwaysApproveRules(messages, options).ToList();

        var upgraded = Assert.Single(AlwaysApproveResponses(result));
        Assert.True(upgraded.AlwaysApproveTool);
        Assert.False(upgraded.AlwaysApproveToolWithArguments);
        Assert.Equal("ficc_call_1", upgraded.InnerResponse.RequestId);
        Assert.True(upgraded.InnerResponse.Approved);
    }

    [Fact]
    public void Inbound_AlwaysApproveToolWithArguments_UpgradesToArgumentScopedResponse()
    {
        var (messages, options) = ResumeRequest(approved: true, alwaysApprove: "tool_with_arguments");

        var result = ToolApprovalInterruptMiddleware.ApplyAlwaysApproveRules(messages, options).ToList();

        var upgraded = Assert.Single(AlwaysApproveResponses(result));
        Assert.True(upgraded.AlwaysApproveToolWithArguments);
    }

    [Fact]
    public void Inbound_AlwaysApproveCombinedWithRejection_StaysARejection()
    {
        // A standing "always allow" rule must never resurrect a call the user just declined.
        var (messages, options) = ResumeRequest(approved: false, alwaysApprove: "tool");

        var result = ToolApprovalInterruptMiddleware.ApplyAlwaysApproveRules(messages, options).ToList();

        Assert.False(Assert.Single(Responses(result)).Approved);
        Assert.Empty(AlwaysApproveResponses(result));
    }

    [Fact]
    public void Inbound_AlwaysApproveForDifferentInterrupt_LeavesResponseAlone()
    {
        var (messages, options) = ResumeRequest(approved: true, alwaysApprove: "tool", interruptId: "ficc_call_1");
        // Rules are keyed by interrupt id; a rule for another interrupt must not leak across.
        var (_, otherOptions) = ResumeRequest(approved: true, alwaysApprove: "tool", interruptId: "ficc_call_other");

        var result = ToolApprovalInterruptMiddleware.ApplyAlwaysApproveRules(messages, otherOptions).ToList();

        Assert.Single(Responses(result));
        Assert.Empty(AlwaysApproveResponses(result));
    }

    [Fact]
    public void Inbound_WithoutAguiInput_MessagesUntouched()
    {
        var (messages, _) = ResumeRequest(approved: true);

        var result = ToolApprovalInterruptMiddleware.ApplyAlwaysApproveRules(messages, options: null);

        Assert.Same(messages, result);
    }

    [Fact]
    public async Task Inbound_UpgradedResponse_ReachesTheInnerAgent()
    {
        // The upgrade is only worth anything if the messages the middleware rewrote are the ones the
        // inner ToolApprovalAgent actually sees — that is what persists the standing rule.
        var (messages, options) = ResumeRequest(approved: true, alwaysApprove: "tool");
        var inner = new StubAgent();

        await RunMiddleware(inner, messages, options);

        Assert.NotNull(inner.ReceivedMessages);
        var upgraded = Assert.Single(AlwaysApproveResponses(inner.ReceivedMessages));
        Assert.True(upgraded.AlwaysApproveTool);
    }

    [Fact]
    public async Task Inbound_UnknownAlwaysApproveScope_DegradesToPlainApproval()
    {
        // A client typo must not silently become a standing rule; one-shot approval is the safe
        // direction, and the run still goes through.
        var (messages, options) = ResumeRequest(approved: true, alwaysApprove: "ALWAYS");
        var inner = new StubAgent();

        await RunMiddleware(inner, messages, options);

        Assert.NotNull(inner.ReceivedMessages);
        Assert.Empty(AlwaysApproveResponses(inner.ReceivedMessages));
        Assert.True(Assert.Single(Responses(inner.ReceivedMessages)).Approved);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static async Task<List<AgentResponseUpdate>> RunMiddleware(
        StubAgent inner,
        List<ChatMessage> messages,
        AgentRunOptions? options = null)
    {
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in ToolApprovalInterruptMiddleware.RunStreamingAsync(messages, session: null, options, inner, CancellationToken.None))
        {
            updates.Add(update);
        }
        return updates;
    }

    /// <summary>Run options carrying the client-side tool declarations an AG-UI request would bring.</summary>
    private static AgentRunOptions RunWithClientTools(params string[] toolNames)
    {
        var input = new RunAgentInput
        {
            ThreadId = "t1",
            RunId = "r1",
            Messages = [],
            Tools = [.. toolNames.Select(name => new AGUITool { Name = name })],
        };

        var context = input.ToChatRequestContext(AguiJson.Options);
        return new ChatClientAgentRunOptions { ChatOptions = context.ChatOptions };
    }

    private static IEnumerable<ToolApprovalResponseContent> Responses(IEnumerable<ChatMessage> messages)
        => messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>();

    private static IEnumerable<AlwaysApproveToolApprovalResponseContent> AlwaysApproveResponses(IEnumerable<ChatMessage> messages)
        => messages.SelectMany(m => m.Contents).OfType<AlwaysApproveToolApprovalResponseContent>();

    /// <summary>
    /// Builds a resume turn the way the AG-UI server SDK does: the messages it derives from the
    /// client's <c>resume</c> entry, plus the run options carrying the originating request.
    /// </summary>
    private static (List<ChatMessage> Messages, AgentRunOptions Options) ResumeRequest(
        bool approved,
        string? alwaysApprove = null,
        string interruptId = "ficc_call_1")
    {
        var input = new RunAgentInput
        {
            ThreadId = "t1",
            RunId = "r1",
            Messages = [],
            Resume =
            [
                new AGUIResume
                {
                    InterruptId = interruptId,
                    Status = ResumeStatus.Resolved,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        toolCall = new { callId = "call_1", name = "increment_counter", arguments = new { amount = 1 } },
                        approved,
                        reason = (string?)null,
                        alwaysApprove,
                    }),
                }
            ],
        };

        var context = input.ToChatRequestContext(AguiJson.Options);
        return (context.Messages, new ChatClientAgentRunOptions { ChatOptions = context.ChatOptions });
    }

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
            return YieldUpdates(cancellationToken).ToAgentResponseAsync(cancellationToken);
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RunStreamingAsyncCalled = true;
            ReceivedMessages = messages.ToList();
            await foreach (var update in YieldUpdates(cancellationToken))
            {
                yield return update;
            }
        }

        private async IAsyncEnumerable<AgentResponseUpdate> YieldUpdates([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in UpdatesToYield)
            {
                await Task.Yield();
                yield return update;
            }
        }
    }
}
