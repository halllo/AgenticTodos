# Plan: Server-Side Session & Conversation History for an AG-UI Agentic Backend

## Context

Adding server-side session and conversation history to any project on `Microsoft.Agents.AI` and the AG-UI protocol. Before: frontends resend the full history every request. After: only the current user message, the backend rehydrating the rest.

> **Status in this repo: built.** [backend/](backend/) holds all three types: `FileSystemSessionStore`, `FileSystemChatHistoryProvider` (with its `IOChatHistoryProvider` base) and `HttpContextRoutingAgent` — the last `internal sealed` and nested inside `AGUIEndpoint.cs`, next to the `MapAGUIServer` call that is its only production caller (Step 4). Pinned by [`FileSystemSessionStoreTests`](tests/FileSystemSessionStoreTests.cs), [`ChatHistoryProviderTests`](tests/ChatHistoryProviderTests.cs), [`HttpContextRoutingAgentTests`](tests/HttpContextRoutingAgentTests.cs), [`AguiSessionStoreLifetimeTests`](tests/AguiSessionStoreLifetimeTests.cs). It stays a plan to stay portable: "File to create" addresses the *next* project; the two history-provider types and `HttpContextRoutingAgent` are `internal` here (tests reach them via `InternalsVisibleTo`) where a new project starts `public`, while `FileSystemSessionStore` is public as listed; listings are cut to signatures plus what a reader cannot infer, bodies elided as `…`; each type's XML docs argue its *why*.

---

## Architecture Overview

```text
Frontend per turn → { threadId, messages: [currentUserMessage only] }
Backend per turn: 1. the AG-UI server SDK binds RunAgentInput, resolving the session from its threadId
  (ChatOptions.TryGetRunAgentInput() is the app-side fallback, for direct invocations — Step 4);
  2. load AgentSession by threadId + agentId; 3. load the full ChatMessage history (linked by a StoreId
  Guid); 4. run agent, the framework prepending that history; 5. append the new messages; 6. save the
  AgentSession → streams SSE events back.
```

---

## Step 1 — Storage Directory Convention

Two directories, created lazily at first write via `Directory.CreateDirectory`. (A third, `UploadedFiles/`, belongs to file attachments — [attachments.md](attachments.md).)

| Directory | File naming | Content |
| --- | --- | --- |
| `AgentSessions/` | `{agentId}_{sessionStoreId}.json` | Serialized `AgentSession` object |
| `ChatHistories/` | `{storeId}_full.json` | Accumulated `ChatMessage[]` array |
| `ChatHistories/` | `{storeId}_compacted.json` | Optionally reduced/summarized messages |

- **`agentId`** — `AIAgent.Id`; for the routing agent computed per request from the route (`routed-{alias}`), not at construction (`IdCore`, Step 4)
- **`sessionStoreId`** — the AG-UI `threadId`, and the base class's name for it: opaque client input by contract, hence escaped and bounded (Step 2)
- **`storeId`** — a `Guid` per conversation, held in the `AgentSession`, linking it to its history files

---

## Step 2 — Session Store

**File to create:** `FileSystemSessionStore.cs` — **base class:** `AgentSessionStore` (`Microsoft.Agents.AI.Hosting`)

Loads or creates the `AgentSession` per conversation — an opaque framework object carrying the `storeId` link to chat history, serialized by the framework itself.

```csharp
// ctor: (ILogger<FileSystemSessionStore> logger, string pathBase = "AgentSessions")
public class FileSystemSessionStore : AgentSessionStore
{
    // GetSessionAsync, SaveSessionAsync and DeleteSessionAsync are all abstract on the base, so all three
    // must be implemented — each (AIAgent agent, string sessionStoreId, …, CancellationToken), logging
    // a LogInformation of {SessionStoreId}, plain File.* I/O around
    // agent.Create/Deserialize/SerializeSessionAsync (delete returning ValueTask.CompletedTask).

    // Keyed by agent.Id, so that id must be stable across restarts and distinct per agent (Step 4's
    // IdCore). Both halves go through Bound(): Uri.EscapeDataString, with a truncated SHA-256 of the
    // escaped form substituted once it outgrows its share of the 255-byte name. Both guards are needed,
    // and both failures surface only inside SaveSessionAsync — after the SSE response is committed, where
    // error middleware can no longer report anything. Arithmetic, exception per case, and why escaping
    // rather than validating: the XML docs on this file, plus README.md.
    private string GetPath(string sessionStoreId, string agentId) =>
        Path.Combine(this.pathBase, $"{Bound(agentId)}_{Bound(sessionStoreId)}.json");
}
```

