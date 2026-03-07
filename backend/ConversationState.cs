using System.Text.Json.Serialization;

namespace AgenticTodos.Backend;

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
