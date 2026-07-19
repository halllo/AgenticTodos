using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MEAI001, MAAI001 // Tool approval types are experimental

namespace AgenticTodos.Backend;

/// <summary>
/// Agent-level middleware that bridges Microsoft.Extensions.AI tool approval content across the
/// AG-UI protocol boundary. The AGUI hosting packages (1.13.0-preview) serialize only
/// Text/FunctionCall/FunctionResult/Reasoning/Data content, so a <see cref="ToolApprovalRequestContent"/>
/// emitted by <c>FunctionInvokingChatClient</c> for an <see cref="ApprovalRequiredAIFunction"/> would be
/// silently dropped. This middleware translates it into a synthetic client tool call named
/// <c>request_approval</c> (streamed as regular TOOL_CALL_* SSE events, which end the run like any
/// unanswered client tool call), and translates the client's tool result back into a
/// <see cref="ToolApprovalResponseContent"/> — or an <see cref="AlwaysApproveToolApprovalResponseContent"/>
/// when the client requests a standing "don't ask again" rule, which the inner
/// <see cref="ToolApprovalAgent"/> persists in the session.
///
/// The wire contract is stateless: the request payload carries the wrapped tool call, and the client
/// echoes it back in the response, so no server-side correlation memory is needed between the two runs.
/// </summary>
internal static class ToolApprovalBridgeMiddleware
{
    public const string ApprovalToolName = "request_approval";

    public const string AlwaysApproveToolScope = "tool";
    public const string AlwaysApproveToolWithArgumentsScope = "tool_with_arguments";

