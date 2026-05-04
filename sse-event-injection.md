# SSE Event Injection

Injects additional AG-UI events into the SSE stream produced by `MapAGUI` without modifying the framework. Uses HTTP pipeline middleware.

## Why

`MapAGUI`, `AGUIServerSentEventsResult`, and `BaseEvent` are all `internal sealed` — no extension points exist. The middleware wraps `HttpContext.Response.Body` before the endpoint runs, intercepts the raw SSE bytes, and emits extra events inline.

## Files

| File | Role |
|---|---|
| `backend/SseInterceptorStream.cs` | Write-only `Stream` wrapper; buffers bytes, splits on `\n\n`, forwards original events, calls injector |
| `backend/SseEventInjectionMiddleware.cs` | ASP.NET Core middleware; swaps `Response.Body`, holds the `InjectAfter` callback |
| `backend/Program.cs` | Registers the middleware via `UseWhen` scoped to paths ending in `/agui` |

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
         Forwards original event → calls InjectAfter(json) → writes injected events
              ↓
         Response.Body restored
```

## Customizing the injector

Edit `InjectAfter` in `backend/SseEventInjectionMiddleware.cs`:

```csharp
private static IEnumerable<string> InjectAfter(string eventJson)
{
    using JsonDocument doc = JsonDocument.Parse(eventJson);
    if (!doc.RootElement.TryGetProperty("type", out JsonElement typeProp)) yield break;

    // Inject after whichever event type you need:
    if (typeProp.GetString() != "RUN_STARTED") yield break;

    string msgId = Guid.NewGuid().ToString("N");
    yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_START", messageId = msgId, role = "assistant" });
    yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_CONTENT", messageId = msgId, delta = "..." });
    yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_END", messageId = msgId });
}
```

Return zero strings to suppress injection for a given event. The injector receives the raw JSON payload (without the `data:` prefix) and returns JSON strings that are written as complete SSE events.

## Constraints

- No AG-UI type safety — `BaseEvent` subclasses are internal; events must be anonymous types or custom records serialized to the correct JSON shape.
- Operates at the raw SSE byte level after the framework has already serialized events.
- All framework features (session store, tool filtering, error recovery) are preserved because `MapAGUI` itself is untouched.
