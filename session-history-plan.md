# Plan: Server-Side Session & Conversation History for an AG-UI Agentic Backend

## Context

This plan describes how to add server-side session and conversation history management to any project using `Microsoft.Agents.AI` and the AG-UI protocol. Currently, frontends resend the full message history on every request. After implementing this plan, frontends send only the current user message per turn, and the backend rehydrates the full history automatically.

---

## Architecture Overview

```
Frontend (per turn)
  → sends: { threadId, messages: [currentUserMessage only] }

Backend (per turn)
  1. Extract threadId from the AG-UI request (field: ag_ui_thread_id)
  2. Load AgentSession from disk using threadId + agentId
  3. Load full ChatMessage history from disk (linked to session via a StoreId Guid)
  4. Run agent — framework prepends full history automatically
  5. Append new messages to history file
  6. Save updated AgentSession to disk
  → streams back: SSE events
```

---

## Step 1 — Storage Directory Convention

Two directories hold all persistent state. They are created lazily at first write via `Directory.CreateDirectory`.

| Directory | File naming | Content |
|---|---|---|
| `AgentSessions/` | `{agentId}_{conversationId}.json` | Serialized `AgentSession` object |
| `ChatHistories/` | `{storeId}_full.json` | Accumulated `ChatMessage[]` array |
| `ChatHistories/` | `{storeId}_compacted.json` | Optionally reduced/summarized messages |

- **`agentId`** — the `AIAgent.Id` property (set when the agent is created)
- **`conversationId`** — the AG-UI `threadId` sent by the client on every request
- **`storeId`** — a `Guid` generated once per conversation, stored inside the `AgentSession`, links the session to its history files

---

## Step 2 — Session Store

**File to create:** `FileSystemSessionStore.cs`

**Base class:** `AgentSessionStore` (`Microsoft.Agents.AI.Hosting`)

Loads or creates an `AgentSession` for each conversation. The session is an opaque framework object that carries the `storeId` link to chat history. Serialization/deserialization is fully delegated to the agent framework.

```csharp
public class FileSystemSessionStore : AgentSessionStore
{
    private readonly string pathBase;
    private readonly ILogger<FileSystemSessionStore> logger;

    public FileSystemSessionStore(ILogger<FileSystemSessionStore> logger, string pathBase = "AgentSessions")
    {
        this.logger = logger;
        this.pathBase = pathBase;
    }

    public override async ValueTask<AgentSession> GetSessionAsync(
        AIAgent agent, string conversationId, CancellationToken cancellationToken = default)
    {
        this.logger.LogInformation("Loading session for conversation {conversationId}", conversationId);
        var path = GetPath(conversationId, agent.Id);
        if (!File.Exists(path))
        {
            return await agent.CreateSessionAsync(cancellationToken);
        }
        using var stream = File.OpenRead(path);
        var sessionContent = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: cancellationToken);
        return await agent.DeserializeSessionAsync(sessionContent, cancellationToken: cancellationToken);
    }

    public override async ValueTask SaveSessionAsync(
        AIAgent agent, string conversationId, AgentSession session, CancellationToken cancellationToken = default)
    {
        this.logger.LogInformation("Saving session for conversation {conversationId}", conversationId);
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        Directory.CreateDirectory(this.pathBase);
        using var stream = File.Create(GetPath(conversationId, agent.Id));
        await JsonSerializer.SerializeAsync(stream, serialized, cancellationToken: cancellationToken);
    }

    private string GetPath(string conversationId, string agentId) =>
        Path.Combine(this.pathBase, $"{agentId}_{conversationId}.json");
}
```

**DI registration** (in `Program.cs`):
```csharp
builder.Services.AddSingleton<AgentSessionStore, FileSystemSessionStore>();
```

---

## Step 3 — Chat History Provider

Split into two classes: an abstract base (`IOChatHistoryProvider`) that owns the load/store logic, and a concrete file system implementation (`FileSystemChatHistoryProvider`) that handles I/O. This makes the storage backend swappable.

**File to create:** `FileSystemChatHistoryProvider.cs` (both classes live here)

### 3a. State Object

A small object stored inside the `AgentSession` that links a session to its history files.

```csharp
public class State
{
    public Guid StoreId { get; set; }
}
```

