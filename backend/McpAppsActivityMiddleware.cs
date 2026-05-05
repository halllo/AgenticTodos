using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace AgenticTodos.Backend;

/// <summary>
/// Singleton service that maps MCP tool names to their <c>ui.resourceUri</c> metadata value.
/// Only tools decorated with <c>[McpMeta("ui", JsonValue = {"resourceUri":"..."})]</c> are registered.
/// </summary>
internal sealed class McpToolRegistry
{
    private readonly IReadOnlyDictionary<string, string> _toolResourceUris;

    public McpToolRegistry(IEnumerable<AIFunction> tools)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools.OfType<McpClientTool>())
        {
            var resourceUri = tool.ProtocolTool.Meta?["ui"]?["resourceUri"]?.GetValue<string>();
            if (resourceUri is not null)
                dict[tool.Name] = resourceUri;
        }
        _toolResourceUris = dict;
    }

    public string? GetResourceUri(string toolName) =>
        _toolResourceUris.TryGetValue(toolName, out var uri) ? uri : null;
}

/// <summary>
/// Agent-level streaming middleware that emits a <see cref="DataContent"/> marker immediately
/// after each <see cref="FunctionResultContent"/> whose tool has a registered <c>ui.resourceUri</c>.
/// Uses MIME type <c>application/x-mcp-activity</c> so the AGUI framework converts it to a
/// <c>TEXT_MESSAGE_CONTENT</c> SSE event (not <c>STATE_SNAPSHOT</c>), which
/// <see cref="SseEventInjectionMiddleware"/> then detects and replaces with <c>ACTIVITY_SNAPSHOT</c>.
/// </summary>
internal static class McpAppsActivityMiddleware
{
    public static Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        McpToolRegistry registry,
        CancellationToken cancellationToken)
        => RunStreamingAsync(messages, session, options, innerAgent, registry, cancellationToken)
            .ToAgentResponseAsync();

    public static async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        McpToolRegistry registry,
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
                var resourceUri = registry.GetResourceUri(info.ToolName);
                if (resourceUri is null) continue;

                var resultJson = SerializeResult(frc.Result);
                var normalizedResult = ToolResultNormalizer.Normalize(resultJson);

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
}
