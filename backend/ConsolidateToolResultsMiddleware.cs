using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// In case of parallel tool calls, AGUI frontend creates separate tool call messages for each tool invocation.
/// We need to consolidate them into a single tool result message, to not violate the Amazon Bedrock validation:
/// 'Expected toolResult blocks at messages.2.content for the following Ids: tooluse_ZMLJA3jfS0-SVst_Mtd-QA'
/// </summary>
public class ConsolidateToolResultsMiddleware(IChatClient inner) : IChatClient
{
    public void Dispose() => inner.Dispose();

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var cosolidated = ConsolidateToolResults(messages);
        var response = await inner.GetResponseAsync(cosolidated, options, cancellationToken);
        return response;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cosolidated = ConsolidateToolResults(messages);
        await foreach (var update in inner.GetStreamingResponseAsync(cosolidated, options, cancellationToken))
        {
            yield return update;
        }
    }

    private IEnumerable<ChatMessage> ConsolidateToolResults(IEnumerable<ChatMessage> messages)
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
                yield return new ChatMessage(ChatRole.Tool, bufferedToolContents)
                {
                    MessageId = bufferedToolMessageTemplate.MessageId,
                    AuthorName = bufferedToolMessageTemplate.AuthorName,
                    AdditionalProperties = bufferedToolMessageTemplate.AdditionalProperties,
                };

                bufferedToolContents = null;
                bufferedToolMessageTemplate = null;
            }

            yield return message;
        }

        if (bufferedToolContents is not null && bufferedToolMessageTemplate is not null)
        {
            yield return new ChatMessage(ChatRole.Tool, bufferedToolContents)
            {
                MessageId = bufferedToolMessageTemplate.MessageId,
                AuthorName = bufferedToolMessageTemplate.AuthorName,
                AdditionalProperties = bufferedToolMessageTemplate.AdditionalProperties,
            };
        }
    }
}