    /// <summary>The wrapped tool call a client must approve; echoed back verbatim in <see cref="ApprovalResponse"/>.</summary>
    public sealed class ApprovalToolCall
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public JsonElement? Arguments { get; set; }
    }

    /// <summary>Arguments of the synthetic <c>request_approval</c> tool call.</summary>
    public sealed class ApprovalRequest
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("tool_call")]
        public ApprovalToolCall? ToolCall { get; set; }
    }

    /// <summary>Tool result content the client returns for a <c>request_approval</c> call.</summary>
    public sealed class ApprovalResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("approved")]
        public bool? Approved { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        /// <summary><c>null</c> = this call only; <c>"tool"</c> = always allow this tool;
        /// <c>"tool_with_arguments"</c> = always allow this tool with exactly these arguments.</summary>
        [JsonPropertyName("always_approve")]
        public string? AlwaysApprove { get; set; }

        [JsonPropertyName("tool_call")]
        public ApprovalToolCall? ToolCall { get; set; }
    }

    extension(AIAgentBuilder agentBuilder)
    {
        public AIAgentBuilder UseToolApprovalBridge() => agentBuilder.Use(runFunc: RunAsync, runStreamingFunc: RunStreamingAsync);
    }

    internal static async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var transformedMessages = ConvertApprovalResultsToApprovalResponses(messages);
        var response = await innerAgent.RunAsync(transformedMessages, session, options, cancellationToken)
            .ConfigureAwait(false);

        if (response.Messages.Any(m => m.Contents.Any(c => c is ToolApprovalRequestContent)))
        {
            response.Messages =
            [
                .. response.Messages.Select(message =>
                {
                    if (!message.Contents.Any(c => c is ToolApprovalRequestContent))
                    {
                        return message;
                    }

                    var converted = message.Clone();
                    converted.Contents = [.. message.Contents.Select(ConvertApprovalRequestToToolCall)];
                    // Match the streaming path: a null MessageId serializes as a null
                    // TOOL_CALL_START.parentMessageId, which AG-UI clients reject.
                    converted.MessageId ??= Guid.NewGuid().ToString("N");
                    return converted;
                })
            ];
        }

        return response;
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var transformedMessages = ConvertApprovalResultsToApprovalResponses(messages);

        await foreach (var update in innerAgent.RunStreamingAsync(transformedMessages, session, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (update.Contents.Any(c => c is ToolApprovalRequestContent))
            {
                update.Contents = [.. update.Contents.Select(ConvertApprovalRequestToToolCall)];
                // ToolApprovalAgent re-emits queued approval requests in updates without a
                // MessageId. The AGUI layer serializes that as TOOL_CALL_START.parentMessageId =
                // null, which the @ag-ui/client Zod schema rejects (optional string, not nullable)
                // — failing the whole run client-side. Stamp an id so the field is a string.
                update.MessageId ??= Guid.NewGuid().ToString("N");
            }
            yield return update;
        }
    }

    /// <summary>
    /// Outbound: replaces a <see cref="ToolApprovalRequestContent"/> with a synthetic
    /// <c>request_approval</c> <see cref="FunctionCallContent"/> the AGUI layer can serialize.
    /// The synthetic call id is the approval request id, so the client's tool result correlates back.
    /// </summary>
    private static AIContent ConvertApprovalRequestToToolCall(AIContent content)
        => content is ToolApprovalRequestContent { ToolCall: FunctionCallContent fcc } request
            ? new FunctionCallContent(
                callId: request.RequestId,
                name: ApprovalToolName,
                arguments: new Dictionary<string, object?>
                {
                    ["id"] = request.RequestId,
                    ["tool_call"] = new Dictionary<string, object?>
                    {
                        ["id"] = fcc.CallId,
                        ["name"] = fcc.Name,
                        ["arguments"] = fcc.Arguments,
                    },
                })
            : content;

    /// <summary>
    /// Inbound: converts tool results for <c>request_approval</c> calls back into
    /// <see cref="ToolApprovalResponseContent"/> (wrapped in a user message, as the approval flow
    /// expects) and strips any re-sent <c>request_approval</c> tool calls. Everything else —
    /// notably results of regular client-side (WebMCP) tools — passes through untouched.
    /// </summary>
    internal static List<ChatMessage> ConvertApprovalResultsToApprovalResponses(IEnumerable<ChatMessage> messages)
    {
        // Materialize once: the AGUI layer hands us a lazy iterator.
        var result = new List<ChatMessage>();

        foreach (var message in messages)
        {
            List<AIContent>? approvalResponses = null;
            List<AIContent>? remaining = null;
            var changed = false;

            for (var i = 0; i < message.Contents.Count; i++)
            {
                var content = message.Contents[i];
                switch (content)
                {
                    case FunctionResultContent frc when TryParseApprovalResponse(frc) is { } response:
                        (approvalResponses ??= []).Add(response);
                        changed = true;
                        break;
                    // A client re-sending history may include the synthetic assistant tool call;
                    // it must not reach the model provider (orphaned tool call without result).
                    case FunctionCallContent { Name: ApprovalToolName }:
                        changed = true;
                        break;
                    default:
                        (remaining ??= []).Add(content);
                        break;
                }
            }

            if (!changed)
            {
                result.Add(message);
                continue;
            }

            if (remaining is { Count: > 0 })
            {
                var kept = message.Clone();
                kept.Contents = remaining;
                result.Add(kept);
            }

            if (approvalResponses is { Count: > 0 })
            {
                result.Add(new ChatMessage(ChatRole.User, approvalResponses));
            }
        }

        return result;
    }

    /// <summary>
    /// Parses a tool result as an <see cref="ApprovalResponse"/>. Returns <c>null</c> for anything
    /// that is not an approval response (shape mismatch, or call id not matching the payload id),
    /// so regular tool results are never misinterpreted.
    /// </summary>
    private static AIContent? TryParseApprovalResponse(FunctionResultContent frc)
    {
        var dto = frc.Result switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } el => Deserialize(el),
            JsonElement { ValueKind: JsonValueKind.String } el => Parse(el.GetString()),
            string s => Parse(s),
            _ => null,
        };

        if (dto?.Id is null || dto.Approved is null || dto.ToolCall?.Id is null || dto.ToolCall.Name is null)
        {
            return null;
        }

        if (!string.Equals(frc.CallId, dto.Id, StringComparison.Ordinal))
        {
            return null;
        }

        var arguments = dto.ToolCall.Arguments is { ValueKind: JsonValueKind.Object } argsEl
            ? argsEl.Deserialize<Dictionary<string, object?>>()
            : null;
        var request = new ToolApprovalRequestContent(
            dto.Id,
            new FunctionCallContent(dto.ToolCall.Id, dto.ToolCall.Name, arguments));

        return dto switch
        {
            { Approved: true, AlwaysApprove: AlwaysApproveToolScope }
                => request.CreateAlwaysApproveToolResponse(dto.Reason),
            { Approved: true, AlwaysApprove: AlwaysApproveToolWithArgumentsScope }
                => request.CreateAlwaysApproveToolWithArgumentsResponse(dto.Reason),
            _ => request.CreateResponse(dto.Approved.Value, dto.Reason),
        };

        static ApprovalResponse? Deserialize(JsonElement el)
        {
            try
            {
                return el.Deserialize<ApprovalResponse>();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        static ApprovalResponse? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonSerializer.Deserialize<ApprovalResponse>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
