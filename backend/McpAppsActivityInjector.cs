using System.Text.Json;

namespace AgenticTodos.Backend;

/// <summary>
/// SSE event injector for the AG-UI MCP Apps protocol. Replaces
/// <c>TEXT_MESSAGE_CONTENT</c> events whose <c>delta</c> carries an <c>mcp-activity</c> marker
/// with proper <c>ACTIVITY_SNAPSHOT</c> events.
/// <para>
/// Pass <see cref="TryInjectActivitySnapshot"/> as the injector argument to
/// <see cref="SseEventInjectionMiddleware"/>:
/// <code>branch.UseMiddleware&lt;SseEventInjectionMiddleware&gt;(McpAppsActivityInjector.TryInjectActivitySnapshot)</code>
/// </para>
/// </summary>
internal static class McpAppsActivityInjector
{
    /// <summary>
    /// Inspects a single SSE <c>data:</c> event JSON string.
    /// <list type="bullet">
    ///   <item>
    ///     <c>TEXT_MESSAGE_CONTENT</c> whose <c>delta</c> parses as JSON with <c>type == "mcp-activity"</c>:
    ///     suppress the event and emit an <c>ACTIVITY_SNAPSHOT</c> instead.
    ///   </item>
    ///   <item>Everything else: forward unchanged (return empty sequence).</item>
    /// </list>
    /// Returns <c>null</c> to suppress, an empty array to forward unchanged,
    /// or a non-empty array of replacement event JSON strings.
    /// </summary>
    internal static IEnumerable<string>? TryInjectActivitySnapshot(string eventJson)
    {
        using JsonDocument doc = JsonDocument.Parse(eventJson);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp) ||
            typeProp.GetString() != "TEXT_MESSAGE_CONTENT")
            return [];

        if (!root.TryGetProperty("delta", out var deltaProp))
            return [];

        string? deltaText = deltaProp.GetString();
        if (deltaText is null) return [];

        string? messageId, resourceUri, resultJson, toolInputJson;
        try
        {
            using var activityDoc = JsonDocument.Parse(deltaText);
            JsonElement activity = activityDoc.RootElement;

            if (activity.ValueKind != JsonValueKind.Object)
                return [];

            if (!activity.TryGetProperty("type", out var actTypeProp) ||
                actTypeProp.GetString() != "mcp-activity")
                return [];

            messageId = activity.TryGetProperty("messageId", out var mid) ? mid.GetString() : null;
            resourceUri = activity.TryGetProperty("resourceUri", out var ru) ? ru.GetString() : null;
            if (resourceUri is null) return [];

            // GetRawText() copies the JSON fragment to a string before the document is disposed.
            resultJson = activity.TryGetProperty("result", out var result)
                ? result.GetRawText()
                : """{"content":[]}""";
            toolInputJson = activity.TryGetProperty("toolInput", out var toolInput)
                ? toolInput.GetRawText()
                : "{}";
        }
        catch (JsonException)
        {
            return [];
        }

        string encodedMsgId = messageId is null ? "null" : JsonSerializer.Serialize(messageId);
        string encodedUri = JsonSerializer.Serialize(resourceUri);
        string contentJson = $$"""{"resourceUri":{{encodedUri}},"result":{{resultJson}},"toolInput":{{toolInputJson}}}""";
        string activitySnapshot = $$"""{"type":"ACTIVITY_SNAPSHOT","messageId":{{encodedMsgId}},"activityType":"mcp-apps","replace":true,"content":{{contentJson}}}""";

        return [activitySnapshot];
    }
}
