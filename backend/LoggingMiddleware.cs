using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

public class LoggingMiddleware(IChatClient inner, ILogger<LoggingMiddleware> logger) : IChatClient
{
    public void Dispose()
    {
        inner.Dispose();
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await inner.GetResponseAsync(messages, options, cancellationToken);
        logger.LogDebug("Response update: {@Messages}", messages);
        return response;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return inner.GetService(serviceType, serviceKey);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            logger.LogDebug("Streaming response update: {@Update}", new { Contents = JsonSerializer.Serialize(update.Contents), update.FinishReason });
            yield return update;
        }
    }
}