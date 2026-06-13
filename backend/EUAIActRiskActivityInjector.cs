using System.Text.Json;

namespace AgenticTodos.Backend;

/// <summary>
/// SSE event injector that replaces <c>TEXT_MESSAGE_CONTENT</c> events whose <c>delta</c> carries an
/// <c>eu-ai-act-activity</c> marker (emitted by <see cref="EUAIActRiskActivityMiddleware"/>) with an
/// <c>ACTIVITY_SNAPSHOT</c> event whose <c>activityType</c> is <see cref="ActivityType"/>.
/// Companion to <see cref="McpAppsActivityInjector"/>; combined via <see cref="ActivitySnapshotInjectionMiddleware"/>.
/// <para>
/// Returns <c>null</c> to suppress, an empty array to forward unchanged,
/// or a single-element array holding the replacement <c>ACTIVITY_SNAPSHOT</c>.
/// </para>
/// </summary>
internal static class EUAIActRiskActivityInjector
{
    internal const string ActivityType = "eu-ai-act-risk";

    internal static IEnumerable<string>? TryInjectActivitySnapshot(string eventJson)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(eventJson); }
        catch (JsonException) { return []; }
        using (doc)
        {
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp) ||
                typeProp.GetString() != "TEXT_MESSAGE_CONTENT")
                return [];

            // The outer TEXT_MESSAGE_CONTENT always carries a valid messageId — use it as fallback.
            string? outerMessageId = root.TryGetProperty("messageId", out var outerMid) ? outerMid.GetString() : null;

            if (!root.TryGetProperty("delta", out var deltaProp))
                return [];

            string? deltaText = deltaProp.GetString();
            if (deltaText is null) return [];

            string? messageId, risk, category, reason;
            try
            {
                using var activityDoc = JsonDocument.Parse(deltaText);
                JsonElement activity = activityDoc.RootElement;

                if (activity.ValueKind != JsonValueKind.Object)
                    return [];

                if (!activity.TryGetProperty("type", out var actTypeProp) ||
                    actTypeProp.GetString() != "eu-ai-act-activity")
                    return [];

                // Prefer inner messageId, fall back to outer. Both null means we cannot emit a valid event.
                messageId = activity.TryGetProperty("messageId", out var mid) ? mid.GetString() : outerMessageId;
                if (messageId is null) return null; // suppress; cannot produce a valid ACTIVITY_SNAPSHOT without a messageId

                risk = activity.TryGetProperty("risk", out var r) ? r.GetString() : null;
                category = activity.TryGetProperty("category", out var c) ? c.GetString() : null;
                reason = activity.TryGetProperty("reason", out var rs) ? rs.GetString() : null;
            }
            catch (JsonException)
            {
                return [];
            }

            string encodedMsgId = JsonSerializer.Serialize(messageId);
            string contentJson =
                $$"""{"risk":{{JsonSerializer.Serialize(risk ?? "Unknown")}},"category":{{JsonSerializer.Serialize(category ?? string.Empty)}},"reason":{{JsonSerializer.Serialize(reason ?? string.Empty)}}}""";
            string activitySnapshot =
                $$"""{"type":"ACTIVITY_SNAPSHOT","messageId":{{encodedMsgId}},"activityType":"{{ActivityType}}","replace":true,"content":{{contentJson}}}""";

            return [activitySnapshot];
        } // end using (doc)
    }
}
