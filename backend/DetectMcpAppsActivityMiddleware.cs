using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace AgenticTodos.Backend;

/// <summary>
/// Agent-level streaming middleware that emits a <see cref="DataContent"/> marker immediately
/// after each <see cref="FunctionResultContent"/> whose tool has a registered <c>ui.resourceUri</c>.
/// Uses MIME type <c>application/x-mcp-activity</c> so the AGUI framework converts it to a
/// <c>TEXT_MESSAGE_CONTENT</c> SSE event (not <c>STATE_SNAPSHOT</c>), which
/// <see cref="SseEventInjectionMiddleware"/> then detects and replaces with <c>ACTIVITY_SNAPSHOT</c>.
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

        await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, cancellationToken)
                           .ConfigureAwait(false))
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
                var resourceUri = chatOptions?.ChatOptions?.Tools?.OfType<McpClientTool>()
                    .FirstOrDefault(t => string.Equals(t.Name, info.ToolName, StringComparison.OrdinalIgnoreCase))
                    ?.ProtocolTool.Meta?["ui"]?["resourceUri"]?.GetValue<string>();
                if (resourceUri is null) continue;

                var resultJson = SerializeResult(frc.Result);
                var normalizedResult = NormalizeToolResult(resultJson);

                var activityJson = BuildActivityJson(
                    messageId: Guid.NewGuid().ToString("N"),
                    resourceUri: resourceUri,
                    normalizedResult: normalizedResult,
                    toolInputJson: info.ArgsJson);

                // Use application/x-mcp-activity so the AGUI framework routes this through
                // TextMessageContentEvent (not StateSnapshotEvent), keeping it out of the
                // state-snapshot pathway. SseEventInjectionMiddleware replaces it with
                // ACTIVITY_SNAPSHOT before the client sees it.
                yield return new AgentResponseUpdate
                {
                    Contents = [new DataContent(Encoding.UTF8.GetBytes(activityJson), "application/x-mcp-activity")]
                };
            }
        }
    }

    private static string BuildActivityJson(
        string messageId, string resourceUri, string normalizedResult, string toolInputJson)
    {
        string encodedMsgId = JsonSerializer.Serialize(messageId);
        string encodedUri = JsonSerializer.Serialize(resourceUri);
        return $$"""{"type":"mcp-activity","messageId":{{encodedMsgId}},"resourceUri":{{encodedUri}},"result":{{normalizedResult}},"toolInput":{{toolInputJson}}}""";
    }

    private static string SerializeResult(object? result) => result switch
    {
        null => string.Empty,
        string str => str,
        TextContent tc => JsonSerializer.Serialize(tc.Text ?? string.Empty),
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
