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
/// <b>same</b> response — the one case where the two approval layers cost a round trip.
/// <para>
/// Two documented behaviours compose. FICC escalates all-or-nothing: if any call in a response needs
/// approval, every sibling call becomes a <see cref="ToolApprovalRequestContent"/> too. And
/// <c>ToolApprovalAgent</c> surfaces multiple unapproved requests <b>one at a time</b>, queueing the
/// rest in the session. The escalated client-tool request is not rescued by
/// <c>ApprovalNotRequiredFunctionBypassing</c>, because that only auto-approves non-gated
/// <see cref="AIFunction"/>s and a WebMCP tool arrives as a declaration
/// (see <c>Sdk_FirstTurn_ClientToolsInstalledAsDeclarationsNotApprovalRequired</c>).
/// </para>
/// <para>
/// The result is that whichever request the model listed second is deferred by one run. Nothing is
/// lost — the first two tests pin which half each order defers, and
/// <see cref="AllThreeRuns_RecoverBothCalls_AtTheCostOfOneExtraRoundTrip"/> walks the whole sequence
/// through to both tools having run. See human-in-the-loop.md ("Sibling escalation").
/// </para>
/// </summary>
public class ToolApprovalSiblingEscalationTests : IDisposable
{
    [Fact]
    public async Task GatedServerToolFirst_DefersTheWebMcpCallByOneRun()
    {
        var contents = await RunPipelineAsync(
            new FunctionCallContent("call_1", "increment_counter", new Dictionary<string, object?>()),
            new FunctionCallContent("call_2", "add_todo", new Dictionary<string, object?> { ["title"] = "milk" }));

        // The gated server tool becomes the interrupt, as it should.
        var interrupt = Assert.IsType<InterruptRequestContent>(Assert.Single(contents));
        Assert.Equal("call_1", interrupt.ToolCallId);

        // But add_todo is gone: no tool call for the client to execute, no interrupt either. It sits in
        // the session's QueuedApprovalRequests until the next run.
        Assert.DoesNotContain(contents, c => Names(c).Contains("add_todo"));
    }

    [Fact]
    public async Task WebMcpToolFirst_DefersTheApprovalCardByOneRun()
    {
        var contents = await RunPipelineAsync(
            new FunctionCallContent("call_2", "add_todo", new Dictionary<string, object?> { ["title"] = "milk" }),
            new FunctionCallContent("call_1", "increment_counter", new Dictionary<string, object?>()));

        // The client tool's escalated request passes through unconverted, which is correct on its own —
        // the SDK maps it to TOOL_CALL_* so the browser executes it.
        var request = Assert.IsType<ToolApprovalRequestContent>(Assert.Single(contents));
        Assert.Equal("add_todo", Assert.IsType<FunctionCallContent>(request.ToolCall).Name);

        // But increment_counter does not prompt this run — no interrupt is emitted for it. It waits in
        // the session queue, and run 2 surfaces it.
        Assert.DoesNotContain(contents, c => c is InterruptRequestContent);
    }

    [Fact]
    public async Task AllThreeRuns_RecoverBothCalls_AtTheCostOfOneExtraRoundTrip()
    {
        // The deferral costs a round trip but loses nothing. Uses the app's real history stack, because
        // run 3 depends on it: the add_todo approval request persisted in run 2 is an orphan by run 3,
        // and FICC throws "no matching ToolApprovalResponseContent" on it unless the normalizer scrubs
        // it first (its tool call has a result in history by then).
        var scripted = new ScriptedChatClient(
        [
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call_1", "increment_counter", new Dictionary<string, object?>()),
                new FunctionCallContent("call_2", "add_todo", new Dictionary<string, object?> { ["title"] = "milk" }),
            ])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "all done")),
        ]);
        var agent = BuildAgent(scripted, new FileSystemChatHistoryProvider(_historyDir));
        var session = await agent.CreateSessionAsync();

        // Run 1 — the gated server tool prompts; the WebMCP call is queued.
        var run1 = await RunAsync(agent, session, new RunAgentInput
        {
            ThreadId = "t1",
            RunId = "r1",
            Messages = [UserMessage],
            Tools = [new AGUITool { Name = "add_todo" }],
        });
        var interrupt = Assert.IsType<InterruptRequestContent>(Assert.Single(run1));
        Assert.Equal("call_1", interrupt.ToolCallId);

        // Run 2 — the user approves. ToolApprovalAgent pops the queued request and returns it without
        // invoking the inner agent, so the browser gets its tool call now instead of an answer.
        var run2 = await RunAsync(agent, session, new RunAgentInput
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
        });
        var deferred = Assert.IsType<ToolApprovalRequestContent>(Assert.Single(run2));
        Assert.Equal("add_todo", Assert.IsType<FunctionCallContent>(deferred.ToolCall).Name);
        Assert.Equal(1, scripted.CallCount); // the model was not consulted again

        // Run 3 — the browser's result arrives, the collected approval is replayed, and the gated tool
        // finally executes.
        var run3 = await RunAsync(agent, session, new RunAgentInput
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
                            Id = "call_2",
                            Function = new AGUIToolCallFunction { Name = "add_todo", Arguments = """{"title":"milk"}""" },
                        }
                    ],
                },
                new AGUIToolMessage { Id = "m3", ToolCallId = "call_2", Content = "added todo 7" },
            ],
            Tools = [new AGUITool { Name = "add_todo" }],
        });

        var result = Assert.Single(run3.OfType<FunctionResultContent>());
        Assert.Equal("call_1", result.CallId);
        Assert.Contains("counter incremented", result.Result?.ToString());
        Assert.Contains("all done", string.Concat(run3.OfType<TextContent>().Select(t => t.Text)));
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private const string Prompt = "increment the counter and add a todo";

    private static AGUIUserMessage UserMessage => new() { Id = "m1", Content = Prompt };

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
    /// Mirrors the Program.cs pipeline: a <see cref="ChatClientAgent"/> holding the gated server tool,
    /// wrapped by <c>UseToolApprovalInterrupts</c> (outer) → <c>UseToolApproval</c> (inner).
    /// </summary>
    private static AIAgent BuildAgent(IChatClient chatClient, ChatHistoryProvider? historyProvider)
        => new ChatClientAgent(chatClient, new ChatClientAgentOptions
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

    private static IEnumerable<string> Names(AIContent content) => content switch
    {
        ToolApprovalRequestContent { ToolCall: FunctionCallContent fcc } => [fcc.Name],
        FunctionCallContent fcc => [fcc.Name],
        InterruptRequestContent { Message: { } message } => [message],
        _ => [],
    };

    /// <summary>An <see cref="IChatClient"/> replaying canned responses in order.</summary>
    private sealed class ScriptedChatClient(IReadOnlyList<ChatResponse> responses) : IChatClient
    {
        /// <summary>How many times the model has been consulted, to prove a run short-circuited.</summary>
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = responses[Math.Min(CallCount, responses.Count - 1)];
            CallCount++;
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
