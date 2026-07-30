using System.Text.Json;
using AGUI.Abstractions;
using AgenticTodos.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticTodos.Tests;

/// <summary>
/// The app's error contract with every AG-UI client. An HTTP 500 with a non-SSE body is invisible to
/// them, so a failure raised before the stream started has to arrive as RUN_STARTED + RUN_ERROR.
/// </summary>
public class AguiRunErrorMiddlewareTests
{
    [Fact]
    public async Task EagerFailure_BecomesRunStartedThenRunError()
    {
        // RUN_STARTED first is the load-bearing part: a client rejects any event that arrives before a
        // run began, so without it the error message never reaches the user.
        var (events, context) = await RunAsync(_ => throw new AguiClientException("Unknown agent alias 'nope'."));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers.CacheControl);

        Assert.Equal(2, events.Count);
        Assert.Equal("RUN_STARTED", events[0].GetProperty("type").GetString());
        Assert.Equal("RUN_ERROR", events[1].GetProperty("type").GetString());
        Assert.Equal("EagerError", events[1].GetProperty("code").GetString());
        Assert.Equal("Unknown agent alias 'nope'.", events[1].GetProperty("message").GetString());
    }

    [Fact]
    public async Task RunStarted_CarriesNoInputEcho()
    {
        // @ag-ui/client validates `input` against a schema requiring `tools` and `context` to be
        // arrays; a partial echo fails validation, which discards the whole event.
        var (events, _) = await RunAsync(_ => throw new AguiClientException("boom"));

        Assert.False(events[0].TryGetProperty("input", out _));
    }

    [Fact]
    public async Task UnexpectedFailure_IsReportedGenerically()
    {
        // Only AguiClientException's text is meant for the wire. A provider credential message or a DI
        // resolution failure describes server internals and must not be echoed.
        var (events, _) = await RunAsync(_ => throw new InvalidOperationException("secret-connection-string"));

        var message = events[1].GetProperty("message").GetString();
        Assert.DoesNotContain("secret-connection-string", message);
        Assert.Equal("EagerError", events[1].GetProperty("code").GetString());
    }

    [Fact]
    public async Task MalformedRequest_KeepsItsHttpStatus()
    {
        // A malformed body is an HTTP-level problem; dressing it up as a started run would hide the 4xx.
        await Assert.ThrowsAsync<BadHttpRequestException>(
            () => RunAsync(_ => throw new BadHttpRequestException("bad body")));
    }

    [Fact]
    public async Task Cancellation_IsNotReportedAsARunError()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => RunAsync(_ => throw new OperationCanceledException()));
    }

    [Fact]
    public async Task FailureAfterTheStreamStarted_AppendsARunError()
    {
        // The stream is committed, so it cannot be reshaped — but it can still be appended to, and a
        // terminal RUN_ERROR is the difference between a run that ended in the protocol and one the
        // client only sees as a dropped connection. Provider validation failures on a mid-run model
        // call land here, which is the common case.
        var (events, _) = await RunAsync(StreamThenThrow(new AguiClientException("too late")));

        Assert.Equal("RUN_ERROR", events[^1].GetProperty("type").GetString());
        Assert.Equal("StreamError", events[^1].GetProperty("code").GetString());
        Assert.Equal("too late", events[^1].GetProperty("message").GetString());
    }

    [Fact]
    public async Task FailureAfterTheStreamStarted_SendsNoSecondRunStarted()
    {
        // A bare RUN_ERROR is legal at any point in a stream — the verifiers' "still active" guards are
        // attached to RUN_FINISHED only. A second RUN_STARTED is not: both clients reject one while a
        // run is active, and rejecting it would discard the error with it.
        var (events, _) = await RunAsync(StreamThenThrow(new AguiClientException("too late")));

        Assert.Single(events, e => e.GetProperty("type").GetString() == "RUN_STARTED");
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task FailureAfterTheStreamStarted_KeepsTheEventsAlreadySent()
    {
        // Response.Clear() would throw on a committed response, and the run's real events are the user's
        // turn so far — the error is appended after them, not in place of them.
        var (events, _) = await RunAsync(StreamThenThrow(new InvalidOperationException("boom")));

        Assert.Equal("RUN_STARTED", events[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task FailureAfterTheStreamStarted_IsReportedGenerically()
    {
        // Same policy as the pre-stream path, with a message that does not claim the run never began.
        var (events, _) = await RunAsync(StreamThenThrow(new InvalidOperationException("secret-connection-string")));

        var message = events[^1].GetProperty("message").GetString();
        Assert.DoesNotContain("secret-connection-string", message);
        Assert.Equal("The agent run failed.", message);
    }

    [Fact]
    public async Task FailureAfterTheStreamStarted_SurvivesADeadConnection()
    {
        // The failure that got us here is sometimes the connection itself. A write that throws in turn
        // must not resurface as an exception: there is nobody left to tell, and aborting the response
        // would discard whatever of the frame did get through.
        var (_, context) = await RunAsync(context =>
        {
            context.Features.Get<AguiRunErrorPipeline.StartedResponseFeature>()!.HasStarted = true;
            // HttpResponse.Body reads through IHttpResponseBodyFeature, so that is the one to swap.
            context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new ThrowingStream()));
            throw new InvalidOperationException("boom");
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>
    /// A terminal that gets one event onto the wire — enough to commit the response — and then fails,
    /// the shape of every mid-run failure.
    /// </summary>
    private static RequestDelegate StreamThenThrow(Exception failure) => async context =>
    {
        context.Response.ContentType = "text/event-stream";
        await context.Response.WriteAsync("data: {\"type\":\"RUN_STARTED\",\"threadId\":\"t\",\"runId\":\"r\"}\n\n");
        context.Features.Get<AguiRunErrorPipeline.StartedResponseFeature>()!.HasStarted = true;
        throw failure;
    };

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new IOException("connection reset");
        public override Task FlushAsync(CancellationToken cancellationToken) => throw new IOException("connection reset");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new IOException("connection reset");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            throw new IOException("connection reset");
    }

    [Fact]
    public async Task RequestsOutsideThePrefix_AreNotWrapped()
    {
        await Assert.ThrowsAsync<AguiClientException>(
            () => RunAsync(_ => throw new AguiClientException("boom"), path: "/agents/other/openai/agui"));
    }

    private static Task<(List<JsonElement> Events, HttpContext Context)> RunAsync(
        RequestDelegate terminal,
        string path = AGUIEndpoint.RoutedPathPrefix + "/openai/agui")
        => AguiRunErrorPipeline.RunAsync(terminal, path);
}

/// <summary>
/// The middleware under a minimal request pipeline, shared so <see cref="AguiEndpointWiringTests"/> can
/// ask the same question of the path the endpoint is <i>actually</i> mapped on.
/// </summary>
internal static class AguiRunErrorPipeline
{
    internal static async Task<(List<JsonElement> Events, HttpContext Context)> RunAsync(
        RequestDelegate terminal,
        string path)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddOptions();
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(
            options => AGUIEndpoint.ConfigureAguiJson(options.SerializerOptions));
        var provider = services.BuildServiceProvider();

        var app = new ApplicationBuilder(provider);
        app.UseAguiRunErrorStream(AGUIEndpoint.RoutedPathPrefix);
        app.Run(terminal);

        var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = path;

        // DefaultHttpContext's stock response feature always reports HasStarted = false, however much is
        // written to the body — so the middleware's central precondition would be untestable without a
        // feature that can actually say the response is committed.
        var response = new StartedResponseFeature { Body = body };
        context.Features.Set(response);
        context.Features.Set<IHttpResponseFeature>(response);
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));

        await app.Build()(context);

        return (ParseSse(body), context);
    }

    internal sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; set; }

        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    private static List<JsonElement> ParseSse(MemoryStream body) =>
        [.. System.Text.Encoding.UTF8.GetString(body.ToArray())
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(frame => JsonSerializer.Deserialize<JsonElement>(frame["data: ".Length..]))];
}
