using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

public class OmitAdditionalPropertiesMiddleware(IChatClient inner, string[]? propertyKeysToOmit = null) : IChatClient
{
    public void Dispose()
    {
        inner.Dispose();
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        OmitAdditionalProperties(options);
        var response = await inner.GetResponseAsync(messages, options, cancellationToken);
        return response;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return inner.GetService(serviceType, serviceKey);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        OmitAdditionalProperties(options);
        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
    }
    private void OmitAdditionalProperties(ChatOptions? options)
    {
        if (propertyKeysToOmit == null)
        {
            options?.AdditionalProperties?.Clear();
        }
        else if (propertyKeysToOmit != null && options?.AdditionalProperties != null)
        {
            foreach (var key in propertyKeysToOmit)
            {
                options.AdditionalProperties.Remove(key);
            }
        }
    }
}