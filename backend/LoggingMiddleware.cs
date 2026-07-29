using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// Debug-level tracing of what actually crosses the boundary to the model provider: the request
/// messages on the way down, and the response — or each streamed update — on the way back.
/// </summary>
internal sealed class LoggingMiddleware(IChatClient inner, ILogger<LoggingMiddleware> logger) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Log("Request messages: {Messages}", messages);
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        Log("Response messages: {Messages}", response.Messages);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Log("Request messages: {Messages}", messages);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            Log("Streaming response update: {Contents}", update.Contents);
            yield return update;
        }
    }

    /// <remarks>
    /// Guarded by <see cref="ILogger.IsEnabled"/> because the serialization is the expensive part, and
    /// a streaming run emits thousands of updates with Debug off — which is the default.
    /// <para>
    /// The <c>catch</c> is what keeps a diagnostic from failing the request it is diagnosing: no
    /// options instance can be complete (see <see cref="LogOptions"/>), and the moment a content type
    /// outside the registered set appears is exactly the moment someone has turned Debug on to
    /// investigate it. Nothing worth propagating can originate in a synchronous serialize call — there
    /// is no cancellation token and no I/O involved — so the line degrades to the content type names,
    /// which is what identifies the offender anyway.
    /// </para>
    /// </remarks>
    private void Log(string template, object value)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        string serialized;
        try
        {
            serialized = JsonSerializer.Serialize(value, LogOptions);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Tracing failed to serialize; falling back to content types.");
            serialized = DescribeTypes(value);
        }

        logger.LogDebug(template, serialized);
    }

    /// <summary>
    /// JSON options for the trace line. <see cref="AIJsonUtilities.DefaultOptions"/> covers the
    /// <b>built-in</b> <see cref="AIContent"/> hierarchy and can never cover more than that: it is
    /// already <c>MakeReadOnly()</c>, and <see cref="AIContent"/> carries a closed
    /// <c>[JsonPolymorphic]</c> set (no <c>UnknownDerivedTypeHandling</c>), so an unregistered subtype
    /// throws <c>NotSupportedException: Runtime type '…' is not supported by polymorphic type
    /// 'AIContent'</c> instead of degrading. So this is a copy — mutable — reusing
    /// <see cref="AGUIEndpoint.ConfigureAguiJson"/> to widen the set.
    /// <para>
    /// Nothing outside the built-in hierarchy is expected here: this app's own content types are
    /// emitted above <c>ChatClientAgent</c> and never travel down, and AG-UI's
    /// <c>InterruptResponseContent</c> — which the server SDK does put on a request message for every
    /// <c>resume</c> entry that is not a tool approval — is dropped by
    /// <see cref="OmitEmptyMessagesMiddleware"/> before the chat-client chain begins. The registrations
    /// are therefore defence in depth; the <c>catch</c> in <see cref="Log"/> is the actual guarantee.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions LogOptions = CreateLogOptions();

    private static JsonSerializerOptions CreateLogOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        AGUIEndpoint.ConfigureAguiJson(options);
        return options;
    }

    /// <summary>
    /// Fallback description of a value the serializer rejected: the roles and content type names, which
    /// is enough to see which content type is unregistered and where it came from.
    /// </summary>
    private static string DescribeTypes(object value) => value switch
    {
        IEnumerable<ChatMessage> messages => string.Join(
            ", ", messages.Select(m => $"{m.Role}:[{Names(m.Contents)}]")),
        IEnumerable<AIContent> contents => $"[{Names(contents)}]",
        _ => value.GetType().Name,
    };

    private static string Names(IEnumerable<AIContent> contents) =>
        string.Join('+', contents.Select(c => c.GetType().Name));
}
