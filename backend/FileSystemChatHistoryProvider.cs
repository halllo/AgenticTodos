using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

public class FileSystemChatHistoryProvider : IOChatHistoryProvider
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

public abstract class IOChatHistoryProvider : ChatHistoryProvider
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

        return await Read<List<ChatMessage>>($"{state.StoreId}_compacted.json")
            ?? await Read<List<ChatMessage>>($"{state.StoreId}_full.json")
            ?? [];
    }

    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        var state = this.sessionState.GetOrInitializeState(context.Session);

        var newMessages = context.RequestMessages.Concat(context.ResponseMessages ?? []).ToList();

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