using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// In case of parallel tool calls, AGUI frontend creates separate tool call messages for each tool invocation.
/// We need to consolidate them into a single tool result message, to not violate the Amazon Bedrock validation:
/// 'Expected toolResult blocks at messages.2.content for the following Ids: tooluse_ZMLJA3jfS0-SVst_Mtd-QA'
/// </summary>
/// <remarks>
/// Still required after the AG-UI SDK migration: the protocol carries one <c>toolCallId</c> per
/// <c>tool</c> message and <c>AsChatMessages</c> emits one <see cref="ChatMessage"/> per AG-UI tool
/// message, so parallel results still arrive split.
/// </remarks>
internal sealed class ConsolidateToolResultsMiddleware(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(ConsolidateToolResults(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(ConsolidateToolResults(messages), options, cancellationToken);

    private static IEnumerable<ChatMessage> ConsolidateToolResults(IEnumerable<ChatMessage> messages)
    {
        List<AIContent>? bufferedToolContents = null;
        ChatMessage? bufferedToolMessageTemplate = null;

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Tool)
            {
                bufferedToolContents ??= [];
                bufferedToolContents.AddRange(message.Contents);
                bufferedToolMessageTemplate ??= message;
                continue;
            }

            if (bufferedToolContents is not null && bufferedToolMessageTemplate is not null)
            {
                yield return Merged(bufferedToolContents, bufferedToolMessageTemplate);

                bufferedToolContents = null;
                bufferedToolMessageTemplate = null;
            }

            yield return message;
        }

        if (bufferedToolContents is not null && bufferedToolMessageTemplate is not null)
        {
            yield return Merged(bufferedToolContents, bufferedToolMessageTemplate);
        }

        static ChatMessage Merged(List<AIContent> contents, ChatMessage template) =>
            new(ChatRole.Tool, contents)
            {
                MessageId = template.MessageId,
                AuthorName = template.AuthorName,
                AdditionalProperties = template.AdditionalProperties,
            };
    }
}
