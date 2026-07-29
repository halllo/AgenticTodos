using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using AgenticTodos.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// Covers the conversation-state round trip: what the middleware hands the model, what it publishes
/// for tools, and what it sends back to the client.
/// </summary>
public class StateSnapshotMiddlewareTests
{
    [Fact]
    public async Task StateFromClient_InjectedForTheModelAndEchoedBack()
    {
        var inner = new StubAgent();

        var updates = await Run(inner, RunWithState(counter: 7));

        var injected = Assert.Single(inner.ReceivedMessages!, m => m.Role == ChatRole.System);
        Assert.Contains("\"counter\":7", injected.Text);

        var echoed = Assert.Single(updates.SelectMany(u => u.Contents).OfType<ConversationStateContent>());
        Assert.Equal(7, echoed.Snapshot.GetProperty("conversation").GetProperty("counter").GetInt32());
    }

    [Fact]
    public async Task InjectedStateMessage_IsTransientSoItIsNeverPersisted()
    {
        // The message describes the *current* turn. Persisting it would replay a stale snapshot on
        // every later turn — the model could read an outdated counter and contradict the live state —
        // and grow the prompt by one block per turn. See TransientChatMessages.
        var inner = new StubAgent();

        await Run(inner, RunWithState(counter: 1));

        var injected = Assert.Single(inner.ReceivedMessages!, m => m.Role == ChatRole.System);
        Assert.True(injected.IsTransient());
    }

    [Fact]
    public async Task StateIsPublishedForToolsUnderTheRunOptions()
    {
        var options = RunWithState(counter: 3);
        var inner = new StubAgent();

        await Run(inner, options);

        Assert.True(StateSnapshotMiddleware.TryGetState(options, out var state));
        Assert.Equal(3, state!.Counter);
    }

    [Fact]
    public void TryGetState_WithoutState_ReturnsFalseInsteadOfThrowing()
    {
        // AdditionalPropertiesDictionary's indexer throws on a missing key, so a run that carried no
        // state must not be read by indexing (increment_counter would throw instead of no-op).
        Assert.False(StateSnapshotMiddleware.TryGetState(new ChatClientAgentRunOptions(), out var state));
        Assert.Null(state);
        Assert.False(StateSnapshotMiddleware.TryGetState(null, out _));

        // The indexer really does throw — this is what TryGetState exists to avoid.
        var empty = new ChatClientAgentRunOptions { AdditionalProperties = [] };
        Assert.Throws<KeyNotFoundException>(() => empty.AdditionalProperties![StateSnapshotMiddleware.StatePropertyName]);
        Assert.False(StateSnapshotMiddleware.TryGetState(empty, out _));
    }

    [Fact]
    public void TryGetState_KeyPresentButNotUsableState_ReturnsFalse()
    {
        var nulled = new ChatClientAgentRunOptions
        {
            AdditionalProperties = new() { [StateSnapshotMiddleware.StatePropertyName] = null },
        };
        Assert.False(StateSnapshotMiddleware.TryGetState(nulled, out _));

        var wrongType = new ChatClientAgentRunOptions
        {
            AdditionalProperties = new() { [StateSnapshotMiddleware.StatePropertyName] = "not a state" },
        };
        Assert.False(StateSnapshotMiddleware.TryGetState(wrongType, out _));
    }

    [Fact]
    public async Task WithoutState_NoStateKeyIsPublished()
    {
        // ChatClientAgent copies AdditionalProperties into ChatOptions, and
        // OmitAdditionalPropertiesMiddleware filters by value type — which cannot match a null. So a
        // stateless run must not leave the key behind at all.
        var options = new ChatClientAgentRunOptions();

        await Run(new StubAgent(), options);

        Assert.True(options.AdditionalProperties is null
            || !options.AdditionalProperties.ContainsKey(StateSnapshotMiddleware.StatePropertyName));
    }

    [Fact]
    public async Task WithoutState_NothingInjectedAndNothingEchoed()
    {
        var inner = new StubAgent();

        var updates = await Run(inner, new ChatClientAgentRunOptions());

        Assert.DoesNotContain(inner.ReceivedMessages!, m => m.Role == ChatRole.System);
        Assert.Empty(updates.SelectMany(u => u.Contents).OfType<ConversationStateContent>());
    }

    [Fact]
    public async Task PendingToolMessages_SuppressInjection()
    {
        // OpenAI requires an assistant message with tool_calls to be followed immediately by its tool
        // messages; a system message must not land in that gap.
        var inner = new StubAgent();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call_1", "increment_counter")]),
            new(ChatRole.Tool, [new FunctionResultContent("call_1", "1")]),
        };

        var updates = await Run(inner, RunWithState(counter: 1), messages);

        Assert.DoesNotContain(inner.ReceivedMessages!, m => m.Role == ChatRole.System);

        // Suppressing the injection must not suppress the echo: the client's copy of the state is
        // refreshed on every turn, so moving this inside the guard above would freeze the counter in the
        // UI from the first continuation turn onwards.
        Assert.Single(updates.SelectMany(u => u.Contents).OfType<ConversationStateContent>());
    }

    [Fact]
    public async Task NonStreamingPath_InjectsAndEchoesToo()
    {
        // RunAsync is implemented as RunStreamingAsync(...).ToAgentResponseAsync(), so this pins that the
        // shim actually carries both halves through rather than dropping the trailing state update.
        var inner = new StubAgent();

        var response = await new AIAgentBuilder(inner).UseStateSnapshot().Build()
            .RunAsync([], session: null, RunWithState(counter: 7));

        Assert.Contains("\"counter\":7", Assert.Single(inner.ReceivedMessages!, m => m.Role == ChatRole.System).Text);
        Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<ConversationStateContent>());
    }

    private static async Task<List<AgentResponseUpdate>> Run(
        StubAgent inner,
        AgentRunOptions options,
        List<ChatMessage>? messages = null)
    {
        var agent = new AIAgentBuilder(inner).UseStateSnapshot().Build();
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync(messages ?? [], session: null, options))
        {
            updates.Add(update);
        }
        return updates;
    }

    /// <summary>Run options carrying the state an AG-UI request would bring.</summary>
    private static AgentRunOptions RunWithState(int counter)
    {
        var input = new RunAgentInput
        {
            ThreadId = "t1",
            RunId = "r1",
            Messages = [],
            State = JsonSerializer.SerializeToElement(new
            {
                conversation = new { selectedResources = new[] { "a.txt" }, counter },
            }),
        };

        var context = input.ToChatRequestContext(AguiJson.Options);
        return new ChatClientAgentRunOptions { ChatOptions = context.ChatOptions };
    }

    private sealed class StubAgent : AIAgent
    {
        public List<ChatMessage>? ReceivedMessages { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
            => RunCoreStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedMessages = messages.ToList();
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.Assistant, [new TextContent("ok")]);
        }
    }
}
