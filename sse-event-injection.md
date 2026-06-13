# SSE Event Injection

Injects additional AG-UI events into the SSE stream produced by `MapAGUI` without modifying the framework. Uses HTTP pipeline middleware.

## Why

`MapAGUI`, `AGUIServerSentEventsResult`, and `BaseEvent` are all `internal sealed` — no extension points exist. The middleware wraps `HttpContext.Response.Body` before the endpoint runs, intercepts the raw SSE bytes, and emits extra events inline.

## Files

| File | Role |
|---|---|
| `backend/SseEventInjectionMiddleware.cs` | Abstract ASP.NET Core middleware; swaps `Response.Body` with a nested `SseInterceptorStream` (write-only `Stream` that buffers bytes, splits on `\n\n`, and calls the subclass's `Inject` per event), and converts eager downstream exceptions into a `RUN_STARTED` + `RUN_ERROR` pair |
| `backend/ActivitySnapshotInjectionMiddleware.cs` | Concrete subclass registered on `/agui`; its `Inject`/`TryInject` routes each event through the activity-snapshot injectors (MCP apps first, then EU AI Act risk) |
| `backend/McpAppsActivityInjector.cs`, `backend/EUAIActRiskActivityInjector.cs` | The individual injectors that `ActivitySnapshotInjectionMiddleware.TryInject` composes |
| `backend/Program.cs` | Registers `ActivitySnapshotInjectionMiddleware` via `UseWhen` scoped to paths ending in `/agui` |

## How it works

```
Request → UseWhen (path ends with /agui?)
              ↓ yes
         Response.Body swapped with SseInterceptorStream
              ↓
         MapAGUI endpoint writes SSE events
              ↓
         SseInterceptorStream intercepts each "data: {json}\n\n" event
              ↓
         Calls Inject(json): null → suppress · empty → forward unchanged · non-empty → replace
              ↓
         Response.Body restored
```

## Adding an injector

Subclass `SseEventInjectionMiddleware`, override `Inject`, and register the subclass in `Program.cs`:

```csharp
internal sealed class MySnapshotMiddleware(RequestDelegate next)
    : SseEventInjectionMiddleware(next)
{
    protected override IEnumerable<string>? Inject(string eventJson)
    {
        using JsonDocument doc = JsonDocument.Parse(eventJson);
        if (!doc.RootElement.TryGetProperty("type", out JsonElement typeProp))
            return [];                                  // not ours — forward unchanged

        // Replace whichever event type you need:
        if (typeProp.GetString() != "TEXT_MESSAGE_CONTENT") return [];

        string msgId = Guid.NewGuid().ToString("N");
        return [JsonSerializer.Serialize(new { type = "ACTIVITY_SNAPSHOT", messageId = msgId /* ... */ })];
    }
}

// Program.cs
branch => branch.UseMiddleware<MySnapshotMiddleware>()
```

`Inject` receives the raw JSON payload (without the `data:` prefix). Its return value controls what reaches the client:

- `null` — suppress the original event (write nothing).
- empty sequence — forward the original event unchanged.
- non-empty sequence — suppress the original and emit these events instead (include the original in the list to keep it).

To compose several injectors in one middleware, route between them inside `Inject` — see `ActivitySnapshotInjectionMiddleware.TryInject`, which tries the MCP-apps injector first and falls back to the EU AI Act risk injector.

## Constraints

- No AG-UI type safety — `BaseEvent` subclasses are internal; events must be anonymous types or custom records serialized to the correct JSON shape.
- Operates at the raw SSE byte level after the framework has already serialized events.
- All framework features (session store, tool filtering, error recovery) are preserved because `MapAGUI` itself is untouched.
