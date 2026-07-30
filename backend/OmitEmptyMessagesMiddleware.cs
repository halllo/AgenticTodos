using AGUI.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// Drops incoming messages that carry no content a model can be shown. Bedrock rejects an empty
/// content block outright, and one such message poisons the whole turn.
/// <para>
/// The AG-UI→MEAI conversion is where they come from: <c>AsChatMessages</c> falls through to an empty
/// assistant message for an <c>AGUIAssistantMessage</c> with neither content nor tool calls, and to an
/// empty user message for an <c>AGUIUserMessage</c> whose content list is empty. Only messages arriving
/// from the client are affected — the state snapshot this app injects is prepended <i>below</i> this
/// middleware (see <see cref="StateSnapshotMiddleware"/>) and always carries text.
/// </para>
/// <para>
/// <b><see cref="InterruptResponseContent"/> counts as empty too.</b>
/// <c>RunAgentInputExtensions.ToChatRequestContext</c> emits one per <c>resume</c> entry whose payload
/// is not a decodable tool approval — i.e. carries no <c>toolCall</c> — and collects them into a single
/// <c>ChatRole.User</c> message. Declining an interrupt is exactly that shape
/// (<c>{status:"cancelled"}</c>, no payload; see human-in-the-loop.md), and the provider mappers drop
/// content they do not model, so what reaches Bedrock is a message with no content block left in it.
/// </para>
/// <para>
/// Dropping it loses nothing: no reader for the type exists below this point. The server SDK only ever
/// <i>writes</i> it (AGUI.Client is where the reading half lives, and that is the client's side of the
/// wire), and <c>FunctionInvokingChatClient</c> cannot know an AG-UI type at all — the dependency runs
/// the other way. <c>AGUIResume.Status</c> has no reader either, so the decision itself never becomes a
/// <see cref="ToolApprovalResponseContent"/>; what actually resolves the pending approval is
/// <see cref="ToolApprovalHistoryNormalizer"/>'s fourth repair, which auto-rejects the request left
/// unanswered in history. The message is pure residue of the wire format.
/// </para>
/// </summary>
internal static class OmitEmptyMessagesMiddleware
{
    extension(AIAgentBuilder agentBuilder)
    {
        public AIAgentBuilder UseOmitEmptyMessages() => agentBuilder.Use(sharedFunc: Invoke);
    }

    internal static Task Invoke(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, CancellationToken, Task> next,
        CancellationToken cancellationToken)
    {
        // Empty means "nothing a model can be shown": blank text, or content this app is certain is
        // inert (below). A message whose contents include a tool call, a tool result, an image,
        // reasoning or an approval is meaningful even with no text, so it survives.
        static bool IsEmpty(ChatMessage message) =>
            message.Contents.Count == 0 ||
            message.Contents.All(content => content switch
            {
                TextContent { Text: var text } => string.IsNullOrWhiteSpace(text),
                InterruptResponseContent => true,
                _ => false,
            });

        return next(messages.Where(m => !IsEmpty(m)), session, options, cancellationToken);
    }
}
