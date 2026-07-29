using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// Agent-level middleware that surfaces Microsoft.Extensions.AI tool approvals as AG-UI
/// <b>interrupts</b> — the protocol's first-class human-in-the-loop mechanism (see
/// human-in-the-loop.md).
/// <para>
/// Outbound: a <see cref="ToolApprovalRequestContent"/> emitted by <c>FunctionInvokingChatClient</c>
/// for an <see cref="ApprovalRequiredAIFunction"/> becomes an <see cref="InterruptRequestContent"/>,
/// which the AG-UI server SDK turns into <c>RUN_FINISHED</c> with
/// <c>outcome = { type: "interrupt", interrupts: [...] }</c>. The interrupt carries the pending tool
/// call in its metadata so the client can render it and echo it back.
/// </para>
/// <para>
/// Inbound: the SDK decodes each <c>resume</c> entry whose payload contains a <c>toolCall</c> into a
/// <see cref="ToolApprovalRequestContent"/>/<see cref="ToolApprovalResponseContent"/> pair on its
/// own, so a plain approve/reject needs no work here. This middleware only upgrades the response
/// when the payload also asks for a standing rule (<c>alwaysApprove</c>), which the inner
/// <see cref="ToolApprovalAgent"/> then persists in the session.
/// </para>
/// <para>
/// Why convert at all instead of letting the SDK map approvals directly: its condition is
/// <c>!clientToolNames.Contains(name) &amp;&amp; (clientToolNames.Count == 0 || isContinuation)</c> — so it
/// emits an interrupt only for a tool the client did not declare, and then only when the run declares
/// no client-side tools at all or is already a continuation turn. This app always declares WebMCP
/// tools, so on a first turn a gated server-side tool would never reach the user. The first clause is
/// the one <see cref="ShouldConvert"/> mirrors: a client tool's approval request must keep travelling
/// as an ordinary tool call.
/// </para>
/// <para>
/// The reason is <see cref="InterruptReasons.Confirmation"/> rather than
/// <see cref="InterruptReasons.ToolCall"/> on purpose: a <c>tool_call</c> interrupt tells a client to
/// correlate the interrupt with a tool call it has already seen streamed, and a call awaiting
/// approval has deliberately not been streamed (the AG-UI .NET client silently drops such an
/// interrupt when it cannot find the matching call). The pending call travels in the metadata
/// instead, which both this repo's clients read.
/// </para>
/// </summary>
internal static class ToolApprovalInterruptMiddleware
{
    /// <summary>Payload property carrying a standing-rule request; see <see cref="AlwaysApproveScope"/>.</summary>
    internal const string AlwaysApprovePropertyName = "alwaysApprove";

    /// <summary>Scopes accepted in <see cref="AlwaysApprovePropertyName"/>.</summary>
    internal static class AlwaysApproveScope
    {
        /// <summary>Always allow this tool, whatever the arguments.</summary>
        public const string Tool = "tool";

        /// <summary>Always allow this tool with exactly these arguments.</summary>
        public const string ToolWithArguments = "tool_with_arguments";
    }

    extension(AIAgentBuilder agentBuilder)
    {
        public AIAgentBuilder UseToolApprovalInterrupts() => agentBuilder.Use(runFunc: RunAsync, runStreamingFunc: RunStreamingAsync);
    }

    internal static async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var clientToolNames = ReadClientToolNames(options);

        var response = await innerAgent
            .RunAsync(ApplyAlwaysApproveRules(messages, options), session, options, cancellationToken);

