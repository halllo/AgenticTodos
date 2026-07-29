using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace AgenticTodos.Backend;

/// <summary>
/// Agent-level streaming middleware that emits an <see cref="McpAppActivityContent"/> immediately
/// after each <see cref="FunctionResultContent"/> whose tool has a registered <c>ui.resourceUri</c>.
/// The mapping registered in <see cref="AGUIEndpoint.CreateStreamOptions"/> turns it into an AG-UI
/// <c>ACTIVITY_SNAPSHOT</c> event.
/// </summary>
internal static class DetectMcpAppsActivityMiddleware
{
    extension(AIAgentBuilder agentBuilder)
    {
        public AIAgentBuilder UseDetectMcpAppsActivity() => agentBuilder.Use(runFunc: RunAsync, runStreamingFunc: RunStreamingAsync);
    }

    private static Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
        => RunStreamingAsync(messages, session, options, innerAgent, cancellationToken)
            .ToAgentResponseAsync();

    private static async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var callIdToInfo = new Dictionary<string, (string ToolName, string ArgsJson)>(StringComparer.Ordinal);

        await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
        {
            foreach (var fcc in update.Contents.OfType<FunctionCallContent>())
            {
                var argsJson = fcc.Arguments is not null
                    ? JsonSerializer.Serialize(fcc.Arguments)
                    : "{}";
                callIdToInfo[fcc.CallId] = (fcc.Name, argsJson);
            }

            yield return update;

            foreach (var frc in update.Contents.OfType<FunctionResultContent>())
            {
                if (!callIdToInfo.Remove(frc.CallId, out var info)) continue;
                var runContext = AIAgent.CurrentRunContext;
                var chatOptions = runContext?.Agent?.GetService<ChatClientAgentOptions>();
                // GetService-based lookup instead of OfType: an approval-gated MCP tool is wrapped
                // in ApprovalRequiredAIFunction (a DelegatingAIFunction), which forwards GetService
                // to the inner McpClientTool.
                var resourceUri = chatOptions?.ChatOptions?.Tools?
                    .Select(t => t.GetService<McpClientTool>())
                    .OfType<McpClientTool>()
                    .FirstOrDefault(t => string.Equals(t.Name, info.ToolName, StringComparison.OrdinalIgnoreCase))
                    ?.ProtocolTool.Meta?["ui"]?["resourceUri"]?.GetValue<string>();
                if (resourceUri is null) continue;

                var resultJson = SerializeResult(frc.Result);
                var normalizedResult = NormalizeToolResult(resultJson);

                yield return new AgentResponseUpdate
                {
                    Contents =
                    [
                        new McpAppActivityContent(
                            // The tool call id, not a fresh GUID: the activity identifies that call, so
                            // re-emitting it replaces the rendered app (which is what Replace = true
                            // promises) instead of stacking a second card for the same result.
                            messageId: frc.CallId,
                            resourceUri: resourceUri,
                            result: ParseOrEmptyObject(normalizedResult),
                            toolInput: ParseOrEmptyObject(info.ArgsJson))
                    ]
                };
            }
        }
    }

    private static JsonElement ParseOrEmptyObject(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return s_emptyObject;
        }
    }

    private static readonly JsonElement s_emptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static string SerializeResult(object? result) => result switch
    {
        null => string.Empty,
        string str => str,
        // No null guard on Text: its getter substitutes string.Empty for a null backing field.
        TextContent tc => JsonSerializer.Serialize(tc.Text),
        JsonElement el => el.GetRawText(),
        _ => JsonSerializer.Serialize(result),
    };

    /// <summary>
    /// Normalises a raw tool result string to the MCP <c>CallToolResult</c> shape:
    /// <c>{"content":[{"type":"text","text":"..."}]}</c>.
    /// </summary>
    internal static string NormalizeToolResult(string raw)
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

            // JSON string — SerializeResult encodes string/TextContent results this way.
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
