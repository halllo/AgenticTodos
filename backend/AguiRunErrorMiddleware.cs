using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace AgenticTodos.Backend;

/// <summary>
/// Reports a failed AG-UI request as a protocol-level error — <c>RUN_ERROR</c> — instead of the bare
/// HTTP 500 or the truncated stream the endpoint produces on its own.
/// <para>
/// AG-UI clients only understand the event stream: the .NET client surfaces a non-SSE response as a
/// transport error, and <c>@ag-ui/client</c>'s event verifier is left with a run that never started,
/// so nothing renders. The server SDK does not translate exceptions itself, so the app does it here.
/// </para>
/// <para>
/// Both sides of the response-committed line are covered, because the failure modes are equally
/// invisible either way. <b>Before</b> the first event the status and headers are still open, so the
/// whole response is replaced with <c>RUN_STARTED</c> + <c>RUN_ERROR</c>. <b>After</b> it — a provider
/// rejecting the follow-up model call mid-run is the common case — the response can no longer be
/// reshaped, but it can still be appended to, and one more <c>RUN_ERROR</c> frame is all the protocol
/// needs: the stream then ends on a terminal event instead of a dropped connection.
/// </para>
/// </summary>
internal static class AguiRunErrorMiddleware
{
    /// <summary>
    /// Wraps the AG-UI endpoints under <paramref name="pathPrefix"/>. Registered before the endpoint
    /// mapping; endpoints run at the end of the pipeline, so this catch surrounds their execution.
    /// </summary>
    public static IApplicationBuilder UseAguiRunErrorStream(this IApplicationBuilder app, string pathPrefix) =>
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(pathPrefix),
            branch => branch.Use(async (HttpContext context, RequestDelegate next) =>
            {
                try
                {
                    await next(context);
                }
                // BadHttpRequestException is deliberately excluded: a malformed body is an HTTP-level
                // problem and should keep its 4xx status rather than be dressed up as a started run.
                catch (Exception ex) when (ex is not OperationCanceledException
                                          and not BadHttpRequestException)
                {
                    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(AguiRunErrorMiddleware));

                    var committed = context.Response.HasStarted;

                    logger.LogError(ex, "AG-UI request to {Path} failed {When} the stream started",
                        context.Request.Path, committed ? "after" : "before");

                    // Only an AguiClientException's text is meant for the client. The filter above
                    // admits every other unhandled exception too — a provider credential failure, a DI
                    // resolution error — and those messages describe server internals, so they are
                    // logged (above) rather than streamed.
                    var message = ex is AguiClientException ? ex.Message
                        : committed ? GenericStreamErrorMessage : GenericEagerErrorMessage;

                    if (committed)
                    {
                        await AppendRunErrorAsync(context, logger, message);
                    }
                    else
                    {
                        await WriteRunErrorStreamAsync(context, message);
                    }
                }
            }));

    /// <summary>
    /// Stands in for any exception whose text is not the client's business, raised before the run could
    /// start. The real cause is logged.
    /// </summary>
    private const string GenericEagerErrorMessage = "The agent run could not be started.";

    /// <summary>
    /// The same, for a run that had already started streaming — it did start, so it cannot claim
    /// otherwise, and the distinction is what tells a user their turn was half-executed.
    /// </summary>
    private const string GenericStreamErrorMessage = "The agent run failed.";

    private const string EagerErrorCode = "EagerError";
    private const string StreamErrorCode = "StreamError";

    /// <summary>
    /// Replaces an uncommitted response with a complete two-event error stream.
    /// </summary>
    private static async Task WriteRunErrorStreamAsync(HttpContext context, string message)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        // RUN_STARTED has to come first: a client rejects any event that arrives before a run began,
        // so the error needs a run to belong to. Both ids stay empty: this middleware deliberately does
        // not read the request body (the SDK has already bound and even normalized them by the time
        // resolveAgent throws, but they are not reachable from out here), and clients only use them to
        // correlate — exactly as the protocol's own error path does.
        //
        // Deliberately no `input` echo on RUN_STARTED: @ag-ui/client validates it against a schema that
        // requires `tools` and `context` to be arrays, and a partial echo fails validation — which
        // discards the whole event, so the error message never reaches the user. Verified against the
        // real client: without `input`, RUN_ERROR is delivered and runAgent resolves normally.
        BaseEvent[] events =
        [
            new RunStartedEvent(),
            new RunErrorEvent { Message = message, Code = EagerErrorCode },
        ];

        var payload = new StringBuilder();
        foreach (var @event in events)
        {
            payload.Append("data: ").Append(Serialize(context, @event)).Append("\n\n");
        }

        await context.Response.WriteAsync(payload.ToString(), context.RequestAborted);
    }

    /// <summary>
    /// Appends a terminal <c>RUN_ERROR</c> to a stream that is already committed, so a mid-run failure
    /// ends the run in the protocol rather than by dropping the connection.
    /// </summary>
    private static async Task AppendRunErrorAsync(HttpContext context, ILogger logger, string message)
    {
        try
        {
            // No Response.Clear() and no header assignment — both throw once the response has started,
            // and neither is needed: the response is already a committed text/event-stream that the
            // client is still reading. Only the body is appended to.
            //
            // No second RUN_STARTED either, unlike the pre-stream path: this run already sent one, and
            // both clients reject a RUN_STARTED while a run is active. A bare RUN_ERROR is legal at any
            // point in a stream — in @ag-ui/client and AGUI.Client alike the "still active" guards (open
            // text message, open tool call, open step) are attached to RUN_FINISHED only, and RUN_ERROR
            // is exempt even from the run-already-finished check, being the protocol's abort event.
            var frame = new StringBuilder()
                // A leading separator, in case the SDK threw part-way through writing a frame: it ends
                // the truncated one so this event starts on a boundary rather than being glued onto it.
                // Free in the normal case — a blank line with nothing buffered dispatches nothing, so a
                // whole previous frame is unaffected. It does *not* rescue a genuinely torn frame:
                // @ag-ui/client still dies on that frame's malformed JSON before reaching this event
                // (verified against the real client). That case needs a write to fail mid-frame, which
                // means the connection is already gone and this append is about to fail too.
                .Append("\n\n")
                .Append("data: ")
                .Append(Serialize(context, new RunErrorEvent { Message = message, Code = StreamErrorCode }))
                .Append("\n\n");

            await context.Response.WriteAsync(frame.ToString(), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
        catch (Exception writeFailure)
        {
            // Best effort by construction: the failure that got us here is sometimes the connection
            // itself, and rethrowing would only trade one undeliverable error for another — while also
            // aborting the response, which would discard whatever of this frame did get through.
            logger.LogDebug(writeFailure, "Could not append RUN_ERROR to the committed AG-UI stream");
        }
    }

    /// <summary>
    /// Serializes through the <see cref="BaseEvent"/>-declared type, which picks up
    /// <c>BaseEventJsonConverter</c> — what writes the <c>type</c> discriminator each event is
    /// identified by — using the same options the endpoint itself serializes with: <c>AddAGUIServer()</c>
    /// has already chained the AG-UI source-generated context into them, so this path stays correct if
    /// reflection-based serialization is ever turned off.
    /// </summary>
    private static string Serialize(HttpContext context, BaseEvent @event) =>
        JsonSerializer.Serialize(
            @event,
            context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions);
}

/// <summary>
/// A failure the client caused and whose message is safe to put on the wire — an unknown agent alias,
/// say. <see cref="AguiRunErrorMiddleware"/> forwards this text verbatim in <c>RUN_ERROR</c> and
/// replaces every other exception's message with a generic one.
/// </summary>
internal sealed class AguiClientException(string message) : Exception(message);
