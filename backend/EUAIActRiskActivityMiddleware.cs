using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EUAIActClassifier;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// Agent-level streaming middleware that surfaces the EU AI Act verdict attached by
/// <c>UseEUAIActClassification()</c> (at the <see cref="IChatClient"/> level) as an activity snapshot
/// in the AGUI stream — but only when the turn is classified <see cref="Risk.High"/> or above.
/// <para>
/// The classifier attaches its <see cref="Classification"/> to a final synthetic response update's
/// <c>AdditionalProperties</c>; that dictionary is preserved when the agent wraps each
/// <see cref="ChatResponseUpdate"/> into an <see cref="AgentResponseUpdate"/>, so we can read it here.
/// When the risk warrants it, we emit a <see cref="DataContent"/> marker with the dedicated MIME
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

            // Scan by value type rather than the package's internal AdditionalProperties key, so a
            // future key rename doesn't silently break us.
            var classification = update.AdditionalProperties?.Values.OfType<Classification>().FirstOrDefault();
            if (classification is null || classification.Risk < Risk.High) continue;

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