        if (response.Messages.Any(m => m.Contents.Any(c => ShouldConvert(c, clientToolNames))))
        {
            response.Messages =
            [
                .. response.Messages.Select(message =>
                {
                    if (!message.Contents.Any(c => ShouldConvert(c, clientToolNames)))
                    {
                        return message;
                    }

                    var converted = message.Clone();
                    converted.Contents = [.. message.Contents.Select(c => ConvertApprovalRequestToInterrupt(c, clientToolNames))];
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
        var clientToolNames = ReadClientToolNames(options);

        await foreach (var update in innerAgent
                           .RunStreamingAsync(ApplyAlwaysApproveRules(messages, options), session, options, cancellationToken))
        {
            if (update.Contents.Any(c => ShouldConvert(c, clientToolNames)))
            {
                update.Contents = [.. update.Contents.Select(c => ConvertApprovalRequestToInterrupt(c, clientToolNames))];

                // AgentResponseUpdate.AsChatResponseUpdate() — which the AG-UI endpoint calls on the
                // way to the event stream — returns RawRepresentation verbatim when it holds a
                // ChatResponseUpdate, discarding these Contents. Today no approval request arrives on
                // such an update (ToolApprovalAgent re-emits them on a bare update), but relying on
                // that would make the conversion silently vanish if it ever changed.
                update.RawRepresentation = null;
            }
            yield return update;
        }
    }

    /// <summary>
    /// Names of the tools the client declared for this run. Their approval requests must keep
    /// travelling as ordinary tool calls: the SDK maps those to <c>TOOL_CALL_*</c> events so the
    /// client executes them, and a continuation turn wraps every client tool in an
    /// <c>ApprovalRequiredAIFunction</c> (a proxy returning the previous result), so converting one
    /// would show an approval card for a WebMCP tool and, once approved, replay a stale result.
    /// </summary>
    private static HashSet<string> ReadClientToolNames(AgentRunOptions? options)
    {
        if (options is not ChatClientAgentRunOptions { ChatOptions: { } chatOptions } ||
            !chatOptions.TryGetRunAgentInput(out var input) ||
            input.Tools is not { Count: > 0 } tools)
        {
            return [];
        }

        return [.. tools.Select(tool => tool.Name).OfType<string>()];
    }

    private static bool ShouldConvert(AIContent content, HashSet<string> clientToolNames) =>
        content is ToolApprovalRequestContent { ToolCall: FunctionCallContent fcc } &&
        !clientToolNames.Contains(fcc.Name);

    /// <summary>
    /// Outbound: replaces a <see cref="ToolApprovalRequestContent"/> with the AG-UI interrupt the
    /// protocol defines for it. The request id becomes the interrupt id, which the client returns as
    /// <c>resume[].interruptId</c> — that is what lets the SDK rebuild the approval pair server-side.
    /// </summary>
    private static AIContent ConvertApprovalRequestToInterrupt(AIContent content, HashSet<string> clientToolNames)
        => ShouldConvert(content, clientToolNames) &&
           content is ToolApprovalRequestContent { ToolCall: FunctionCallContent fcc } request
            ? new InterruptRequestContent(request.RequestId)
            {
                Reason = InterruptReasons.Confirmation,
                ToolCallId = fcc.CallId,
                Message = $"Approval required for tool call: {fcc.Name}",
                Metadata = JsonSerializer.SerializeToElement(new
                {
                    // Mirrors AGUIToolApprovalPayload/AGUIToolApprovalResumePayload: the client renders
                    // this and echoes it back in the resume payload, so no server-side correlation
                    // memory is needed between the two runs.
                    toolCall = new
                    {
                        callId = fcc.CallId,
                        name = fcc.Name,
                        arguments = fcc.Arguments,
                    },
                }),
                ResponseSchema = ApprovalResponseSchema,
            }
            : content;

    /// <summary>
    /// Inbound: turns a plain approval into an "always allow" one when the client's resume payload
    /// asked for a standing rule. The SDK has already decoded the payload's <c>toolCall</c> into an
    /// approval request/response pair; only the response kind changes here.
    /// </summary>
    internal static IEnumerable<ChatMessage> ApplyAlwaysApproveRules(
        IEnumerable<ChatMessage> messages,
        AgentRunOptions? options)
    {
        var rules = ReadAlwaysApproveRules(options);
        if (rules.Count == 0)
        {
            return messages;
        }

        return
        [
            .. messages.Select(message =>
            {
                if (!message.Contents.Any(c => c is ToolApprovalResponseContent r && rules.ContainsKey(r.RequestId)))
                {
                    return message;
                }

                var upgraded = message.Clone();
                upgraded.Contents = [.. message.Contents.Select(content => Upgrade(content, rules))];
                return upgraded;
            })
        ];

        static AIContent Upgrade(AIContent content, Dictionary<string, string> rules)
        {
            if (content is not ToolApprovalResponseContent { Approved: true, ToolCall: { } toolCall } response ||
                !rules.TryGetValue(response.RequestId, out var scope))
            {
                return content;
            }

            var request = new ToolApprovalRequestContent(response.RequestId, toolCall);
            return scope switch
            {
                AlwaysApproveScope.Tool => request.CreateAlwaysApproveToolResponse(),
                AlwaysApproveScope.ToolWithArguments => request.CreateAlwaysApproveToolWithArgumentsResponse(),
                // An unrecognized scope degrades to the plain approval the client already sent, which
                // is the safe direction: a typo grants one call rather than a standing rule.
                _ => content,
            };
        }
    }

    /// <summary>
    /// Reads <c>resume[].payload.alwaysApprove</c> off the originating AG-UI request, keyed by
    /// interrupt id. Returns an empty map when the run carries no AG-UI input or no standing rules.
    /// </summary>
    private static Dictionary<string, string> ReadAlwaysApproveRules(AgentRunOptions? options)
    {
        if (options is not ChatClientAgentRunOptions { ChatOptions: { } chatOptions } ||
            !chatOptions.TryGetRunAgentInput(out var input) ||
            input.Resume is not { Count: > 0 } resume)
        {
            return [];
        }

        Dictionary<string, string> rules = [];
        foreach (var entry in resume)
        {
            if (entry.InterruptId is not { Length: > 0 } interruptId ||
                entry.Payload is not { ValueKind: JsonValueKind.Object } payload ||
                !payload.TryGetProperty(AlwaysApprovePropertyName, out var scope) ||
                scope.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            rules[interruptId] = scope.GetString()!;
        }

        return rules;
    }

    /// <summary>
    /// JSON Schema advertised on the interrupt for the expected resume payload. Purely informational
    /// for the client; the server accepts the payload shape the SDK decodes.
    /// <para>
    /// It deliberately does not offer a <c>reason</c>: the SDK decodes the payload into
    /// <c>AGUIToolApprovalResumePayload</c>, which models only <c>approved</c>, <c>toolCall</c> and
    /// <c>result</c>, so a reason could not reach the approval content no matter what a client sent.
    /// </para>
    /// </summary>
    private static readonly JsonElement ApprovalResponseSchema = JsonSerializer.Deserialize<JsonElement>(
        """
        {
          "type": "object",
          "properties": {
            "approved": { "type": "boolean" },
            "alwaysApprove": { "enum": ["tool", "tool_with_arguments", null] },
            "toolCall": {
              "type": "object",
              "properties": {
                "callId": { "type": "string" },
                "name": { "type": "string" },
                "arguments": { "type": "object" }
              },
              "required": ["callId", "name"]
            }
          },
          "required": ["approved", "toolCall"]
        }
        """);
}