`StoreId` is initialized to `Guid.NewGuid()` the first time a session is used and then persisted via `ProviderSessionState<State>` inside the `AgentSession`.

### 3b. Abstract Base Class

```csharp
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

    // Called by the framework automatically BEFORE the agent runs.
    // Return value is prepended to the message list before the LLM call.
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var state = this.sessionState.GetOrInitializeState(context.Session);
        return await Read<List<ChatMessage>>($"{state.StoreId}_compacted.json")
            ?? await Read<List<ChatMessage>>($"{state.StoreId}_full.json")
            ?? [];
    }

    // Called by the framework automatically AFTER the agent runs.
    // context.RequestMessages = what was sent; context.ResponseMessages = what the agent replied.
    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        var state = this.sessionState.GetOrInitializeState(context.Session);
        var newMessages = context.RequestMessages.Concat(context.ResponseMessages ?? []).ToList();

        var fullFilePath = $"{state.StoreId}_full.json";
        var loaded = await Read<List<ChatMessage>>(fullFilePath);
        var allMessages = (loaded ?? []).Concat(newMessages).ToList();
        await Write(fullFilePath, allMessages);

        // Optional: write compacted history if a reducer is configured
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
                    await Write(compactedFilePath, reduced);
            }
        }
    }

    protected abstract Task<T?> Read<T>(string filePath) where T : class;
    protected abstract Task Write<T>(string filePath, T content);

    public class State
    {
        public Guid StoreId { get; set; }
    }
}
```

**Framework hooks:**
- `ProvideChatHistoryAsync` — the framework calls this before each run; the returned messages are automatically prepended to the incoming message list before the LLM call
- `StoreChatHistoryAsync` — the framework calls this after each run; `RequestMessages` is the full list sent to the LLM (including prior history), `ResponseMessages` is the agent's reply

### 3c. Concrete File System Implementation

```csharp
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
        if (!File.Exists(p)) return default;
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
```

**Wire into agent creation** (in `Program.cs`):
```csharp
chatClient.AsAIAgent(
    name: "MyAgent",
    tools: tools,
    historyProvider: new FileSystemChatHistoryProvider())
```

---

## Step 4 — Per-Request Session Lifecycle (HttpContextRoutingAgent)

**File to create:** `HttpContextRoutingAgent.cs`

**Purpose:** On each HTTP request, extract the `threadId`, load (or create) the session, run the agent, and save the updated session. The history is loaded/saved transparently by the `FileSystemChatHistoryProvider` registered with the agent.

**Base class:** `AIAgent` (`Microsoft.Agents.AI`)

```csharp
public class HttpContextRoutingAgent(
    IHttpContextAccessor httpContextAccessor,
    Func<HttpContext, ValueTask<AIHostAgent>> resolveAgent) : AIAgent
{
    protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
    {
        var agent = await GetAgent();
        return await agent.CreateSessionAsync(cancellationToken);
    }

    protected override async ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedSession, CancellationToken cancellationToken)
    {
        var agent = await GetAgent();
        return await agent.DeserializeSessionAsync(serializedSession, cancellationToken: cancellationToken);
    }

    protected override async ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, CancellationToken cancellationToken)
    {
        var agent = await GetAgent();
        return await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
    }

    protected override async ValueTask<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, CancellationToken cancellationToken)
    {
        var agent = await GetAgent();
        var conversationId = GetConversationId(options);
        var dedicatedSession = session is null
            ? await agent.GetOrCreateSessionAsync(conversationId, cancellationToken)
            : null;
        var response = await agent.RunAsync(messages, session ?? dedicatedSession, options, cancellationToken);
        if (dedicatedSession is not null)
            await agent.SaveSessionAsync(conversationId, dedicatedSession, cancellationToken);
        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var agent = await GetAgent();
        var conversationId = GetConversationId(options);
        var dedicatedSession = session is null
            ? await agent.GetOrCreateSessionAsync(conversationId, cancellationToken)
            : null;
        await foreach (var update in agent.RunStreamingAsync(
            messages, session ?? dedicatedSession, options, cancellationToken))
            yield return update;
        if (dedicatedSession is not null)
            await agent.SaveSessionAsync(conversationId, dedicatedSession, cancellationToken);
    }

    private ValueTask<AIHostAgent> GetAgent()
    {
        var context = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No HTTP context.");
        return resolveAgent(context);
    }

    // The AG-UI client library sends threadId in the request body;
    // the controller maps it to ag_ui_thread_id in AdditionalProperties.
    private static string GetConversationId(AgentRunOptions? options) =>
        (options as ChatClientAgentRunOptions)
            ?.ChatOptions?.AdditionalProperties?["ag_ui_thread_id"]?.ToString()
        ?? throw new ArgumentNullException("No conversation ID provided ('ag_ui_thread_id').");
}
```

