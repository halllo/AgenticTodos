# Custom AG-UI Events

Emits AG-UI events the protocol models but `Microsoft.Extensions.AI` has no content type for — state snapshots, activity snapshots — from inside the agent pipeline.

## Why this is now a supported extension point

The AG-UI server SDK (`AGUI.Server`) turns the `ChatResponseUpdate` content it knows into protocol events and hands **everything else** to the fallbacks registered on `AGUIStreamOptions` (tool approvals come with a caveat — [human-in-the-loop.md](human-in-the-loop.md)):

```
Agent middleware yields AgentResponseUpdate { Contents = [ConversationStateContent, …] }
     ↓
AIHostAgent → AsChatResponseUpdatesAsync()
     ↓
AsAGUIEventStreamAsync(context)          ← context carries the AGUIStreamOptions
     ↓
built-in mappings (text / reasoning / tool calls/results / approvals / interrupts)
     ↓ content not handled
MapContent fallback → AGUIEndpoint.MapClientContent
     ↓
StateSnapshotEvent · ActivitySnapshotEvent · …  → SSE
```

`AGUIStreamOptions` reaches the endpoint as metadata — `app.MapAGUIServer(pattern, agent).WithMetadata(CreateStreamOptions())`; `IOptions<AGUIStreamOptions>` from DI is the fallback when the endpoint carries none.

> Historically unreachable: in `Microsoft.Agents.AI.AGUI` the whole protocol surface was `internal` — `BaseEvent` and every event class — and the endpoint's SSE result type is internal to this day, so `MapAGUI` was a black box with no seam for an extra event. This repo therefore rewrote the serialized SSE bytes by intercepting `HttpContext.Response.Body` (`SseEventInjectionMiddleware`, plus one injector per event kind). That machinery is gone: the mappings replace it with real types and no byte-level rewriting.

## Files

