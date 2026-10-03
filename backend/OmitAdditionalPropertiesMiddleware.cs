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
        => base.GetResponseAsync(messages, WithoutOmittedProperties(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages, WithoutOmittedProperties(options), cancellationToken);

    /// <summary>
    /// <paramref name="options"/> itself when there is nothing to strip; otherwise a copy without the matching
    /// entries.
    /// </summary>
    /// <remarks>
    /// A copy, not an in-place removal: <c>FunctionInvokingChatClient</c> hands the same <c>ChatOptions</c> to
    /// every round-trip of a run, and the clients between it and this one read <c>RunAgentInput</c> off them on
    /// each — <see cref="ClientToolDeferralMiddleware"/> keys its state by the AG-UI thread id. Removed in place,
    /// the request would be gone from the second round-trip on.
    /// </remarks>
    private ChatOptions? WithoutOmittedProperties(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { } additionalProperties ||
            propertyValueTypesToOmit is not { Length: > 0 })
        {
            return options;
        }

        // Matching on the value's type means a null survives — which is why StateSnapshotMiddleware
        // writes its key only when there IS state: a `my_state: null` left here would travel on into the
        // model request. No explicit null check is needed for that; Type.IsInstanceOfType(null) is false.
        var kept = additionalProperties
            .Where(p => !propertyValueTypesToOmit.Any(t => t.IsInstanceOfType(p.Value)))
            .ToList();
        if (kept.Count == additionalProperties.Count)
        {
            return options;
        }

        var stripped = options.Clone();
        stripped.AdditionalProperties = new AdditionalPropertiesDictionary(kept);
        return stripped;
    }
}