**DI registration** (in `Program.cs`):

```csharp
builder.Services.AddHttpContextAccessor();

// The resolver: given the current HTTP request, return the right AIHostAgent
builder.Services.AddSingleton<Func<HttpContext, ValueTask<AIHostAgent>>>(async httpContext =>
{
    // Resolve whichever agent this request targets (e.g., from route, config, etc.)
    var agent = ResolveYourAgent(httpContext);
    var sessionStore = httpContext.RequestServices.GetRequiredService<AgentSessionStore>();
    return new AIHostAgent(agent, sessionStore);
});

builder.Services.AddSingleton<HttpContextRoutingAgent>();
```

The `resolveAgent` lambda is the only project-specific piece — replace `ResolveYourAgent(httpContext)` with however your project selects an agent (by route param, config, etc.).

**Endpoint mapping** (in `Program.cs`):
```csharp
app.MapPost("/agents/{alias}/agui", (HttpContextRoutingAgent agent, ...) =>
{
    // Parse AG-UI input, call agent.RunStreamingAsync(), stream SSE response
    // This wiring depends on your HTTP/SSE setup
});
```

---

## Step 5 — Frontend: Send Only the Current Message

The only required frontend change is to **not resend the full history** on each turn. The backend now owns the history.

Before this change, the frontend collected all prior messages and sent them on every request. After this change, the frontend sends only the current user message.

**AG-UI / TypeScript pattern:**
```typescript
// On each turn — clear local agent message state first, then add only the new message
this.agent.setMessages([]);
this.agent.addMessages([{ id: '', role: 'user', content: userInput }]);
await this.agent.runAgent();
```

The AG-UI client library automatically includes `threadId` in every request body (generated once per conversation). The backend uses `threadId` to load the full history, so the frontend does not need to send it.

**C# CLI pattern:**
```csharp
// After receiving the agent response, clear the local message list
messages.Clear();
// Next turn: only add the new user message before calling RunStreamingAsync
```

---

## Files to Create

| File | Purpose |
|---|---|
| `FileSystemSessionStore.cs` | Load/save `AgentSession` per conversation |
| `FileSystemChatHistoryProvider.cs` | Load/append `ChatMessage[]` per conversation; includes `IOChatHistoryProvider` abstract base |
| `HttpContextRoutingAgent.cs` | Per-request session lifecycle (load → run → save) |

All DI wiring goes in `Program.cs`.

---

## NuGet Packages Required

| Package | Used for |
|---|---|
| `Microsoft.Agents.AI` | `AIAgent`, `AgentSession`, `ChatHistoryProvider`, `IChatReducer`, `InvokingContext`, `InvokedContext` |
| `Microsoft.Agents.AI.Hosting` | `AgentSessionStore`, `AIHostAgent`, `ProviderSessionState<T>` |
| `Microsoft.Extensions.AI` | `IChatClient`, `ChatMessage`, `ChatOptions` |
| `Microsoft.Extensions.Logging` | `ILogger<T>` |

---

## Verification

1. **Send two messages** from the frontend; confirm the second response uses context from the first without the frontend resending history
2. **Restart the backend** mid-conversation; send a third message; confirm the agent still has full context (session + history reloaded from disk)
3. **Inspect `AgentSessions/`** — one JSON file per conversation, named `{agentId}_{threadId}.json`
4. **Inspect `ChatHistories/`** — one `{storeId}_full.json` per conversation, growing by two entries (user + assistant) each turn
5. **Confirm the frontend sends only one message per turn** by inspecting the network request body — `messages` array should have exactly one entry (the current user message)
