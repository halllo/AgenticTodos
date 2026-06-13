using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EUAIActClassifier;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// Agent-level streaming middleware that surfaces the EU AI Act verdict produced by the
/// <c>UseEUAIActClassification(classifier)</c> agent (registered as the innermost agent, directly below
/// this one) as an activity snapshot in the AGUI stream — but only when the turn is classified
/// <see cref="Risk.High"/> or above.
/// <para>
/// The classification agent classifies the completed turn once per run and emits its
/// <see cref="Classification"/> on a trailing side-channel <see cref="AgentResponseUpdate"/>; we read it
/// here via the supported, key-agnostic <c>update.EUAIActClassification</c> getter (EUAIActClassifier
/// ≥ 0.0.2). When the risk warrants it, we emit a <see cref="DataContent"/> marker with the dedicated MIME
/// <see cref="ActivityMediaType"/> so the AGUI framework routes it through a
/// <c>TEXT_MESSAGE_CONTENT</c> event; <see cref="EUAIActRiskActivityInjector"/> then rewrites that
/// into an <c>ACTIVITY_SNAPSHOT</c> (<c>activityType: "eu-ai-act-risk"</c>) before the client sees it.
/// Mirrors <see cref="DetectMcpAppsActivityMiddleware"/>.
/// </para>
/// </summary>
internal static class EUAIActRiskActivityMiddleware
{
    /// <summary>
    /// MIME type of the emitted marker. Any media type other than <c>application/json</c>
    /// (→ <c>STATE_SNAPSHOT</c>) and <c>application/json-patch+json</c> (→ <c>STATE_DELTA</c>) makes the
    /// AGUI framework route the <see cref="DataContent"/> through a <c>TEXT_MESSAGE_CONTENT</c> event;
    /// a dedicated type (rather than reusing the MCP-apps one) keeps the two activity kinds distinct.
    /// </summary>
    private const string ActivityMediaType = "application/x-eu-ai-act-activity";

    extension(AIAgentBuilder agentBuilder)
    {
        public AIAgentBuilder UseEUAIActRiskActivity() => agentBuilder.Use(runFunc: RunAsync, runStreamingFunc: RunStreamingAsync);
    }

    private static Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
        => RunStreamingAsync(messages, session, options, innerAgent, cancellationToken)
            .ToAgentResponseAsync();

    private static async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var emitted = false;

        await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return update;

            if (emitted) continue;

            // Supported, key-agnostic getter: the verdict rides on a trailing side-channel update, so
            // intermediate content updates return null and we simply skip them.
            var classification = update.EUAIActClassification;
            if (classification is null) continue;

            // Classification is best-effort — a classifier failure surfaces as Risk.Unknown rather than
            // throwing. Risk.Unknown is the lowest tier, so the High-or-above guard below intentionally
            // skips it: we never assert a risk level we couldn't actually determine.
            if (classification.Risk < Risk.High) continue;

            emitted = true;

            var activityJson = BuildActivityJson(
                messageId: Guid.NewGuid().ToString("N"),
                classification: classification);

            // Routes through TEXT_MESSAGE_CONTENT (not STATE_SNAPSHOT); EUAIActRiskActivityInjector
            // replaces it with ACTIVITY_SNAPSHOT before the client sees it.
            yield return new AgentResponseUpdate
            {
                Contents = [new DataContent(Encoding.UTF8.GetBytes(activityJson), ActivityMediaType)]
            };
        }
    }

    private static string BuildActivityJson(string messageId, Classification classification)
    {
        string encodedMsgId = JsonSerializer.Serialize(messageId);
        string encodedRisk = JsonSerializer.Serialize(classification.Risk.ToString());
        string encodedCategory = JsonSerializer.Serialize(classification.Category ?? string.Empty);
        string encodedReason = JsonSerializer.Serialize(classification.Reason ?? string.Empty);
        return $$"""{"type":"eu-ai-act-activity","messageId":{{encodedMsgId}},"risk":{{encodedRisk}},"category":{{encodedCategory}},"reason":{{encodedReason}}}""";
    }
}
