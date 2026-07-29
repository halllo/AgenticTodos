using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace AgenticTodos.Backend;

/// <summary>
/// Reports a failed AG-UI request as a protocol-level error stream — <c>RUN_STARTED</c> followed by
/// <c>RUN_ERROR</c> over SSE — instead of the bare HTTP 500 the endpoint produces on its own.
/// <para>
/// AG-UI clients only understand the event stream: the .NET client surfaces a non-SSE response as a
/// transport error, and <c>@ag-ui/client</c>'s event verifier is left with a run that never started,
/// so nothing renders. The server SDK does not translate exceptions itself, so the app does it here.
/// </para>
/// <para>
/// Only a failure raised <b>before the response started</b> can be reported this way. Once the SDK
/// has written its first event the status and headers are committed and a later failure can only
/// abort the body — the stream then ends without <c>RUN_FINISHED</c>, which is what an AG-UI client
/// sees as a dropped connection.
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
                                          and not BadHttpRequestException
                                          && !context.Response.HasStarted)
                {
                    context.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(typeof(AguiRunErrorMiddleware))
                        .LogError(ex, "AG-UI request to {Path} failed before the stream started", context.Request.Path);

                    // Only an AguiClientException's text is meant for the client. The filter above
                    // admits every other unhandled exception too — a provider credential failure, a DI
                    // resolution error — and those messages describe server internals, so they are
                    // logged (above) rather than streamed.
                    await WriteRunErrorAsync(
                        context,
                        code: "EagerError",
                        message: ex is AguiClientException ? ex.Message : GenericErrorMessage);
                }
            }));

    /// <summary>
    /// Stands in for any exception whose text is not the client's business. The real cause is logged.
    /// </summary>
    private const string GenericErrorMessage = "The agent run could not be started.";

    private static async Task WriteRunErrorAsync(HttpContext context, string code, string message)
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
            new RunErrorEvent { Message = message, Code = code },
        ];

        // The same options the endpoint itself serializes with: AddAGUIServer() has already chained the
        // AG-UI source-generated context into them, so this path stays correct if reflection-based
        // serialization is ever turned off.
        var jsonOptions = context.RequestServices
            .GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        var payload = new StringBuilder();
        foreach (var @event in events)
        {
            // Serializing through the BaseEvent-declared type picks up BaseEventJsonConverter, which
            // is what writes the `type` discriminator each event is identified by.
            payload.Append("data: ").Append(JsonSerializer.Serialize(@event, jsonOptions)).Append("\n\n");
        }

        await context.Response.WriteAsync(payload.ToString(), context.RequestAborted);
    }
}

/// <summary>
/// A failure the client caused and whose message is safe to put on the wire — an unknown agent alias,
/// say. <see cref="AguiRunErrorMiddleware"/> forwards this text verbatim in <c>RUN_ERROR</c> and
/// replaces every other exception's message with a generic one.
/// </summary>
internal sealed class AguiClientException(string message) : Exception(message);
