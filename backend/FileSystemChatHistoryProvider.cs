using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// One JSON file per conversation under <c>ChatHistories</c>, named after the store id the base class
/// keeps in the session.
/// </summary>
/// <remarks>
/// The <see cref="JsonSerializer"/> calls pass no options, so persistence uses the ambient defaults
/// rather than <see cref="AIJsonUtilities.DefaultOptions"/>. Every content type this app persists
/// round-trips the same either way, and switching would not close the closed-polymorphism trap
/// <see cref="LoggingMiddleware"/> has to work around: the <c>[JsonPolymorphic]</c> set is declared on
/// <see cref="AIContent"/> itself, so an unregistered subtype fails under both. What it would change is
/// the shape of everything written from then on — <see cref="AIJsonUtilities.DefaultOptions"/> is
/// web-flavoured (camelCase, case-insensitive reads), the ambient default neither — and that is close to
/// a one-way door: it would still read the PascalCase files already on disk, but nothing would read its
/// own output back if the switch were reverted.
/// </remarks>
internal class FileSystemChatHistoryProvider : IOChatHistoryProvider
{
    private readonly string pathBase;

    public FileSystemChatHistoryProvider(
        string pathBase = "ChatHistories",
        IChatReducer? reducer = null,
        Func<AgentSession?, State>? stateInitializer = null,
        string? stateKey = null)
        : base(reducer, stateInitializer, stateKey)
    {
        this.pathBase = pathBase;
    }

    protected async override Task<T?> Read<T>(string filePath) where T : class
    {
        var p = Path.Combine(this.pathBase, filePath);
        if (!File.Exists(p))
        {
            return default;
        }

        using var read = File.OpenRead(p);
        return await JsonSerializer.DeserializeAsync<T>(read);
    }

    protected override async Task Write<T>(string filePath, T content)
    {
        Directory.CreateDirectory(this.pathBase);
        using var write = File.Create(Path.Combine(this.pathBase, filePath));
        await JsonSerializer.SerializeAsync(write, content);
    }
}

internal abstract class IOChatHistoryProvider : ChatHistoryProvider
{
    private readonly IChatReducer? reducer;
    private readonly ProviderSessionState<State> sessionState;

    public IOChatHistoryProvider(
        IChatReducer? reducer = null,
        Func<AgentSession?, State>? stateInitializer = null,
        string? stateKey = null)
    {
        this.reducer = reducer;
        this.sessionState = new ProviderSessionState<State>(
            stateInitializer ?? (_ => new State { StoreId = Guid.NewGuid() }),
            stateKey ?? this.GetType().Name);
    }

    public class State
    {
        public Guid StoreId { get; set; }
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var state = this.sessionState.GetOrInitializeState(context.Session);

        var history = await Read<List<ChatMessage>>($"{state.StoreId}_compacted.json")
            ?? await Read<List<ChatMessage>>($"{state.StoreId}_full.json")
            ?? [];

        // Restore redacted-thinking byte[] payloads that the JSON round-trip degrades to JsonElement,
        // so extended-thinking (Claude on Bedrock) history replays without a provider validation error.
        RedactedReasoningNormalizer.Normalize(history);

        // Scrub completed tool-approval pairs and auto-reject orphaned requests, so the
        // function-invocation layer can replay the history (see ToolApprovalHistoryNormalizer).
        ToolApprovalHistoryNormalizer.Normalize(history, context.RequestMessages);

        return history;
    }

    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        var state = this.sessionState.GetOrInitializeState(context.Session);

        // Per-turn context injected by a middleware above the agent (the conversation state snapshot)
        // arrives in RequestMessages but must not become part of the transcript — see
        // TransientChatMessages for why persisting it corrupts every later turn.
        var newMessages = context.RequestMessages
            .Concat(context.ResponseMessages ?? [])
            .Where(message => !message.IsTransient())
            .ToList();

        var fullFilePath = $"{state.StoreId}_full.json";
        var loaded = await Read<List<ChatMessage>>(fullFilePath);
        var allMessages = (loaded ?? []).Concat(newMessages).ToList();
        await Write(fullFilePath, allMessages);

        if (reducer is not null)
        {
            var compactedFilePath = $"{state.StoreId}_compacted.json";
            var loadedCompacted = await Read<List<ChatMessage>>(compactedFilePath);
            if (loadedCompacted is not null)
            {
                var allCompactedMessages = loadedCompacted.Concat(newMessages).ToList();
                var reduced = (await this.reducer.ReduceAsync(allCompactedMessages, cancellationToken)).ToList();
                await Write(compactedFilePath, reduced);
            }
            else
            {
                var reduced = (await this.reducer.ReduceAsync(allMessages, cancellationToken)).ToList();
                if (reduced.Count < allMessages.Count)
                {
                    // store compacted history, so next turns can use it with priority
                    await Write(compactedFilePath, reduced);
                }
            }
        }
    }

    protected abstract Task<T?> Read<T>(string filePath) where T : class;

    protected abstract Task Write<T>(string filePath, T content);
}
