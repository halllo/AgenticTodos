using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticTodos.Backend;

public static class StateSnapshotEmittingAgentExtensions
{
    public static AIAgent WrapWithStateSnapshot(this AIAgent inner) => new StateSnapshotEmittingAgent(inner);
}

/// <summary>
/// Custom state that is round-tripped via the AG-UI STATE_SNAPSHOT mechanism.
/// The client sends this back in RunAgentInput.state on every turn,
/// and StateSnapshotEmittingAgent reads it and injects it as a system message.
/// </summary>
public class ConversationState
{
    [JsonPropertyName("selectedResources")]
    public List<string> SelectedResources { get; set; } = [];

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; set; } = [];
}

/// <summary>
/// Wraps an inner agent and:
///   1. Injects the incoming ag_ui_state as a system message so the LLM has context.
///   2. Appends a STATE_SNAPSHOT DataContent event after each streaming run so the
///      client can capture and round-trip the state.
///
/// MapAGUI() converts DataContent("application/json") into a STATE_SNAPSHOT SSE event.
/// The client receives it as DataContent in response updates, stores it as bytes, and sends
/// it back as a ChatRole.System DataContent message on the next request.
/// MapAGUI() then extracts that DataContent → ChatOptions.AdditionalProperties["ag_ui_state"].
/// </summary>
public class StateSnapshotEmittingAgent(AIAgent inner) : DelegatingAIAgent(inner)
{
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = GetStateFromOptions(options);
        var augmentedMessages = state is not null
            ? PrependStateMessage(messages, state)
            : messages;

        options?.AdditionalProperties?["my_state"] = state;

        await foreach (var update in InnerAgent.RunStreamingAsync(augmentedMessages, session, options, cancellationToken))
            yield return update;

        if (state is not null)
        {
            var snapshot = JsonSerializer.SerializeToElement(new { conversation = state });
            yield return new AgentResponseUpdate
            {
                Contents = [new DataContent(JsonSerializer.SerializeToUtf8Bytes(snapshot), "application/json")]
            };
        }
    }

    private static ConversationState? GetStateFromOptions(AgentRunOptions? options)
    {
        if (options is not ChatClientAgentRunOptions chatOpts) return null;
        if (chatOpts.ChatOptions?.AdditionalProperties?.TryGetValue("ag_ui_state", out var stateObj) != true) return null;
        if (stateObj is not JsonElement stateEl || stateEl.ValueKind != JsonValueKind.Object) return null;
        if (!stateEl.TryGetProperty("conversation", out var convEl)) return null;
        return convEl.Deserialize<ConversationState>();
    }

    private static IEnumerable<ChatMessage> PrependStateMessage(
        IEnumerable<ChatMessage> messages,
        ConversationState state)
    {
        if (state.SelectedResources.Count == 0 && state.Metadata.Count == 0)
            return messages;

        var json = JsonSerializer.Serialize(state);
        var msg = new ChatMessage(ChatRole.System,
            $"Current conversation state (selected resources / metadata):\n```json\n{json}\n```");
        return messages.Prepend(msg);
    }
}