Full argument: [backend/FileSystemSessionStore.cs](backend/FileSystemSessionStore.cs), [README.md](README.md#-ag-ui-endpoint-mappings-do-not-support-per-request-agent-selection).

**DI registration** (in `Program.cs` — verbatim what this repo has, the two lines just not adjacent):

```csharp
builder.Services.AddSingleton<AgentSessionStore, FileSystemSessionStore>();
builder.Services.AddAGUISessionStore();   // forwarding stand-in
```

Keyed by the routing agent's name, `AddAGUISessionStore()`'s singleton stand-in forwards each call to the current request's container, freeing the real store to use any lifetime — [README.md](README.md#-ag-ui-endpoint-mappings-do-not-support-per-request-agent-selection), third bullet.

---

## Step 3 — Chat History Provider

**File to create:** `FileSystemChatHistoryProvider.cs`, holding both classes: an abstract `IOChatHistoryProvider` with the load/store logic, a concrete `FileSystemChatHistoryProvider` with the I/O — so the backend is swappable.

### 3a. State Object

`State` links a session to its history files and lives inside the `AgentSession`, nested on the base so the subclass's `stateInitializer` can name it without a second type. `StoreId` becomes `Guid.NewGuid()` on first use, persisted via `ProviderSessionState<State>`.

### 3b. Abstract Base Class

```csharp
public abstract class IOChatHistoryProvider : ChatHistoryProvider
{
    // ctor(IChatReducer? reducer = null, Func<AgentSession?, State>? stateInitializer = null,
    //   string? stateKey = null) keeps the reducer and builds ProviderSessionState<State> from
    //   (stateInitializer ?? (_ => new State { StoreId = Guid.NewGuid() }), stateKey ?? this.GetType().Name)
    public class State { public Guid StoreId { get; set; } }

    // Framework hook, automatic BEFORE the run: the return value is prepended to the message list before
    // the LLM call, which makes it the only place the two load-time repairs can run.
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var state = this.sessionState.GetOrInitializeState(context.Session);
        var history = await Read<List<ChatMessage>>($"{state.StoreId}_compacted.json")
            ?? await Read<List<ChatMessage>>($"{state.StoreId}_full.json") ?? [];
        RedactedReasoningNormalizer.Normalize(history);
        ToolApprovalHistoryNormalizer.Normalize(history, context.RequestMessages);
        return history;
    }

    // Framework hook, automatic AFTER the run. Read + Write append
    //   context.RequestMessages.Concat(context.ResponseMessages ?? []).Where(m => !m.IsTransient()).ToList()
    // to {StoreId}_full.json; with a reducer, the same new messages also go onto an existing
    // {StoreId}_compacted.json to be this.reducer.ReduceAsync'd, or the whole list is reduced and written
    // only if that made it shorter.
    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken cancellationToken = default) { … }

    protected abstract Task<T?> Read<T>(string filePath) where T : class;
    protected abstract Task Write<T>(string filePath, T content);
}
```

`context.RequestMessages` is only this turn's arrivals (caller input plus anything `AIContextProvider`s added) and explicitly **excludes** the history just returned; `context.ResponseMessages` is the reply. That exclusion is what makes append-only storage correct, and both repairs unavoidable: the store never gets the run's mutated history back. `IsTransient()` drops what a middleware marked `TransientChatMessages.AsTransient()`.

- `RedactedReasoningNormalizer`: without it a redacted-thinking `byte[]` degraded to a base64 `JsonElement` by the JSON round trip makes the provider reject every later turn of a Claude-on-Bedrock conversation — [README.md](README.md#-extended-thinking-reasoning-support), "redacted-thinking persistence".
- `ToolApprovalHistoryNormalizer`: without it, approval content `FunctionInvokingChatClient` mutated in memory makes the first replay throw *"ToolApprovalRequestContent found … no matching ToolApprovalResponseContent"*, stuck for good — [human-in-the-loop.md](human-in-the-loop.md#history-replay-why-the-normalizer-is-needed).

### 3c. Concrete File System Implementation

Only I/O: a ctor `(string pathBase = "ChatHistories", IChatReducer? reducer = null, Func<AgentSession?, State>? stateInitializer = null, string? stateKey = null)` over `base(reducer, stateInitializer, stateKey)`, then `Read<T>`/`Write<T>` across `Path.Combine(pathBase, filePath)` with `File.Exists`/`File.OpenRead`/`Directory.CreateDirectory`/`File.Create` and `JsonSerializer.DeserializeAsync`/`SerializeAsync`. Wire it in as `ChatHistoryProvider = new FileSystemChatHistoryProvider()` in the `ChatClientAgentOptions` (beside `Name = "MyAgent"`, `ChatOptions { Tools = tools }`) passed to `chatClient.AsAIAgent(options, services)`.

---

## Step 4 — Per-Request Session Lifecycle (HttpContextRoutingAgent)

**File to create:** `HttpContextRoutingAgent.cs` — **base class:** `AIAgent` (`Microsoft.Agents.AI`)

Per HTTP request: take the `threadId`, load or create the session, run the agent, save it back — the registered `FileSystemChatHistoryProvider` handling history. Three details this must get right, and what breaks without each: [README.md](README.md#-ag-ui-endpoint-mappings-do-not-support-per-request-agent-selection).

```csharp
public class HttpContextRoutingAgent(
    IHttpContextAccessor httpContextAccessor,
    Func<HttpContext, ValueTask<AIHostAgent>> resolveAgent) : AIAgent
{
    private const string ResolvedAgentKey = "AgenticTodos.RoutedAgent";

    public override string? Name => "routed";   // the name IS the session store's DI key (Step 2)

    // Route-derived, alias-*shaped* only (the id is half a file name); FileSystemSessionStore keys session
    // files by agent.Id, where the base implementation returns a fresh Guid per instance.
    protected override string? IdCore =>
        httpContextAccessor.HttpContext?.Request.RouteValues["alias"]?.ToString() is { Length: > 0 } alias &&
        alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? $"routed-{alias}" : "routed";

    // CreateSessionCoreAsync(ct), DeserializeSessionCoreAsync(JsonElement, JsonSerializerOptions?, ct) and
    // SerializeSessionCoreAsync(AgentSession, JsonSerializerOptions?, ct) forward one-line to
    // `(await GetAgentAsync()).CreateSessionAsync/DeserializeSessionAsync/SerializeSessionAsync(…)`.
    // RunCoreAsync(messages, session = null, options = null, ct) returns Task<AgentResponse> — note, not
    // ValueTask<AgentResponse>; RunCoreStreamingAsync(…, [EnumeratorCancellation] ct) returns
    // IAsyncEnumerable<AgentResponseUpdate>. Both, after `var agent = await GetAgentAsync()`:
    var conversationId = session is null ? GetConversationId(options) : null;  // guarded: it *throws*
    var dedicatedSession = conversationId is null                             //   without a ThreadId
        ? null : await agent.GetOrCreateSessionAsync(conversationId, cancellationToken);
    // … then agent.RunAsync / an await foreach over agent.RunStreamingAsync with (session ??
    //   dedicatedSession, options, cancellationToken), then agent.SaveSessionAsync(conversationId!,
    //   dedicatedSession, cancellationToken) if dedicatedSession is not null — streaming *after* the loop
    //   (HttpContextRoutingAgentTests.StreamingRunWithoutASession_SavesOnlyOnceTheStreamIsDrained).

    // TryGetValue/AsTask cache the in-flight Task — not its result — under ResolvedAgentKey in
    // HttpContext.Items, collapsing the several lookups the SDK triggers per run into one.
    private ValueTask<AIHostAgent> GetAgentAsync() { … }

    // Reads RunAgentInput off ChatOptions.AdditionalProperties via TryGetRunAgentInput (only
    // ChatClientAgentRunOptions carry those ChatOptions), else throws InvalidOperationException:
    // "No conversation ID provided (AG-UI RunAgentInput.ThreadId)."
    private static string GetConversationId(AgentRunOptions? options) { … }
}
```

**DI registration and endpoint mapping** (in `Program.cs`):

```csharp
builder.Services.AddHttpContextAccessor();
// The resolver — the only project-specific piece — returns the AIHostAgent this request addresses:
//   new AIHostAgent(ResolveYourAgent(httpContext), RequestServices.GetRequiredService<AgentSessionStore>())
builder.Services.AddSingleton<Func<HttpContext, ValueTask<AIHostAgent>>>(async httpContext => …);
builder.Services.AddSingleton<HttpContextRoutingAgent>();

app.MapAGUIServer("/agents/routed/{alias}/agui", routingAgent).WithMetadata(CreateStreamOptions());
```

Replace `ResolveYourAgent(httpContext)` with however the project picks an agent. Here `Program.cs` holds only `AddHttpContextAccessor()` plus `app.MapAGUIViaHttpRoutingAgent();`, whose body in [backend/AGUIEndpoint.cs](backend/AGUIEndpoint.cs) constructs `new HttpContextRoutingAgent(...)` inline, passes `resolveAgent` as a constructor argument, and makes the `MapAGUIServer` call — route pattern and `.WithMetadata(...)` included — beside the `RoutedPathPrefix` constant [`AguiRunErrorMiddleware`](backend/AguiRunErrorMiddleware.cs) is scoped to, so route and error middleware cannot drift apart.

Do not hand-roll the endpoint: `MapAGUIServer` owns the SSE plumbing (binding `RunAgentInput`, defaulting a missing `ThreadId`, `GetOrCreateSessionAsync` on the thread id, `AsAGUIEventStreamAsync`, saving once the stream drains) *and* the `GetKeyedService<AgentSessionStore>(agent.Name)` lookup Step 2 depends on — under an `app.MapPost`, `AddAGUISessionStore()` is dead code and the session never persists. `IdCore` also needs `{alias}` to be a real route parameter of *this* endpoint; `.WithMetadata(...)` matters only when the app maps its own `AIContent` onto AG-UI events ([custom-agui-events.md](custom-agui-events.md)).

---

## Step 5 — Frontend: Send Only the Current Message

The only required frontend change is to **not resend the full history** on each turn. AG-UI / TypeScript:

```typescript
// On each turn — add only the new message; the list is already empty
this.agent.addMessages([{ id: '', role: 'user', content: userInput }]);
await this.agent.runAgent();

// …and clear it wherever a run ends, not on the way in:
onRunFinishedEvent: () => { agent.setMessages([]); /* … */ }
```

`setMessages([])` belongs on run *end*, not on the way in: a resumed run (a client-side tool result, an interrupt decision) also goes out with only the new messages and never passes through the composer. The C# CLI equivalent is `messages.Clear()` after the response, then only the new user message — or that tool call's result, if the run ended on one — before the next `RunStreamingAsync`.

`threadId` reaches every request body automatically, but *automatically* differs per client:

- **`@ag-ui/client`** generates one per conversation — `AbstractAgent`'s constructor does `this.threadId = threadId ?? uuidv4()` — so an `HttpAgent` instance is one thread for its lifetime, and a new agent (switching models, say) starts a fresh session.
- **The .NET `AGUIChatClient`** mints a **fresh thread id per run** unless the caller pins one: `threadId = input.ThreadId ?? ExtractTemporaryThreadId(messages) ?? ExtractThreadIdFromOptions(options) ?? AGUIIdGenerator.NewThreadId()`. Left alone, every turn addresses a different session and history never accumulates — hence the CLI pinning one id for the whole REPL via `RunAgentInput.ThreadId` through `RawRepresentationFactory` ([cli/Verbs/Agent.cs](cli/Verbs/Agent.cs)).

---

## NuGet Packages Required

| Package | Used for |
| --- | --- |
| `Microsoft.Agents.AI` | `AIAgent`, `AgentSession`, `ChatHistoryProvider`, `InvokingContext`, `InvokedContext` |
| `Microsoft.Agents.AI.Abstractions` | `ProviderSessionState<T>` — namespace `Microsoft.Agents.AI`, so no extra `using`; transitive with the above |
| `Microsoft.Agents.AI.Hosting` | `AgentSessionStore`, `AIHostAgent` |
| `AGUI.Abstractions` | `RunAgentInput` |
| `AGUI.Server` | `ChatOptions.TryGetRunAgentInput()`, `AGUIStreamOptions` |
| `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` | `MapAGUIServer`, `AddAGUIServer` |
| `Microsoft.Extensions.AI` | `IChatClient`, `ChatMessage`, `ChatOptions`, `IChatReducer` — the reducer is MEAI's, not the agent framework's, so `FileSystemChatHistoryProvider.cs` needs `using Microsoft.Extensions.AI;` beside `using Microsoft.Agents.AI;` |
| `Microsoft.Extensions.Logging` | `ILogger<T>` |

These three replaced the discontinued `Microsoft.Agents.AI.AGUI`. Referencing the first two explicitly is only necessary when the app touches the protocol types itself — reading `RunAgentInput`, mapping its own content onto events — as every sample above does. Pin them to the versions the hosting package resolves.

---

## Verification

What to check by hand in a new project, and what this repo's tests pin — look there first on a regression.

| Test file | Pins |
| --- | --- |
| [`FileSystemSessionStoreTests`](tests/FileSystemSessionStoreTests.cs) | Step 2: save→get round trip, unknown session → fresh one, delete, distinct threads → distinct files, a hostile `sessionStoreId` not escaping the path base while an ordinary one stays readable |
| [`ChatHistoryProviderTests`](tests/ChatHistoryProviderTests.cs) | Step 3: transient messages dropped and never accumulating, everything else appended |
| [`HttpContextRoutingAgentTests`](tests/HttpContextRoutingAgentTests.cs) | Step 4: the lookup awaited once per request, shared by concurrent callers; `IdCore` route-derived, alias-shaped only; `Name` as the session-store DI key; a session-less run taking the thread id from the AG-UI input and saving after (streaming: after drain); a run with a session never touching the store; one with neither failing |
| [`AguiSessionStoreLifetimeTests`](tests/AguiSessionStoreLifetimeTests.cs) | `AddAGUISessionStore()`: the stand-in resolving from the root provider with a scoped real store, forwarding to the request's container, failing readably outside a request |

End to end: a second message uses context from the first without the frontend resending history (its `messages` array holding exactly one entry), and a third after a mid-conversation restart still has full context.
