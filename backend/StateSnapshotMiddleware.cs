using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticTodos.Backend;

public static class StateSnapshotMiddleware
{
    public class ConversationState
    {
        [JsonPropertyName("selectedResources")]
        public List<string> SelectedResources { get; set; } = [];

        [JsonPropertyName("counter")]
        public int Counter { get; set; }
    }

    public static Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
        => RunStreamingAsync(messages, session, options, innerAgent, cancellationToken).ToAgentResponseAsync();

    public static async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
          IEnumerable<ChatMessage> messages,
          AgentSession? session,
          AgentRunOptions? options,
          AIAgent innerAgent,
          [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // materialize state
        var state = GetState(options);

        // make state object available to downstream processings
        options?.AdditionalProperties ??= [];
        options?.AdditionalProperties?["my_state"] = state;

        // make state available to LLM
        // Only inject the state snapshot when the conversation doesn't end with a pending tool
        // call/result pair. The OpenAI Chat Completions API requires that an assistant message
        // with tool_calls is immediately followed by the corresponding tool messages — no other
        // message type (including system) may appear between them. Skipping injection here
        // ensures the state snapshot is never inserted into that gap.
        if (state != null && !messages.Any(m => m.Role == ChatRole.Tool))
        {
            var stateMessage = new ChatMessage(ChatRole.System, $"Current conversation state (selected resources / metadata):\n```json\n{JsonSerializer.Serialize(state)}\n```");
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
            var snapshot = JsonSerializer.SerializeToElement(new { conversation = state });
            yield return new AgentResponseUpdate
            {
                Contents = [new DataContent(JsonSerializer.SerializeToUtf8Bytes(snapshot), "application/json")]
            };
        }
    }

        private static ConversationState? GetState(AgentRunOptions? options)
    {
        if (options is not ChatClientAgentRunOptions chatOpts) return null;
        if (chatOpts.ChatOptions?.AdditionalProperties?.TryGetValue("ag_ui_state", out var stateObj) != true) return null;
        if (stateObj is not JsonElement stateEl || stateEl.ValueKind != JsonValueKind.Object) return null;
        if (!stateEl.TryGetProperty("conversation", out var convEl)) return null;
        return convEl.Deserialize<ConversationState>();
    }
}