| File | Role |
|---|---|
| [`backend/AguiClientContent.cs`](backend/AguiClientContent.cs) | `ConversationStateContent`, `McpAppActivityContent`, `EUAIActRiskActivityContent` — emitted for the client, not the model |
| [`backend/StateSnapshotMiddleware.cs`](backend/StateSnapshotMiddleware.cs) · [`DetectMcpAppsActivityMiddleware.cs`](backend/DetectMcpAppsActivityMiddleware.cs) · [`EUAIActRiskActivityMiddleware.cs`](backend/EUAIActRiskActivityMiddleware.cs) | The producers, one agent middleware per content type |
| [`backend/AGUIEndpoint.cs`](backend/AGUIEndpoint.cs) | `CreateStreamOptions()`, `MapClientContent(...)`, `ConfigureAguiJson(...)`, `AddAGUIJson(...)` — together, so a test can assert both that the halves agree and that the app installs them |
| [`backend/Program.cs`](backend/Program.cs) | Calls `AddAGUIJson()`; builds the emitting middleware chain (`CreateAgent`) |
| [`backend/AguiRunErrorMiddleware.cs`](backend/AguiRunErrorMiddleware.cs) | SSE `RUN_ERROR` instead of an HTTP 500 — see [Errors](#errors) |
| [`tests/AguiClientContentMappingTests.cs`](tests/AguiClientContentMappingTests.cs) | Every mapping; foreign content not claimed |
| [`tests/AguiEndpointWiringTests.cs`](tests/AguiEndpointWiringTests.cs) | Whether the mappings run at all: `AGUIStreamOptions` reaches the endpoint as metadata, the error middleware's prefix still covers the route, every mapped content type survives `rawEvent` serialization |

## Adding an event kind

Three edits — the content type in `AguiClientContent.cs`, its registration in `AGUIEndpoint.ConfigureAguiJson`, its mapping in `AGUIEndpoint.MapClientContent` — then emit it from any agent middleware:

```csharp
// 1
internal sealed class MyActivityContent(string messageId, string detail) : AIContent
{
    public string MessageId { get; } = messageId;
    public string Detail { get; } = detail;
}

// 2
options.AddAIContentType<MyActivityContent>("agenticTodos.myActivity");

// 3 — anonymous types keep property names verbatim, which is what the frontend reads
MyActivityContent my =>
[
    new ActivitySnapshotEvent
    {
        MessageId = my.MessageId,
        ActivityType = "my-activity",
        Replace = true,
        Content = JsonSerializer.SerializeToElement(new { detail = my.Detail }),
    }
],

// 4
yield return new AgentResponseUpdate { Contents = [new MyActivityContent(id, detail)] };
```

Return `null` for content that is not yours so the SDK keeps looking. Step 2 is **required** — the SDK serializes every update into the event's `rawEvent` field, and `AIContent` fails serialization for unregistered subtypes — and lives beside the mapping, one edit apart, rather than in `Program.cs`, which only calls the `AddAGUIJson()` seam onto the endpoint's JSON options (`Microsoft.AspNetCore.Http.Json.JsonOptions`) — [why, in full](backend/AGUIEndpoint.cs). [`LoggingMiddleware`](backend/LoggingMiddleware.cs) reuses the registrations, so a Debug trace cannot fail the request it is tracing.

## Other hooks on `AGUIStreamOptions`

| Hook | Use |
|---|---|
| `MapContent(content => events)` | Any unhandled `AIContent` → events (used here) |
| `MapInterrupt(content => interrupt)` | Unhandled content → a human-in-the-loop interrupt on `RUN_FINISHED`. **Unused:** the HITL pause emits `InterruptRequestContent`, mapped natively — [human-in-the-loop.md](human-in-the-loop.md) |
| `MapResult(toolName, frc => events)` / `MapCall(toolName, fcc => events)` | Extra events after a specific tool's result/call |
| `MapResultAsStateSnapshot(toolName)` / `MapResultAsStateDelta(toolName)` | A tool's `JsonElement` result as state |

## Errors

The SDK does not translate exceptions into protocol events: an unhandled failure returns an HTTP 500 with a non-SSE body — a transport failure to every AG-UI client, and `@ag-ui/client`'s verifier is left with a run that never started, so nothing renders. [`AguiRunErrorMiddleware`](backend/AguiRunErrorMiddleware.cs) wraps the endpoints under `/agents/routed` and reshapes a pre-stream failure into the protocol's own error shape:

```text
data: {"type":"RUN_STARTED","threadId":"","runId":""}

data: {"type":"RUN_ERROR","message":"Unknown agent alias 'nope'.","code":"EagerError"}
```

- `RUN_STARTED` first, because clients reject any event before a run began; both ids empty, because this middleware deliberately does not read the request body — the SDK has bound them by then, but they are unreachable from outside the endpoint, and clients only correlate with them.
- **Only an `AguiClientException`'s message goes on the wire** — a caller-caused failure whose text is safe to show, an unknown alias being this app's one. Every other unhandled exception the catch admits (a provider credential failure, a DI resolution error) describes server internals, so it is logged and the client told *"The agent run could not be started."*
- Deliberately **not** converted: `OperationCanceledException` (the client hung up, nobody left to tell) and `BadHttpRequestException` (a malformed body is HTTP-level and keeps its 4xx rather than being dressed up as a started run).
- Pre-stream failures only: after the first event the status and headers are committed, so a later failure can only abort the body — the stream ends without `RUN_FINISHED`, a dropped connection to the client.

The argument for each is in the middleware's XML docs and inline comments; all four are pinned by [`AguiRunErrorMiddlewareTests`](tests/AguiRunErrorMiddlewareTests.cs).

## Constraints

- Mapped events land where the content appears in the stream, so one can fall between `TEXT_MESSAGE_START` and `TEXT_MESSAGE_END`; clients accept that, since only `RUN_FINISHED` requires open text messages to be closed.
- Client content also flows through the rest of the agent pipeline; emit it above `ChatClientAgent`, as the existing middlewares do, to keep it out of chat history.
- A *message* rather than an update still lands in `ChatHistoryProvider.InvokedContext.RequestMessages`: a middleware prepending a `ChatMessage` for the current turn must mark it [`TransientChatMessages.AsTransient()`](backend/TransientChatMessages.cs), or the append-only history store persists it and replays a stale copy on every later turn.
