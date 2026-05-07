using System.Text;
using System.Text.Json;
using AgenticTodos.Backend;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AgenticTodos.Tests;

public class SseEventInjectionMiddlewareTests
{
    // ---------------------------------------------------------------------------
    // Non-matching events — forwarded unchanged (empty array)
    // ---------------------------------------------------------------------------

    [Fact]
    public void NonDataEvent_RunStarted_ForwardedUnchanged()
    {
        var result = Inject("""{"type":"RUN_STARTED","threadId":"t","runId":"r"}""");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void TextMessageStart_ForwardedUnchanged()
    {
        var result = Inject("""{"type":"TEXT_MESSAGE_START","messageId":"m","role":"assistant"}""");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void TextMessageContent_PlainText_ForwardedUnchanged()
    {
        var result = Inject("""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":"hello"}""");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void TextMessageContent_DeltaIsNumber_ForwardedUnchanged()
    {
        // Regression: bare JSON numbers (e.g. "23" in bold markdown) must not throw.
        var result = Inject("""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":"23"}""");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void TextMessageContent_DeltaIsJsonArray_ForwardedUnchanged()
    {
        var result = Inject("""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":"[1,2,3]"}""");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void TextMessageContent_DeltaIsJsonObjectWithOtherType_ForwardedUnchanged()
    {
        var result = Inject("""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":"{\"type\":\"something-else\"}"}""");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void TextMessageContent_NoDeltaProperty_ForwardedUnchanged()
    {
        var result = Inject("""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m"}""");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    // ---------------------------------------------------------------------------
    // mcp-activity marker — replaced with ACTIVITY_SNAPSHOT
    // ---------------------------------------------------------------------------

    [Fact]
    public void McpActivityMarker_ReplacedWithActivitySnapshot()
    {
        var delta = JsonSerializer.Serialize(new
        {
            type = "mcp-activity",
            messageId = "msg-abc",
            resourceUri = "ui://get-time.html",
            result = new { content = new[] { new { type = "text", text = "12:00" } } },
            toolInput = new { }
        });
        var eventJson = $$"""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":{{JsonSerializer.Serialize(delta)}}}""";

        var result = Inject(eventJson)?.ToList();

        Assert.NotNull(result);
        Assert.Single(result!);

        using var doc = JsonDocument.Parse(result![0]);
        var root = doc.RootElement;
        Assert.Equal("ACTIVITY_SNAPSHOT", root.GetProperty("type").GetString());
        Assert.Equal("msg-abc", root.GetProperty("messageId").GetString());
        Assert.Equal("mcp-apps", root.GetProperty("activityType").GetString());
        Assert.True(root.GetProperty("replace").GetBoolean());

        var content = root.GetProperty("content");
        Assert.Equal("ui://get-time.html", content.GetProperty("resourceUri").GetString());
        Assert.Equal("text", content.GetProperty("result").GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void McpActivityMarker_MissingResourceUri_ForwardedUnchanged()
    {
        var delta = JsonSerializer.Serialize(new
        {
            type = "mcp-activity",
            messageId = "msg-abc",
            // no resourceUri
            result = new { content = Array.Empty<object>() },
            toolInput = new { }
        });
        var eventJson = $$"""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":{{JsonSerializer.Serialize(delta)}}}""";

        var result = Inject(eventJson)?.ToList();

        // resourceUri is required — treat as non-activity and forward unchanged
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void McpActivityMarker_InnerMissingMessageId_FallsBackToOuterMessageId()
    {
        var delta = JsonSerializer.Serialize(new
        {
            type = "mcp-activity",
            // no inner messageId — should fall back to outer TEXT_MESSAGE_CONTENT messageId
            resourceUri = "ui://get-time.html",
            result = new { content = Array.Empty<object>() },
            toolInput = new { }
        });
        var eventJson = $$"""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":{{JsonSerializer.Serialize(delta)}}}""";

        var result = Inject(eventJson)?.ToList();

        Assert.NotNull(result);
        Assert.Single(result!);
        using var doc = JsonDocument.Parse(result![0]);
        Assert.Equal("m", doc.RootElement.GetProperty("messageId").GetString());
    }

    [Fact]
    public void McpActivityMarker_MissingResult_DefaultsToEmptyContent()
    {
        var delta = JsonSerializer.Serialize(new
        {
            type = "mcp-activity",
            messageId = "msg-1",
            resourceUri = "ui://x.html",
            // no result
            toolInput = new { }
        });
        var eventJson = $$"""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":{{JsonSerializer.Serialize(delta)}}}""";

        var result = Inject(eventJson)?.ToList();

        Assert.NotNull(result);
        Assert.Single(result!);
        using var doc = JsonDocument.Parse(result![0]);
        var resultProp = doc.RootElement.GetProperty("content").GetProperty("result");
        Assert.Equal("""{"content":[]}""", resultProp.GetRawText());
    }

    [Fact]
    public void McpActivityMarker_MissingToolInput_DefaultsToEmptyObject()
    {
        var delta = JsonSerializer.Serialize(new
        {
            type = "mcp-activity",
            messageId = "msg-1",
            resourceUri = "ui://x.html",
            result = new { content = Array.Empty<object>() },
            // no toolInput
        });
        var eventJson = $$"""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":{{JsonSerializer.Serialize(delta)}}}""";

        var result = Inject(eventJson)?.ToList();

        Assert.NotNull(result);
        Assert.Single(result!);
        using var doc = JsonDocument.Parse(result![0]);
        var toolInput = doc.RootElement.GetProperty("content").GetProperty("toolInput");
        Assert.Equal("{}", toolInput.GetRawText());
    }

    // ---------------------------------------------------------------------------
    // Helper
    // ---------------------------------------------------------------------------

    private static IEnumerable<string>? Inject(string eventJson) =>
        McpAppsActivityInjector.TryInjectActivitySnapshot(eventJson);
}

public class SseEventInjectionMiddlewareInvokeTests
{
    // Injector that forwards every event unchanged (returns empty sequence).
    private static readonly Func<string, IEnumerable<string>?> s_passThrough = _ => [];

    // ---------------------------------------------------------------------------
    // Eager exception handling
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task EagerException_EmitsRunStartedThenRunError()
    {
        var (middleware, context, responseBody) = Build(
            next: _ => throw new InvalidOperationException("session store failed"));

        await middleware.InvokeAsync(context);

        var events = ParseSseEvents(responseBody);
        Assert.Equal(2, events.Count);

        Assert.Equal("RUN_STARTED", events[0].GetProperty("type").GetString());
        Assert.Equal("", events[0].GetProperty("threadId").GetString());
        Assert.Equal("", events[0].GetProperty("runId").GetString());

        Assert.Equal("RUN_ERROR", events[1].GetProperty("type").GetString());
        Assert.Equal("EagerError", events[1].GetProperty("code").GetString());
        Assert.Equal("session store failed", events[1].GetProperty("message").GetString());
    }

    [Fact]
    public async Task EagerException_ResponseIs200WithTextEventStream()
    {
        var (middleware, context, _) = Build(
            next: _ => throw new InvalidOperationException("oops"));

        await middleware.InvokeAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/event-stream", context.Response.ContentType);
    }

    [Fact]
    public async Task EagerException_ResponseBodyRestoredAfterError()
    {
        var (middleware, context, _) = Build(
            next: _ => throw new InvalidOperationException("oops"));
        var originalBody = context.Response.Body;

        await middleware.InvokeAsync(context);

        Assert.Same(originalBody, context.Response.Body);
    }

    [Fact]
    public async Task OperationCanceledException_Propagates()
    {
        var (middleware, context, _) = Build(
            next: _ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task ExceptionWhenResponseAlreadyStarted_Propagates()
    {
        var (middleware, context, _) = Build(
            next: _ => throw new InvalidOperationException("too late"));

        // Signal that the response has already started so the catch filter returns false.
        context.Features.Set<IHttpResponseFeature>(new ResponseStartedFeature());

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
    }

    // ---------------------------------------------------------------------------
    // Normal path
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NormalPath_EventsForwardedUnchanged()
    {
        var (middleware, context, responseBody) = Build(
            next: async ctx =>
            {
                var bytes = Encoding.UTF8.GetBytes(
                    "data: {\"type\":\"RUN_STARTED\"}\n\ndata: {\"type\":\"RUN_FINISHED\"}\n\n");
                await ctx.Response.Body.WriteAsync(bytes);
            });

        await middleware.InvokeAsync(context);

        var events = ParseSseEvents(responseBody);
        Assert.Equal(2, events.Count);
        Assert.Equal("RUN_STARTED", events[0].GetProperty("type").GetString());
        Assert.Equal("RUN_FINISHED", events[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task NormalPath_ResponseBodyRestoredAfterSuccess()
    {
        var (middleware, context, _) = Build(next: _ => Task.CompletedTask);
        var originalBody = context.Response.Body;

        await middleware.InvokeAsync(context);

        Assert.Same(originalBody, context.Response.Body);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static (SseEventInjectionMiddleware middleware, DefaultHttpContext context, MemoryStream responseBody)
        Build(RequestDelegate next, Func<string, IEnumerable<string>?>? injector = null)
    {
        var responseBody = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = responseBody;
        var middleware = new SseEventInjectionMiddleware(next, injector ?? s_passThrough);
        return (middleware, context, responseBody);
    }

    private static List<JsonElement> ParseSseEvents(MemoryStream stream)
    {
        var text = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        return text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(e => e.StartsWith("data: ", StringComparison.Ordinal))
            .Select(e => JsonDocument.Parse(e["data: ".Length..]).RootElement)
            .ToList();
    }

    private sealed class ResponseStartedFeature : IHttpResponseFeature
    {
        public bool HasStarted => true;
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}
