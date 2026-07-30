using System.Net;
using System.Text;
using AGUI.Client;

namespace AgenticTodos.Tests;

/// <summary>
/// The assumption <see cref="AguiRunErrorMiddleware"/>'s committed-stream path rests on: a bare
/// <c>RUN_ERROR</c>, appended to a stream that is already mid-run, reaches the client — in whatever
/// position the failure happened to interrupt.
/// <para>
/// Pinned here because it is a claim about someone else's library, and the whole point of appending
/// the frame is that a client acts on it: if <c>AGUI.Client</c> ever rejected one of these positions
/// the CLI would be back to seeing a dropped connection, silently. Scripted streams against a stub
/// handler, so this costs nothing and needs no server. The browser side of the same contract is
/// covered by <c>@ag-ui/client</c>'s own verifier, whose "still active" guards are attached to
/// <c>RUN_FINISHED</c> only.
/// </para>
/// </summary>
public class AguiRunErrorClientContractTests
{
    private const string RunStarted = """data: {"type":"RUN_STARTED","threadId":"t","runId":"r"}""" + "\n\n";

    /// <summary>What the middleware appends: a separator, then one RUN_ERROR frame.</summary>
    private const string AppendedError =
        "\n\n" + """data: {"type":"RUN_ERROR","message":"The agent run failed.","code":"StreamError"}""" + "\n\n";

    public static TheoryData<string, string> Positions => new()
    {
        {
            "a text message is open",
            RunStarted
            + """data: {"type":"TEXT_MESSAGE_START","messageId":"m","role":"assistant"}""" + "\n\n"
            + """data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":"half a sen"}""" + "\n\n"
        },
        {
            "a tool call is open",
            RunStarted
            + """data: {"type":"TOOL_CALL_START","toolCallId":"c","toolCallName":"increment_counter"}""" + "\n\n"
            + """data: {"type":"TOOL_CALL_ARGS","toolCallId":"c","delta":"{\"a"}""" + "\n\n"
        },
        {
            // The shape of the failure this path was written for: a provider rejecting the follow-up
            // model call once the turn's tool results are already on the wire.
            "tool results are already streamed",
            RunStarted
            + """data: {"type":"TOOL_CALL_START","toolCallId":"c","toolCallName":"increment_counter"}""" + "\n\n"
            + """data: {"type":"TOOL_CALL_END","toolCallId":"c"}""" + "\n\n"
            + """data: {"type":"TOOL_CALL_RESULT","messageId":"c","toolCallId":"c","content":"1"}""" + "\n\n"
        },
        {
            // Reachable through a failure in session saving, which happens after the run's own
            // terminal event has gone out.
            "the run already finished",
            RunStarted + """data: {"type":"RUN_FINISHED","threadId":"t","runId":"r"}""" + "\n\n"
        },
    };

    [Theory]
    [MemberData(nameof(Positions))]
    public async Task AppendedRunError_ReachesTheClient(string position, string stream)
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => DrainAsync(stream + AppendedError));

        Assert.Contains("The agent run failed.", Flatten(failure));
        Assert.False(string.IsNullOrEmpty(position));
    }

    [Fact]
    public async Task WithoutTheAppendedError_TheStreamJustStops()
    {
        // The behaviour being fixed, kept as the contrast: a run that ends without a terminal event
        // is not an error to the client, it is a run that quietly stops — nothing to report, nothing
        // to retry, and in the browser nothing to clear the pending interrupts either.
        var updates = await DrainAsync(
            RunStarted + """data: {"type":"TEXT_MESSAGE_START","messageId":"m","role":"assistant"}""" + "\n\n");

        Assert.NotNull(updates);
    }

    private static async Task<List<Microsoft.Extensions.AI.ChatResponseUpdate>> DrainAsync(string sse)
    {
        using var httpClient = new HttpClient(new ScriptedSseHandler(sse));
        var client = new AGUIChatClient(new AGUIChatClientOptions(httpClient, "http://localhost/agui"));

        List<Microsoft.Extensions.AI.ChatResponseUpdate> updates = [];
        await foreach (var update in client.GetStreamingResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hi")]))
        {
            updates.Add(update);
        }

        return updates;
    }

    /// <summary>Flattens an exception chain — the client may wrap the RUN_ERROR before rethrowing.</summary>
    private static string Flatten(Exception? exception)
    {
        var text = new StringBuilder();
        for (; exception is not null; exception = exception.InnerException)
        {
            text.Append(exception.Message).Append(' ');
        }

        return text.ToString();
    }

    private sealed class ScriptedSseHandler(string sse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent(sse, Encoding.UTF8);
            content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
