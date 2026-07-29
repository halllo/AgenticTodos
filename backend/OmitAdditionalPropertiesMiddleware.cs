using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// Strips entries from <see cref="ChatOptions.AdditionalProperties"/> before they reach the inner
/// client. Selection is by value type, because the keys involved are implementation details of the
/// frameworks that put them there — the AG-UI server SDK stashes the whole <c>RunAgentInput</c> under
/// an internal key, and <c>ChatClientAgent</c> copies this app's own run properties across.
/// </summary>
internal sealed class OmitAdditionalPropertiesMiddleware(
    IChatClient inner,
    Type[] propertyValueTypesToOmit) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        OmitAdditionalProperties(options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        OmitAdditionalProperties(options);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    /// <remarks>
    /// Mutating <paramref name="options"/> in place is contained: <c>ChatClientAgent</c> hands each run
    /// a clone (<c>CreateConfiguredChatOptions</c> → <c>ChatOptions.Clone()</c>, whose copy constructor
    /// clones <c>AdditionalProperties</c> too), so the <c>ChatOptions</c> the agent-level middlewares
    /// read <c>RunAgentInput</c> off is never touched.
    /// </remarks>
    private void OmitAdditionalProperties(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { } additionalProperties ||
            propertyValueTypesToOmit is not { Length: > 0 })
        {
            return;
        }

        // Matching on the value's type means a null survives — which is why StateSnapshotMiddleware
        // writes its key only when there IS state: a `my_state: null` left here would travel on into the
        // model request. No explicit null check is needed for that; Type.IsInstanceOfType(null) is false.
        var matching = additionalProperties
            .Where(p => propertyValueTypesToOmit.Any(t => t.IsInstanceOfType(p.Value)))
            .Select(p => p.Key)
            .ToList();
        foreach (var key in matching)
        {
            additionalProperties.Remove(key);
        }
    }
}
