using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// AG-UI state management can result in a system message with an empty text content. We filter those out.
/// Frontend tool calls dont give the middlewares a chance to clean up a state snapshot, which gets turned into a system message with an empty text.
/// </summary>
public static class OmitEmptySystemMessagesMiddleware
{
    public static Task Invoke(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, CancellationToken, Task> next,
        CancellationToken cancellationToken)
    {
        static bool IsEmptySystemMessage(ChatMessage message)
        {
            return message.Role == ChatRole.System
                && message.Contents.Count == 1
                && message.Contents[0] is TextContent textContent
                && string.IsNullOrWhiteSpace(textContent.Text);
        }
        var filteredMessages = messages.Where(m => !IsEmptySystemMessage(m));
        return next(filteredMessages, session, options, cancellationToken);
    }
}