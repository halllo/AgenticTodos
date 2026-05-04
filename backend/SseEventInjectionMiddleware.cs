using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AgenticTodos.Backend;

/// <summary>
/// ASP.NET Core middleware that uses <see cref="SseInterceptorStream"/> to inject
/// additional AG-UI events into the SSE response produced by MapAGUI.
/// Register via UseWhen so it only runs for /agents/* requests.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by ASP.NET Core via UseMiddleware<T>")]
internal sealed class SseEventInjectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        Stream originalBody = context.Response.Body;
        using var interceptor = new SseInterceptorStream(originalBody, InjectAfter);
        context.Response.Body = interceptor;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    // Placeholder: inject a text message sequence immediately after RUN_STARTED.
    // Replace this with the actual events you need to emit.
    private static IEnumerable<string> InjectAfter(string eventJson)
    {
        using JsonDocument doc = JsonDocument.Parse(eventJson);
        if (!doc.RootElement.TryGetProperty("type", out JsonElement typeProp)) yield break;
        if (typeProp.GetString() != "RUN_STARTED") yield break;

        string msgId = Guid.NewGuid().ToString("N");
        yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_START", messageId = msgId, role = "assistant" });
        yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_CONTENT", messageId = msgId, delta = "[injected]" });
        yield return JsonSerializer.Serialize(new { type = "TEXT_MESSAGE_END", messageId = msgId });
    }
}
