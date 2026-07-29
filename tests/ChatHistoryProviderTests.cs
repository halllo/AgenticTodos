// ChatHistoryProvider.InvokedContext's constructor is the one genuinely experimental API these tests
// touch; everything else in the tool-approval stack has since shipped as stable.
#pragma warning disable MAAI001

using AgenticTodos.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// Covers what the append-only history store does and does not persist. The transient rule is the
/// load-bearing half of the state-snapshot fix: <see cref="StateSnapshotMiddleware"/> injects the
/// snapshot as a system message above the agent, so it arrives in
/// <c>InvokedContext.RequestMessages</c> and would otherwise be appended once per turn — later turns
/// then replay stale snapshots ahead of the fresh one.
/// </summary>
public class ChatHistoryProviderTests
{
    [Fact]
    public async Task TransientMessages_AreNotPersisted()
    {
        var provider = new InMemoryHistoryProvider();
        var session = await CreateSessionAsync();

        await StoreAsync(provider, session,
            requestMessages:
            [
                new ChatMessage(ChatRole.System, "Current conversation state …").AsTransient(),
                new ChatMessage(ChatRole.User, "increment the counter"),
            ],
            responseMessages: [new ChatMessage(ChatRole.Assistant, "done")]);

        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant],
            provider.Stored.Select(m => m.Role));
        Assert.DoesNotContain(provider.Stored, m => m.Text.Contains("Current conversation state"));
    }

    [Fact]
    public async Task TransientMessages_DoNotAccumulateAcrossTurns()
    {
        // The regression this guards: one stale snapshot per turn, growing without bound.
        var provider = new InMemoryHistoryProvider();
        var session = await CreateSessionAsync();

        for (var turn = 1; turn <= 3; turn++)
        {
            await StoreAsync(provider, session,
                requestMessages:
                [
                    new ChatMessage(ChatRole.System, $"Current conversation state … counter {turn}").AsTransient(),
                    new ChatMessage(ChatRole.User, $"turn {turn}"),
                ],
                responseMessages: [new ChatMessage(ChatRole.Assistant, $"reply {turn}")]);
        }

        Assert.Equal(6, provider.Stored.Count);
        Assert.DoesNotContain(provider.Stored, m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task NonTransientMessages_ArePersisted()
    {
        // A system message the caller really sent must still be stored, so the mark is what matters.
        var provider = new InMemoryHistoryProvider();
        var session = await CreateSessionAsync();

        await StoreAsync(provider, session,
            requestMessages: [new ChatMessage(ChatRole.System, "You are a helpful assistant.")],
            responseMessages: []);

        Assert.Equal(ChatRole.System, Assert.Single(provider.Stored).Role);
    }

    private static AIAgent Agent { get; } = new NoopChatClient().AsAIAgent();

    private static async Task<AgentSession> CreateSessionAsync()
    {
        // A ChatClientAgent is the cheapest way to obtain a real AgentSession for the provider's
        // per-session state; no model call is made.
        return await Agent.CreateSessionAsync();
    }

    private static ValueTask StoreAsync(
        InMemoryHistoryProvider provider,
        AgentSession session,
        List<ChatMessage> requestMessages,
        List<ChatMessage> responseMessages)
        => ((ChatHistoryProvider)provider).InvokedAsync(
            new ChatHistoryProvider.InvokedContext(Agent, session, requestMessages, responseMessages));

    /// <summary>The real store logic (<see cref="IOChatHistoryProvider"/>) over a dictionary.</summary>
    private sealed class InMemoryHistoryProvider : IOChatHistoryProvider
    {
        private readonly Dictionary<string, object> files = [];

        public List<ChatMessage> Stored =>
            files.TryGetValue($"{StoreId}_full.json", out var content) ? (List<ChatMessage>)content : [];

        private Guid StoreId => this.files.Keys
            .Select(k => Guid.Parse(k.Split('_')[0]))
            .FirstOrDefault();

        protected override Task<T?> Read<T>(string filePath) where T : class
            => Task.FromResult(files.TryGetValue(filePath, out var content) ? (T?)content : null);

        protected override Task Write<T>(string filePath, T content)
        {
            files[filePath] = content!;
            return Task.CompletedTask;
        }
    }
}
