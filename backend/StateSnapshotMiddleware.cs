using AGUI.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticTodos.Backend;

/// <summary>
/// Agent-level streaming middleware that round-trips the AG-UI conversation state: it reads the state
/// the client sent with the run, makes it available to the model and to tools, and emits the updated
/// state back as a <see cref="ConversationStateContent"/> — which the mapping registered in
/// <see cref="AGUIEndpoint.CreateStreamOptions"/> turns into a <c>STATE_SNAPSHOT</c> event.
/// Mirrors <see cref="DetectMcpAppsActivityMiddleware"/> and <see cref="EUAIActRiskActivityMiddleware"/>.
/// </summary>
internal static class StateSnapshotMiddleware
{
    /// <summary>Key under which the run's state is published for tools; see <c>increment_counter</c>.</summary>
    internal const string StatePropertyName = "my_state";

    public class ConversationState
    {
        [JsonPropertyName("selectedResources")]
        public List<string> SelectedResources { get; set; } = [];

        [JsonPropertyName("counter")]
        public int Counter { get; set; }
    }

    extension(AIAgentBuilder agentBuilder)
    {
        public AIAgentBuilder UseStateSnapshot() => agentBuilder.Use(runFunc: RunAsync, runStreamingFunc: RunStreamingAsync);
    }

    /// <summary>
    /// Reads the state a run published for tools. Returns <see langword="false"/> when the run carried
    /// no state — the indexer on <see cref="AdditionalPropertiesDictionary"/> throws on a missing key,
    /// so callers must not index it blindly.
    /// </summary>
    internal static bool TryGetState(AgentRunOptions? options, out ConversationState? state)
    {
        state = options?.AdditionalProperties?.TryGetValue(StatePropertyName, out var value) == true
            ? value as ConversationState
            : null;
        return state is not null;
    }

    private static Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
        => RunStreamingAsync(messages, session, options, innerAgent, cancellationToken).ToAgentResponseAsync();

    private static async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
          IEnumerable<ChatMessage> messages,
          AgentSession? session,
          AgentRunOptions? options,
          AIAgent innerAgent,
          [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // materialize state
        var state = GetState(options);

        // make state object available to downstream processings
        // Only when there is state: ChatClientAgent copies these into ChatOptions.AdditionalProperties,
        // and OmitAdditionalPropertiesMiddleware strips by value type — which cannot match a null. So
        // writing the key unconditionally would leak `my_state: null` into the model request.
        if (options is not null && state is not null)
        {
            options.AdditionalProperties ??= [];
            options.AdditionalProperties[StatePropertyName] = state;
        }

        // make state available to LLM
        // Skip injection on any turn that carries tool results. `messages` holds only the new messages
        // of this turn (both clients clear their local list once the server owns the history), so a
        // continuation turn after a client-side tool call starts with a Tool message — and the assistant
        // message bearing the matching tool_calls is already in the persisted history. Prepending at
        // index 0 would land between the two, which the OpenAI Chat Completions API rejects: an
        // assistant message with tool_calls must be followed immediately by its tool messages, with no
        // other message type (system included) in the gap.
        if (state != null && !messages.Any(m => m.Role == ChatRole.Tool))
        {
            // Transient: this describes the current turn only. Persisting it would replay a stale
            // snapshot on every later turn — see TransientChatMessages.
            var stateMessage = new ChatMessage(
                ChatRole.System,
                $"Current conversation state (selected resources / counter):\n```json\n{JsonSerializer.Serialize(state)}\n```")
                .AsTransient();
            messages = messages.Prepend(stateMessage);
        }

        // invoke downstream processings
        await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
        {
            yield return update;
        }

        // give the client a new state
        if (state is not null)
        {
            yield return new AgentResponseUpdate
            {
                Contents = [new ConversationStateContent(JsonSerializer.SerializeToElement(new { conversation = state }))]
            };
        }
    }

    /// <summary>
    /// Reads the state the client sent with the run. The whole AG-UI request rides on
    /// <c>ChatOptions.AdditionalProperties</c> under a single key; <c>TryGetRunAgentInput</c> is the
    /// supported accessor for agents and delegating chat clients.
    /// </summary>
    private static ConversationState? GetState(AgentRunOptions? options)
    {
        if (options is not ChatClientAgentRunOptions { ChatOptions: { } chatOptions }) return null;
        if (!chatOptions.TryGetRunAgentInput(out var input)) return null;
        if (input.State is not { ValueKind: JsonValueKind.Object } stateEl) return null;
        if (!stateEl.TryGetProperty("conversation", out var convEl)) return null;
        return convEl.Deserialize<ConversationState>();
    }
}
