using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// Marks a <see cref="ChatMessage"/> as per-turn context: it must reach the model for this run, but
/// must not enter the persisted transcript.
/// <para>
/// A middleware above <c>ChatClientAgent</c> that prepends a message puts it into
/// <c>ChatHistoryProvider.InvokedContext.RequestMessages</c>, so an append-only history store
/// persists it. For context that describes the <i>current</i> turn — the conversation state snapshot
/// is the case this exists for — that is wrong twice over: every later turn replays a stale copy
/// (the model can read an outdated value and contradict the live state), and the prompt grows by one
/// block per turn without bound.
/// </para>
/// <para>
/// <see cref="IOChatHistoryProvider.StoreChatHistoryAsync"/> drops marked messages. The mark travels
/// on <see cref="ChatMessage.AdditionalProperties"/> rather than in the text, so nothing has to parse
/// message content to recognise it. It does still reach the provider adapter with the request (which
/// ignores it, as it does the framework's own entries there) — it is a persistence hint, not a secret.
/// </para>
/// </summary>
internal static class TransientChatMessages
{
    private const string MarkerKey = "agenticTodos.transient";

    /// <summary>Marks <paramref name="message"/> as not-to-be-persisted and returns it.</summary>
    public static ChatMessage AsTransient(this ChatMessage message)
    {
        (message.AdditionalProperties ??= [])[MarkerKey] = true;
        return message;
    }

    /// <summary>Whether <paramref name="message"/> was marked by <see cref="AsTransient"/>.</summary>
    public static bool IsTransient(this ChatMessage message) =>
        message.AdditionalProperties is { } properties &&
        properties.TryGetValue(MarkerKey, out var marker) &&
        marker is true;
}
