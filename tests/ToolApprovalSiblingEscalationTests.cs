using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using AgenticTodos.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// What happens when the model calls a gated server tool and a client-side (WebMCP) tool in the
/// <b>same</b> response.
/// <para>
/// Normally the WebMCP tool is a bare declaration, which would stop
/// <see cref="FunctionInvokingChatClient"/> before it runs anything; <see cref="ClientToolDeferralMiddleware"/>
/// therefore splits the turn. Only the gated tool prompts, in either order; approving it runs it and releases
/// the WebMCP call in the same run, without consulting the model; and the model then sees the turn whole.
/// </para>
/// <para>
/// For a client that resends the full history — the run's input carrying an earlier call to the WebMCP tool and
/// its result — the SDK installs it as an invocable approval proxy, and the middleware leaves the turn alone. Two documented behaviours then
/// compose: FICC escalates all-or-nothing — if any call in a response needs approval, every sibling call becomes
/// a <see cref="ToolApprovalRequestContent"/> too — and <c>ToolApprovalAgent</c> surfaces multiple unapproved
/// requests <b>one at a time</b>, queueing the rest in the session. Whichever request the model listed second
/// is deferred by one run. Both calls reach their executor, but that path has known gaps in what the model is
/// told afterwards — see human-in-the-loop.md (Constraints, "Clients that resend the full history").
/// </para>
/// </summary>
public class ToolApprovalSiblingEscalationTests : IDisposable
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Split_GatedServerAndWebMcpCall_EitherOrder_OnlyTheGatedToolPrompts(bool gatedFirst)
    {
        FunctionCallContent[] calls = [IncrementCounter("call_1"), AddTodo("call_2")];
        var contents = await RunPipelineAsync(gatedFirst ? calls : [.. calls.Reverse()]);

        // The gated server tool becomes the interrupt. The WebMCP call is held back by the split: no tool
        // call for the browser yet, and no escalated approval request for it either.
        var interrupt = Assert.IsType<InterruptRequestContent>(Assert.Single(contents));
        Assert.Equal("call_1", interrupt.ToolCallId);
    }

    [Fact]
    public async Task Split_AllThreeRuns_TheWebMcpCallIsReleasedInTheApprovalRun_ModelNotConsulted()
    {
        // Uses the app's real history stack, because run 3 reads the turn back from it.
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [IncrementCounter("call_1"), AddTodo("call_2")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "all done")),
        ]);
        var agent = BuildAgent(scripted, new FileSystemChatHistoryProvider(_historyDir));
        var session = await agent.CreateSessionAsync();

        // Run 1 — the gated server tool prompts; the WebMCP call is held back.
        var run1 = await RunAsync(agent, session, new RunAgentInput
        {
            ThreadId = "t1",
            RunId = "r1",
            Messages = [UserMessage],
            Tools = [new AGUITool { Name = "add_todo" }],
        });
        var interrupt = Assert.IsType<InterruptRequestContent>(Assert.Single(run1));
        Assert.Equal("call_1", interrupt.ToolCallId);

        // Run 2 — the user approves. The gated tool runs, and the held WebMCP call goes to the browser in
        // the same run, as a plain tool call.
        var run2 = await RunAsync(agent, session, Approve(interrupt));
        var result = Assert.Single(run2.OfType<FunctionResultContent>());
        Assert.Equal("call_1", result.CallId);
        Assert.Contains("counter incremented", result.Result?.ToString());
        Assert.Equal(["call_1", "call_2"], run2.OfType<FunctionCallContent>().Select(c => c.CallId));
        Assert.DoesNotContain(run2, c => c is ToolApprovalRequestContent or InterruptRequestContent);
        Assert.Equal(1, scripted.CallCount); // the model was not consulted again

        // Run 3 — the browser's result arrives and the model answers, seeing its one turn whole.
        var run3 = await RunAsync(agent, session, new RunAgentInput
        {
            ThreadId = "t1",
            RunId = "r3",
            Messages = [new AGUIToolMessage { Id = "m3", ToolCallId = "call_2", Content = "added todo 7" }],
            Tools = [new AGUITool { Name = "add_todo" }],
        });
        Assert.Contains("all done", string.Concat(run3.OfType<TextContent>().Select(t => t.Text)));
        Assert.Equal(2, scripted.CallCount);
        Assert.Contains(scripted.LastMessages, m =>
            m.Role == ChatRole.Assistant
            && m.Contents.OfType<FunctionCallContent>().Select(c => c.CallId).SequenceEqual(["call_1", "call_2"]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FullHistoryClient_WebMcpToolIsAnApprovalProxy_WhicheverCallIsListedSecondWaitsOneRun(bool gatedFirst)
    {
        // The run's input carries an earlier add_todo call and its result, as a full-history client sends it, which
        // makes the SDK install add_todo as an invocable approval proxy, so the turn is not split: the escalation decides, and queues the second request.
        FunctionCallContent[] calls = [IncrementCounter("call_1"), AddTodo("call_2")];
        var agent = BuildAgent(
            new ScriptedChatClient(
            [
                new ChatResponse(new ChatMessage(ChatRole.Assistant, gatedFirst ? calls : [.. calls.Reverse()])),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")),
            ]),
            historyProvider: null);
        var input = AnswerAddTodo("call_0");
        input.Messages = [.. input.Messages, new AGUIUserMessage { Id = "m4", Content = Prompt }];

        var contents = await RunAsync(agent, await agent.CreateSessionAsync(), input);

        if (gatedFirst)
        {
            // The approval card now; the WebMCP call's escalated request waits in the session queue.
            var interrupt = Assert.IsType<InterruptRequestContent>(Assert.Single(contents));
            Assert.Equal("call_1", interrupt.ToolCallId);
        }
        else
        {
            // The WebMCP call's escalated request passes through unconverted — the SDK maps it to TOOL_CALL_*
            // so the browser executes it — and the approval card waits.
            var request = Assert.IsType<ToolApprovalRequestContent>(Assert.Single(contents));
            Assert.Equal("call_2", Assert.IsType<FunctionCallContent>(request.ToolCall).CallId);
        }
    }

    [Fact]
    public async Task FullHistoryClient_AProxiedAndABareClientCall_BothReachTheClient()
    {
        // A client that resends the full history makes the SDK install add_todo — answered in that history — as an
        // approval proxy, while change_background_color stays a bare declaration. Split, the bare call would be held
        // behind the proxy's call, which the normalizer later removes from history, so it would never be released.
        // Left to the loop, both are escalated and surface one run after the other.
        var agent = BuildAgent(
            new ScriptedChatClient(
            [
                new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    AddTodo("call_2"),
                    new FunctionCallContent("call_3", "change_background_color", new Dictionary<string, object?> { ["color"] = "green" }),
                ])),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "all done")),
            ]),
            historyProvider: null);
        var session = await agent.CreateSessionAsync();
        AGUITool[] tools = [new AGUITool { Name = "add_todo" }, new AGUITool { Name = "change_background_color" }];
        var input = AnswerAddTodo("call_0");
        input.Messages = [.. input.Messages, new AGUIUserMessage { Id = "m4", Content = Prompt }];
        input.Tools = tools;

        var run1 = await RunAsync(agent, session, input);
        var run2 = await RunAsync(agent, session, new RunAgentInput
        {
            ThreadId = "t1",
            RunId = "r2",
            Messages = [new AGUIToolMessage { Id = "m5", ToolCallId = "call_2", Content = "added todo 8" }],
            Tools = tools,
        });

        var toClient = run1.Concat(run2)
            .Select(c => c switch
            {
                ToolApprovalRequestContent { ToolCall: FunctionCallContent call } => call.CallId,
                FunctionCallContent call => call.CallId,
                _ => null,
            })
            .OfType<string>();
        Assert.Contains("call_2", toClient);
        Assert.Contains("call_3", toClient);
    }

    [Fact]
    public async Task FullHistoryClient_AllThreeRuns_RecoverBothCalls_AtTheCostOfOneExtraRoundTrip()
    {
        // Uses the app's real history stack, because run 3 depends on it: the add_todo approval request
        // persisted in run 1 is an orphan by run 3, and FICC throws "no matching
        // ToolApprovalResponseContent" on it unless the normalizer scrubs it first.
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [IncrementCounter("call_1"), AddTodo("call_2")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "all done")),
        ]);
        var agent = BuildAgent(scripted, new FileSystemChatHistoryProvider(_historyDir));
        var session = await agent.CreateSessionAsync();
        var input = AnswerAddTodo("call_0");
        input.Messages = [.. input.Messages, new AGUIUserMessage { Id = "m4", Content = Prompt }];

        // Run 1 — the approval card; the WebMCP call's escalated request is queued.
        var interrupt = Assert.IsType<InterruptRequestContent>(Assert.Single(await RunAsync(agent, session, input)));

        // Run 2 — the decision is collected, and the queued request goes to the browser.
        var run2 = await RunAsync(agent, session, Approve(interrupt));
        Assert.IsType<ToolApprovalRequestContent>(Assert.Single(run2));

        // Run 3 — the browser's result arrives, the collected approval is replayed, and the gated tool
        // finally executes.
        var run3 = await RunAsync(agent, session, new RunAgentInput
        {
            ThreadId = "t1",
            RunId = "r3",
            Messages = [new AGUIToolMessage { Id = "m5", ToolCallId = "call_2", Content = "added todo 8" }],
            Tools = [new AGUITool { Name = "add_todo" }],
        });
        var result = Assert.Single(run3.OfType<FunctionResultContent>(), r => r.CallId == "call_1");
        Assert.Contains("counter incremented", result.Result?.ToString());
        Assert.Contains("all done", string.Concat(run3.OfType<TextContent>().Select(t => t.Text)));

        // Every result the model sees has its call: the escalated add_todo call was executed by the browser, so
        // the history normalizer keeps the request's call when it scrubs the request.
        var sent = scripted.LastMessages.SelectMany(m => m.Contents).ToList();
        Assert.All(sent.OfType<FunctionResultContent>(), r =>
            Assert.Contains(sent.OfType<FunctionCallContent>(), c => c.CallId == r.CallId));
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private const string Prompt = "increment the counter and add a todo";

    private static AGUIUserMessage UserMessage => new() { Id = "m1", Content = Prompt };

    private static FunctionCallContent IncrementCounter(string callId) =>
        new(callId, "increment_counter", new Dictionary<string, object?>());

    private static FunctionCallContent AddTodo(string callId) =>
        new(callId, "add_todo", new Dictionary<string, object?> { ["title"] = "milk" });

    /// <summary>The run that answers <paramref name="interrupt"/> with an approval.</summary>
    private static RunAgentInput Approve(InterruptRequestContent interrupt) => new()
    {
        ThreadId = "t1",
        RunId = "r2",
        Messages = [UserMessage],
        Tools = [new AGUITool { Name = "add_todo" }],
        Resume =
        [
            new AGUIResume
            {
                InterruptId = interrupt.RequestId,
                Status = ResumeStatus.Resolved,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    toolCall = new { callId = "call_1", name = "increment_counter", arguments = new { } },
                    approved = true,
                }),
            }
        ],
    };

    /// <summary>The browser's run that answers the add_todo call <paramref name="callId"/>.</summary>
    private static RunAgentInput AnswerAddTodo(string callId) => new()
    {
        ThreadId = "t1",
        RunId = "r3",
        Messages =
        [
            UserMessage,
            new AGUIAssistantMessage
            {
                Id = "m2",
                ToolCalls =
                [
                    new AGUIToolCall
                    {
                        Id = callId,
                        Function = new AGUIToolCallFunction { Name = "add_todo", Arguments = """{"title":"milk"}""" },
                    }
                ],
            },
            new AGUIToolMessage { Id = "m3", ToolCallId = callId, Content = "added todo 7" },
        ],
        Tools = [new AGUITool { Name = "add_todo" }],
    };

    /// <summary>Temp directory for the one test that needs the real history provider.</summary>
    private readonly string _historyDir = Path.Combine(Path.GetTempPath(), "hitl-sibling-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_historyDir))
        {
            Directory.Delete(_historyDir, recursive: true);
        }
    }

    /// <summary>
    /// Runs the app's approval pipeline over a scripted model response containing <paramref name="calls"/>,
    /// with <c>increment_counter</c> gated server-side and <c>add_todo</c> declared by the client.
    /// </summary>
    private static async Task<List<AIContent>> RunPipelineAsync(params FunctionCallContent[] calls)
    {
        var agent = BuildAgent(
            new ScriptedChatClient(
            [
                new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. calls])),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")),
            ]),
            historyProvider: null);

        return await RunAsync(agent, await agent.CreateSessionAsync(), new RunAgentInput
        {
            ThreadId = "t1",
            RunId = "r1",
            Messages = [UserMessage],
            Tools = [new AGUITool { Name = "add_todo" }],
        });
    }

    /// <summary>
    /// Mirrors the Program.cs pipeline: <c>UseClientToolDeferral</c> below the function-invocation loop, a
    /// <see cref="ChatClientAgent"/> holding the gated server tool, wrapped by <c>UseToolApprovalInterrupts</c>
    /// (outer) → <c>UseToolApproval</c> (inner).
    /// </summary>
    private static AIAgent BuildAgent(IChatClient chatClient, ChatHistoryProvider? historyProvider)
        => new ChatClientAgent(chatClient.AsBuilder().UseClientToolDeferral().Build(), new ChatClientAgentOptions
        {
            Name = "approval-pipeline-test",
            ChatHistoryProvider = historyProvider,
            ChatOptions = new ChatOptions
            {
                Tools =
                [
                    new ApprovalRequiredAIFunction(
                        AIFunctionFactory.Create(() => "counter incremented", "increment_counter", "Increment the counter.")),
                ],
            },
        })
        .AsBuilder()
        .UseToolApprovalInterrupts()
        .UseToolApproval(new ToolApprovalAgentOptions())
        .Build();

    /// <summary>
    /// Drives one AG-UI run the way <c>AGUIEndpoint</c> does: the messages and chat options both come
    /// from the request's <see cref="ChatRequestContext"/>, over a session shared across runs.
    /// </summary>
    private static async Task<List<AIContent>> RunAsync(AIAgent agent, AgentSession session, RunAgentInput input)
    {
        var context = input.ToChatRequestContext(AguiJson.Options);
        var options = new ChatClientAgentRunOptions { ChatOptions = context.ChatOptions };

        List<AIContent> contents = [];
        await foreach (var update in agent.RunStreamingAsync(context.Messages, session, options))
        {
            contents.AddRange(update.Contents);
        }
        return contents;
    }

    /// <summary>An <see cref="IChatClient"/> replaying canned responses in order.</summary>
    private sealed class ScriptedChatClient(IReadOnlyList<ChatResponse> responses) : IChatClient
    {
        /// <summary>How many times the model has been consulted, to prove a run short-circuited.</summary>
        public int CallCount { get; private set; }

        /// <summary>The messages of the latest call that reached the model.</summary>
        public List<ChatMessage> LastMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = responses[Math.Min(CallCount, responses.Count - 1)];
            CallCount++;
            LastMessages = [.. messages];
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
