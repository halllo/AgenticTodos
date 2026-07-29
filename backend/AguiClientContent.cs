using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// Content the agent pipeline emits for the <i>client</i> rather than for the model.
/// <para>
/// The AG-UI server SDK translates the <see cref="AIContent"/> it knows (text, reasoning, tool
/// calls/results, tool approvals, interrupts) into protocol events and passes everything else to the
/// <c>MapContent</c> fallbacks configured on <c>AGUIStreamOptions</c>. Adding an event kind is
/// therefore two steps: emit one of the content types below from an agent middleware, and map it in
/// <see cref="AGUIEndpoint.CreateStreamOptions"/>.
/// </para>
/// <para>
/// Every type here must be registered via <c>JsonSerializerOptions.AddAIContentType</c> (see
/// <see cref="AGUIEndpoint.ConfigureAguiJson"/>): the SDK serializes each update into the event's
/// <c>rawEvent</c> field, and <see cref="AIContent"/> polymorphism fails serialization for
/// unregistered subtypes.
/// </para>
/// </summary>
internal sealed class ConversationStateContent(JsonElement snapshot) : AIContent
{
    /// <summary>The full conversation state, emitted as the AG-UI <c>STATE_SNAPSHOT</c> payload.</summary>
    public JsonElement Snapshot { get; } = snapshot;
}

/// <summary>
/// An MCP Apps UI resource to render inline, emitted after a tool call whose MCP tool declares a
/// <c>ui.resourceUri</c>. Becomes an <c>ACTIVITY_SNAPSHOT</c> with activity type <c>mcp-apps</c>.
/// </summary>
internal sealed class McpAppActivityContent(
    string messageId,
    string resourceUri,
    JsonElement result,
    JsonElement toolInput) : AIContent
{
    /// <summary>Identity of the activity; re-emitting the same id replaces the rendered app.</summary>
    public string MessageId { get; } = messageId;

    public string ResourceUri { get; } = resourceUri;

    /// <summary>The tool result in MCP <c>CallToolResult</c> shape.</summary>
    public JsonElement Result { get; } = result;

    public JsonElement ToolInput { get; } = toolInput;
}

/// <summary>
/// An EU AI Act verdict for the current turn, emitted only for <c>High</c> risk or above.
/// Becomes an <c>ACTIVITY_SNAPSHOT</c> with activity type <c>eu-ai-act-risk</c>.
/// </summary>
internal sealed class EUAIActRiskActivityContent(
    string messageId,
    string risk,
    string category,
    string reason) : AIContent
{
    public string MessageId { get; } = messageId;

    public string Risk { get; } = risk;

    public string Category { get; } = category;

    public string Reason { get; } = reason;
}
