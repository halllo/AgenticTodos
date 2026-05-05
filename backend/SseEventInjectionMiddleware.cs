using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AgenticTodos.Backend;

/// <summary>
/// ASP.NET Core middleware that uses <see cref="SseInterceptorStream"/> to replace
/// <c>TEXT_MESSAGE_CONTENT</c> events whose <c>delta</c> carries an <c>mcp-activity</c> marker
/// with proper <c>ACTIVITY_SNAPSHOT</c> events, as required by the AG-UI MCP Apps protocol.
/// <para>
/// The marker is emitted by <see cref="McpAppsActivityMiddleware"/> as a
/// <c>DataContent("application/x-mcp-activity")</c>, which the AGUI framework converts to a
/// <c>TEXT_MESSAGE_CONTENT</c> SSE event (keeping it out of the state-snapshot pathway).
/// </para>
/// Register via UseWhen so it only runs for /agents/* requests.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by ASP.NET Core via UseMiddleware<T>")]
internal sealed class SseEventInjectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        Stream originalBody = context.Response.Body;
        using var interceptor = new SseInterceptorStream(originalBody, InjectAfter);
        context.Response.Body = interceptor;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            await interceptor.FlushRemainingAsync().ConfigureAwait(false);
            context.Response.Body = originalBody;
        }
    }

    /// <summary>
    /// Intercepts every SSE <c>data:</c> event.
    /// <list type="bullet">
    ///   <item>
    ///     <c>TEXT_MESSAGE_CONTENT</c> whose <c>delta</c> parses as JSON with <c>type == "mcp-activity"</c>:
    ///     suppress the event and emit an <c>ACTIVITY_SNAPSHOT</c> instead.
    ///   </item>
    ///   <item>Everything else: forward unchanged (return empty sequence).</item>
    /// </list>
    /// </summary>
    private static IEnumerable<string>? InjectAfter(string eventJson)
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

        JsonElement activity;
        try
        {
            using var activityDoc = JsonDocument.Parse(deltaText);
            activity = activityDoc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return [];
        }

        if (!activity.TryGetProperty("type", out var actTypeProp) ||
            actTypeProp.GetString() != "mcp-activity")
            return [];

        string? messageId = activity.TryGetProperty("messageId", out var mid) ? mid.GetString() : null;
        string? resourceUri = activity.TryGetProperty("resourceUri", out var ru) ? ru.GetString() : null;
        if (resourceUri is null) return [];

        string resultJson = activity.TryGetProperty("result", out var result)
            ? result.GetRawText()
            : """{"content":[]}""";
        string toolInputJson = activity.TryGetProperty("toolInput", out var toolInput)
            ? toolInput.GetRawText()
            : "{}";

        string encodedMsgId = messageId is null ? "null" : JsonSerializer.Serialize(messageId);
        string encodedUri = JsonSerializer.Serialize(resourceUri);
        string contentJson = $$"""{"resourceUri":{{encodedUri}},"result":{{resultJson}},"toolInput":{{toolInputJson}}}""";
        string activitySnapshot = $$"""{"type":"ACTIVITY_SNAPSHOT","messageId":{{encodedMsgId}},"activityType":"mcp-apps","replace":true,"content":{{contentJson}}}""";

        return [activitySnapshot];
    }
}
