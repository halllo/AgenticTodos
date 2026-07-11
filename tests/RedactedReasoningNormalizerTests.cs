using System.Text.Json;
using AgenticTodos.Backend;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// Guards the redacted-thinking persistence fix: the AWS Bedrock adapter stores redacted reasoning as
/// a byte[] under AdditionalProperties["RedactedContent"], which a plain JSON round-trip (as done by
/// FileSystemChatHistoryProvider) turns into a JsonElement, breaking the adapter's outbound `obj is byte[]`
/// reconstruction. <see cref="RedactedReasoningNormalizer"/> restores it.
/// </summary>
public class RedactedReasoningNormalizerTests
{
    // Mirrors FileSystemChatHistoryProvider: default options, no AIJsonUtilities customization.
    private static List<ChatMessage> RoundTrip(List<ChatMessage> messages)
    {
        var json = JsonSerializer.Serialize(messages);
        return JsonSerializer.Deserialize<List<ChatMessage>>(json)!;
    }

    [Fact]
    public void NormalThinkingSignature_RoundTripsVerbatim()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextReasoningContent("Let me think about this...") { ProtectedData = "signature-abc123" },
            }),
        };

        var loaded = RoundTrip(messages);

        var reasoning = loaded[0].Contents.OfType<TextReasoningContent>().Single();
        Assert.Equal("Let me think about this...", reasoning.Text);
        Assert.Equal("signature-abc123", reasoning.ProtectedData);
    }

    [Fact]
    public void RedactedContentBytes_AreCorruptedByRoundTrip_ThenRestoredByNormalizer()
    {
        var bytes = new byte[] { 1, 2, 3, 42, 200, 255 };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextReasoningContent(string.Empty)
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["RedactedContent"] = bytes },
                },
            }),
        };

        var loaded = RoundTrip(messages);
        var reasoning = loaded[0].Contents.OfType<TextReasoningContent>().Single();

        // The bug: after a JSON round-trip the byte[] is no longer a byte[] (the adapter's `obj is byte[]` fails).
        var corrupted = reasoning.AdditionalProperties!["RedactedContent"];
        Assert.False(corrupted is byte[], "Expected the round-trip to lose the byte[] type (that is the bug being fixed).");

        // The fix restores the byte[] so the adapter can reconstruct the redacted_thinking block.
        RedactedReasoningNormalizer.Normalize(loaded);

        var restored = reasoning.AdditionalProperties!["RedactedContent"];
        var restoredBytes = Assert.IsType<byte[]>(restored);
        Assert.Equal(bytes, restoredBytes);
    }

    [Fact]
    public void Normalize_IsNoOp_WhenNoReasoningContent()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, "hi there"),
        };

        var exception = Record.Exception(() => RedactedReasoningNormalizer.Normalize(messages));

        Assert.Null(exception);
    }
}
