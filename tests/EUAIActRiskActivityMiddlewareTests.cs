using System.Runtime.CompilerServices;
using System.Text.Json;
using AgenticTodos.Backend;
using EUAIActClassifier;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// The middleware surfaces the classifier's verdict as an <see cref="EUAIActRiskActivityContent"/>,
/// but only for <see cref="Risk.High"/> and above. <c>AGUIEndpoint.MapClientContent</c> then turns that
/// into the <c>ACTIVITY_SNAPSHOT</c> event (covered by <see cref="AguiClientContentMappingTests"/>);
/// what is tested here is which verdicts are surfaced at all.
/// </summary>
public class EUAIActRiskActivityMiddlewareTests
{
    [Theory]
    [InlineData(Risk.High)]
    [InlineData(Risk.Unacceptable)]
    public async Task HighRiskOrAbove_EmitsOneActivity(Risk risk)
    {
        var inner = new StubAgent { Classification = new Classification { Risk = risk, Category = "cat", Reason = "why" } };

        var updates = await Run(inner);

        var activity = Assert.Single(updates.SelectMany(u => u.Contents).OfType<EUAIActRiskActivityContent>());
        Assert.Equal(risk.ToString(), activity.Risk);
        Assert.Equal("cat", activity.Category);
        Assert.Equal("why", activity.Reason);
        Assert.NotEmpty(activity.MessageId);
    }

    [Theory]
    [InlineData(Risk.Unknown)]
    [InlineData(Risk.Minimal)]
    [InlineData(Risk.Limited)]
    public async Task BelowHighRisk_EmitsNothing(Risk risk)
    {
        // Risk.Unknown is what a classifier failure looks like, and it is the lowest tier — the app
        // must not assert a risk level it could not determine.
        var inner = new StubAgent { Classification = new Classification { Risk = risk, Category = "cat", Reason = "why" } };

        var updates = await Run(inner);

        Assert.Empty(updates.SelectMany(u => u.Contents).OfType<EUAIActRiskActivityContent>());
    }

    [Fact]
    public async Task NoClassification_EmitsNothingAndForwardsTheStream()
    {
        var inner = new StubAgent { Classification = null };

        var updates = await Run(inner);

        Assert.Empty(updates.SelectMany(u => u.Contents).OfType<EUAIActRiskActivityContent>());
        Assert.Equal("hello", Assert.Single(updates.SelectMany(u => u.Contents).OfType<TextContent>()).Text);
    }

    [Fact]
    public async Task MissingCategoryAndReason_BecomeEmptyStrings()
    {
        // Category/Reason are declared non-nullable but arrive from a model round-trip, so the
        // middleware's ?? fallback is what keeps a null out of the event payload.
        var inner = new StubAgent { Classification = new Classification { Risk = Risk.High, Category = null!, Reason = null! } };

        var updates = await Run(inner);

        var activity = Assert.Single(updates.SelectMany(u => u.Contents).OfType<EUAIActRiskActivityContent>());
        Assert.Equal(string.Empty, activity.Category);
        Assert.Equal(string.Empty, activity.Reason);
    }

    [Fact]
    public async Task RepeatedClassifications_OnlyTheFirstIsEmitted()
    {
        // The latch: one verdict per run, however many the classifier volunteers.
        var inner = new StubAgent
        {
            Classification = new Classification { Risk = Risk.High, Category = "cat", Reason = "why" },
            RepeatClassification = true,
        };

        var updates = await Run(inner);

        Assert.Single(updates.SelectMany(u => u.Contents).OfType<EUAIActRiskActivityContent>());
    }

    [Fact]
    public async Task NonStreamingPath_EmitsTheActivityToo()
    {
        var inner = new StubAgent { Classification = new Classification { Risk = Risk.High, Category = "cat", Reason = "why" } };

        var response = await BuildAgent(inner).RunAsync([]);

        Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<EUAIActRiskActivityContent>());
    }

    private static async Task<List<AgentResponseUpdate>> Run(StubAgent inner)
    {
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in BuildAgent(inner).RunStreamingAsync([]))
        {
            updates.Add(update);
        }
        return updates;
    }

    private static AIAgent BuildAgent(StubAgent inner) =>
        new AIAgentBuilder(inner).UseEUAIActRiskActivity().Build();

    /// <summary>
    /// Stands in for the classification agent: streams some text, then the verdict on a trailing
    /// side-channel update, which is how <c>UseEUAIActClassification</c> reports it.
    /// </summary>
    private sealed class StubAgent : AIAgent
    {
        public Classification? Classification { get; init; }
        public bool RepeatClassification { get; init; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
            => RunCoreStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.Assistant, [new TextContent("hello")]);

            if (Classification is null)
            {
                yield break;
            }

            yield return ClassificationUpdate(Classification);
            if (RepeatClassification)
            {
                yield return ClassificationUpdate(Classification);
            }
        }

        /// <summary>
        /// How <c>EUAIActClassificationAgent</c> attaches the verdict — the key the
        /// <c>update.EUAIActClassification</c> getter looks under (it also falls back to scanning the
        /// values for a <see cref="Classification"/>, so the app reads it key-agnostically).
        /// </summary>
        private static AgentResponseUpdate ClassificationUpdate(Classification classification) =>
            new()
            {
                AdditionalProperties = new() { ["EUAIActClassifier.Classification"] = classification },
            };
    }
}
