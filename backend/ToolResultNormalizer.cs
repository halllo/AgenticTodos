using System.Text;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AgenticTodos.Tests")]

namespace AgenticTodos.Backend;

/// <summary>
/// Normalises a raw tool result string to the MCP <c>CallToolResult</c> shape:
/// <c>{"content":[{"type":"text","text":"..."}]}</c>.
/// </summary>
internal static class ToolResultNormalizer
{
    public static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return """{"content":[]}""";

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // Already a CallToolResult with a content array.
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("content", out var contentArray) &&
                contentArray.ValueKind == JsonValueKind.Array)
            {
                bool needsType = false;
                foreach (var item in contentArray.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out _))
                    {
                        needsType = true;
                        break;
                    }
                }

                if (!needsType) return raw;

                // Rebuild, injecting "type":"text" for items that lack it.
                var sb = new StringBuilder("""{"content":[""");
                bool first = true;
                foreach (var item in contentArray.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    first = false;

                    if (item.TryGetProperty("type", out _))
                    {
                        sb.Append(item.GetRawText());
                    }
                    else
                    {
                        var text = item.TryGetProperty("text", out var t) ? t.GetString() ?? "" : item.GetRawText();
                        sb.Append($$"""{"type":"text","text":{{JsonSerializer.Serialize(text)}}}""");
                    }
                }
                sb.Append("]}");
                return sb.ToString();
            }

            // Microsoft.Extensions.AI TextContent: {"text":"...", "annotations":null, ...}
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("text", out var textProp))
            {
                var text = textProp.GetString() ?? "";
                return $$"""{"content":[{"type":"text","text":{{JsonSerializer.Serialize(text)}}}]}""";
            }

            // JSON string — SerializeResultContent encodes string/TextContent results this way.
            if (root.ValueKind == JsonValueKind.String)
                return $$"""{"content":[{"type":"text","text":{{raw}}}]}""";
        }
        catch
        {
            // Fall through to safe fallback.
        }

        return $$"""{"content":[{"type":"text","text":{{JsonSerializer.Serialize(raw)}}}]}""";
    }
}
