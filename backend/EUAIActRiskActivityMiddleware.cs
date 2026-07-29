using System.Runtime.CompilerServices;
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
/// ≥ 0.0.2). When the risk warrants it, we emit an <see cref="EUAIActRiskActivityContent"/>, which the
/// mapping registered in <see cref="AGUIEndpoint.CreateStreamOptions"/> turns into an
/// <c>ACTIVITY_SNAPSHOT</c> (<c>activityType: "eu-ai-act-risk"</c>).
/// Mirrors <see cref="DetectMcpAppsActivityMiddleware"/>.
/// </para>
/// </summary>
internal static class EUAIActRiskActivityMiddleware
{
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

        await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
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

            yield return new AgentResponseUpdate
            {
                Contents =
                [
                    new EUAIActRiskActivityContent(
                        messageId: Guid.NewGuid().ToString("N"),
                        risk: classification.Risk.ToString(),
                        category: classification.Category ?? string.Empty,
                        reason: classification.Reason ?? string.Empty)
                ]
            };
        }
    }
}
