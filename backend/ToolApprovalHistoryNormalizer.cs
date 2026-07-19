using Microsoft.Extensions.AI;

#pragma warning disable MEAI001 // Tool approval types are experimental

namespace AgenticTodos.Backend;

/// <summary>
/// Repairs persisted tool-approval content on history load so <c>FunctionInvokingChatClient</c>
/// (FICC) can re-process the conversation.
/// <para>
/// <b>Why this exists: the history store is append-only.</b> On the resume turn FICC repairs the
/// conversation <i>in place</i>: it executes the approved call, appends the recreated
/// <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/> pair, and flips
/// <see cref="FunctionCallContent.InformationalOnly"/> to <c>true</c> on the in-memory approval
/// request/response contents, marking them inert for later replays. A store that re-serializes the
/// whole live conversation after every run captures that repair and never sees the problem.
/// <c>FileSystemChatHistoryProvider</c>, however, appends only the turn's new messages to the file:
/// the <see cref="ToolApprovalRequestContent"/> was persisted in the <b>previous</b> turn with
/// <c>InformationalOnly = false</c> and is never re-written — nor could it be, since
/// <c>ChatHistoryProvider.InvokedContext</c> hands the store only the new messages, not the
/// (mutated) history it supplied. On the next load FICC therefore sees an "active" request whose
/// response it skips as informational, and throws
/// <c>"ToolApprovalRequestContent found ... no matching ToolApprovalResponseContent"</c>.
/// </para>
/// <para>
/// Two repairs, both idempotent:
/// <list type="number">
/// <item><b>Scrub completed pairs</b> — approval requests/responses whose tool call already has a
/// <see cref="FunctionResultContent"/> in history are removed; the recreated call/result pair is the
/// canonical transcript the model providers need (an approval content would be dropped by their
/// mappers anyway, leaving empty messages that e.g. Bedrock rejects).</item>
/// <item><b>Reject orphans</b> — a request with no response in history <i>and</i> none arriving in
/// the current turn's request messages can never be answered (the client lost it, e.g. session file
/// deleted or thread abandoned); every later turn would throw. A synthetic rejected response is
/// appended so FICC generates a failed result and the conversation continues.</item>
/// </list>
/// </para>
/// </summary>
public static class ToolApprovalHistoryNormalizer
{
    public static void Normalize(List<ChatMessage>? history, IEnumerable<ChatMessage>? requestMessages)
    {
        if (history is null || history.Count == 0)
        {
            return;
        }

        var completedCallIds = history
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Select(r => r.CallId)
            .ToHashSet(StringComparer.Ordinal);

        // 1. Scrub approval requests/responses whose tool call already completed.
        if (completedCallIds.Count > 0)
        {
            for (var i = history.Count - 1; i >= 0; i--)
            {
                var message = history[i];
                if (!message.Contents.Any(IsCompletedApprovalContent))
                {
                    continue;
                }

                var kept = message.Contents.Where(c => !IsCompletedApprovalContent(c)).ToList();
                if (kept.Count == 0)
                {
                    history.RemoveAt(i);
                }
                else
                {
                    var scrubbed = message.Clone();
                    scrubbed.Contents = kept;
                    history[i] = scrubbed;
                }
            }
        }

        // 2. Reject orphaned requests that nothing (neither history nor the current turn) answers.
        var answeredRequestIds = history
            .Concat(requestMessages ?? [])
            .SelectMany(m => m.Contents)
            .OfType<ToolApprovalResponseContent>()
            .Select(r => r.RequestId)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = history
            .SelectMany(m => m.Contents)
            .OfType<ToolApprovalRequestContent>()
            .Where(r => !answeredRequestIds.Contains(r.RequestId))
            .ToList();

        if (orphans.Count > 0)
        {
            history.Add(new ChatMessage(
                ChatRole.User,
                [.. orphans.Select(r => (AIContent)r.CreateResponse(false, "The user did not respond to the approval request."))]));
        }

        return;

        bool IsCompletedApprovalContent(AIContent content) => content switch
        {
            ToolApprovalRequestContent { ToolCall: { } toolCall } => completedCallIds.Contains(toolCall.CallId),
            ToolApprovalResponseContent { ToolCall: { } toolCall } => completedCallIds.Contains(toolCall.CallId),
            _ => false,
        };
    }
}
