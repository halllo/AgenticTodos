using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// The AWS Bedrock MEAI adapter stores a <c>redacted_thinking</c> payload as a <see cref="byte"/>[]
/// under <c>AIContent.AdditionalProperties["RedactedContent"]</c>, and on the outbound path only
/// reconstructs the block when that slot is still a <see cref="byte"/>[] (<c>obj is byte[]</c>).
/// <para>
/// When chat history is persisted as JSON and reloaded, that <see cref="object"/>-typed slot
/// round-trips as a <see cref="JsonElement"/> (a base64 string), so the adapter's <c>byte[]</c>
/// check fails, <c>RedactedContent</c> becomes <see langword="null"/>, and Bedrock then rejects the
/// malformed thinking block on every subsequent turn of the conversation. This normalizes the slot
/// back to <see cref="byte"/>[] after load so redacted thinking survives persistence.
/// </para>
/// Normal (non-redacted) thinking is unaffected: its signature lives in the plain
/// <see cref="TextReasoningContent.ProtectedData"/> string, which round-trips verbatim.
/// </summary>
internal static class RedactedReasoningNormalizer
{
    private const string RedactedContentKey = "RedactedContent";

    public static void Normalize(IEnumerable<ChatMessage>? messages)
    {
        if (messages is null)
        {
            return;
        }

        foreach (var message in messages)
        {
            foreach (var reasoning in message.Contents.OfType<TextReasoningContent>())
            {
                if (reasoning.AdditionalProperties is not { } props)
                {
                    continue;
                }

                if (props.TryGetValue(RedactedContentKey, out var value)
                    && value is JsonElement { ValueKind: JsonValueKind.String } element)
                {
                    try
                    {
                        props[RedactedContentKey] = element.GetBytesFromBase64();
                    }
                    catch (FormatException)
                    {
                        // Not base64 after all — leave it untouched rather than corrupt it further.
                    }
                }
            }
        }
    }
}
