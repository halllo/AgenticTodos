using System.Runtime.CompilerServices;
using AGUI.Server;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;

namespace AgenticTodos.Backend;

/// <summary>
/// Splits a model turn that calls server and client (WebMCP) tools in parallel into two turns.
/// <para>
/// Why: <c>FunctionInvokingChatClient</c> runs <i>none</i> of a turn's calls once one of them is a tool it cannot
/// invoke — a bare declaration, which is how the AG-UI SDK installs a client tool. The client answers only its own
/// call, the server call stays unanswered in the stored history, and Bedrock rejects every later request
/// (<i>"Expected toolResult blocks at messages.N.content for the following Ids: …"</i>) — the thread is unusable from
/// then on.
/// </para>
/// <para>
/// How, sitting below the function-invocation loop:
/// <list type="number">
/// <item>Model call 1 returns calls the loop can run and calls to bare declarations. The latter are held back; the
/// loop sees only the former and runs them (an unknown tool is answered with the loop's "function not found" error,
/// an approval-gated one prompts first).</item>
/// <item>The loop calls again for the next round-trip. Instead of the model, it gets the held-back calls, as if the
/// model had made them in a turn of their own. It cannot run them, so the run ends waiting on the client.</item>
/// <item>The client's follow-up run answers them, and the call goes to the model.</item>
/// </list>
/// </para>
/// <para>
/// From step 3 on, every request that goes to the model has the split turn put back together (see
/// <see cref="Consolidated"/>): one assistant message with all of the turn's calls, in the order the model made them,
/// followed by one message with all of their results. Seen as two rounds, Claude on Bedrock took its one parallel step
/// for a failed attempt when the prompt had demanded a single step, and called both tools again. The rewrite happens
/// in transit only — this client sits below the function-invocation loop, so the stored history keeps the two rounds.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// Only calls to bare declarations are held back, because only those stop the loop; everything it can invoke stays
/// where the model put it. A turn that also calls a client tool the SDK installed as an approval-required proxy —
/// which happens only for clients that send the full history — is not split at all (see <see cref="ShouldSplit"/>);
/// the loop and the approval pipeline handle it (the sibling escalation in human-in-the-loop.md).
/// </para>
/// <para>
/// Held calls are released once the calls they were split from are answered — whether those ran, failed or were
/// rejected: the held calls are requests in their own right. They are dropped instead when the thread has moved on
/// without that, e.g. the user ignored or cancelled an approval card and sent a new message.
/// </para>
/// <para>
/// State lives in memory on this instance, which belongs to the agent's chat client and is therefore shared by all
/// of that agent's runs. Keys combine the AG-UI thread id with the call ids, so threads cannot see each other's
/// entries even if a provider reused call ids, and held calls are claimed atomically, so two concurrent requests for
/// the same thread cannot both release them. Steps 1 and 2 normally happen in the same run; when a call awaits
/// approval in between, the held calls wait for the resumed run. Entries expire after the retention period
/// (<see cref="DefaultRetention"/> unless configured) without use: a held call whose run never continues is dropped
/// then, and a split turn older than that reaches the model as two rounds again — as it does after a restart. Bedrock
/// accepts that history too (observed with extended thinking on); only the merge is lost.
/// </para>
/// </remarks>
internal sealed class ClientToolDeferralMiddleware : DelegatingChatClient
{
    /// <summary>How long held calls and the record of a split turn are kept without being used.</summary>
    internal static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);

    private readonly TimeSpan retention;

    /// <summary>Held-back calls (<see cref="Deferral"/>), keyed by thread and the ids of the calls they were split from.</summary>
    private readonly MemoryCache deferred;

    /// <summary>
    /// The split turns <see cref="Consolidated"/> puts back together: all of a turn's call ids in the model's order,
    /// keyed by thread and the ids of its released calls.
    /// </summary>
    private readonly MemoryCache released;

    /// <summary>Makes claiming a deferral (release or drop) atomic.</summary>
    private readonly Lock claim = new();

    public ClientToolDeferralMiddleware(IChatClient innerClient, TimeSpan? retention = null, TimeProvider? timeProvider = null)
        : base(innerClient)
    {
        this.retention = retention ?? DefaultRetention;
        var cacheOptions = new MemoryCacheOptions { Clock = new TimeProviderClock(timeProvider ?? TimeProvider.System) };
        deferred = new MemoryCache(cacheOptions);
        released = new MemoryCache(cacheOptions);
    }

    /// <summary>Step 2 if calls are held for this history; otherwise calls the model and splits a mixed turn (step 1).</summary>
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var thread = ThreadOf(options);
        if (TakeDeferred(history, thread) is { } held)
        {
            return new ChatResponse(held) { FinishReason = ChatFinishReason.ToolCalls };
        }

        var response = await base.GetResponseAsync(Consolidated(history, thread), options, cancellationToken);
        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        var bareCalls = calls.Where(call => IsBareDeclaration(call, options)).ToList();
        if (!ShouldSplit(calls, bareCalls, options))
        {
            return response;
        }

        Defer(thread, calls.Except(bareCalls), bareCalls, calls);
        response.Messages =
        [
            .. response.Messages
                .Select(message => WithoutCalls(message, bareCalls))
                .Where(message => message.Contents.Count > 0),
        ];
        return response;
    }

    /// <summary>
    /// Streaming <see cref="GetResponseAsync"/>. Calls to bare declarations are held until the stream ends, since a
    /// call the loop can run may still follow; if none does, they go out at the end, in one update of their own.
    /// </summary>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var thread = ThreadOf(options);
        if (TakeDeferred(history, thread) is { } held)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, held.Contents)
            {
                MessageId = held.MessageId,
                FinishReason = ChatFinishReason.ToolCalls,
            };
            yield break;
        }

        List<FunctionCallContent> allCalls = [];
        List<FunctionCallContent> heldCalls = [];
        ChatResponseUpdate? heldFrom = null;

        await foreach (var update in base.GetStreamingResponseAsync(Consolidated(history, thread), options, cancellationToken))
        {
            var calls = update.Contents.OfType<FunctionCallContent>().ToList();
            allCalls.AddRange(calls);
            var bareCalls = calls.Where(call => IsBareDeclaration(call, options)).ToList();
            if (bareCalls.Count == 0)
            {
                yield return update;
                continue;
            }

            heldCalls.AddRange(bareCalls);
            heldFrom ??= update;
            // A copy: the original may be the object a caller above builds the next request from.
            var trimmed = update.Clone();
            trimmed.Contents = [.. update.Contents.Except(bareCalls)];
            trimmed.RawRepresentation = null;
            if (trimmed.Contents.Count > 0 || trimmed.FinishReason is not null)
            {
                yield return trimmed;
            }
        }

        if (heldCalls.Count == 0)
        {
            yield break;
        }

        if (ShouldSplit(allCalls, heldCalls, options))
        {
            Defer(thread, allCalls.Except(heldCalls), heldCalls, allCalls);
            yield break;
        }

        // Nothing to split — only bare declarations in this turn, or a client-tool proxy among the others (see
        // ShouldSplit): release them.
        yield return new ChatResponseUpdate(ChatRole.Assistant, [.. heldCalls])
        {
            MessageId = heldFrom!.MessageId,
            ResponseId = heldFrom.ResponseId,
            ModelId = heldFrom.ModelId,
            CreatedAt = heldFrom.CreatedAt,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            deferred.Dispose();
            released.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Held-back calls, and the ids of all of the turn's calls in the order the model made them.</summary>
    private sealed record Deferral(ChatMessage Held, string[] CallOrder);

    /// <summary>Step 1: holds the bare-declaration calls until the turn's other calls are answered.</summary>
    private void Defer(string thread, IEnumerable<FunctionCallContent> runCalls, IEnumerable<FunctionCallContent> heldCalls, IEnumerable<FunctionCallContent> allCalls)
    {
        // A message id of its own: AG-UI uses it as the parent of the held calls' TOOL_CALL events.
        var held = new ChatMessage(ChatRole.Assistant, [.. heldCalls]) { MessageId = Guid.NewGuid().ToString("N") };
        deferred.Set(
            Key(thread, runCalls.Select(call => call.CallId)),
            new Deferral(held, [.. allCalls.Select(call => call.CallId)]),
            new MemoryCacheEntryOptions { SlidingExpiration = retention });
    }

    /// <summary>
    /// Step 2: removes and returns the held calls when the history ends with exactly the calls they were split from
    /// and those calls' results. Drops them when the thread has moved on without that. <see langword="null"/>
    /// otherwise, e.g. while a call awaits approval, or in step 3.
    /// </summary>
    private ChatMessage? TakeDeferred(IReadOnlyList<ChatMessage> history, string thread)
    {
        if (deferred.Count == 0 || LatestAssistantTurn(history, history.Count - 1) is not var (first, last))
        {
            return null;
        }

        var callIds = CallIds(history, first, last);
        var key = Key(thread, callIds);
        var after = history.Skip(last + 1).ToList();
        lock (claim)
        {
            if (!deferred.TryGetValue(key, out Deferral? deferral) || deferral is null)
            {
                return null;
            }

            if (after.Any(message => message.Role != ChatRole.Tool))
            {
                // The thread moved on without answering them: releasing them now would run a stale request and leave
                // the new message unanswered.
                deferred.Remove(key);
                return null;
            }

            if (!AnswersExactly(after, [.. callIds]))
            {
                return null;
            }

            deferred.Remove(key);
            var heldCallIds = deferral.Held.Contents.OfType<FunctionCallContent>().Select(call => call.CallId);
            released.Set(Key(thread, heldCallIds), deferral.CallOrder, new MemoryCacheEntryOptions { SlidingExpiration = retention });
            return deferral.Held;
        }
    }

    /// <summary>
    /// The history to send to the model: <paramref name="history"/> itself, or a copy in which each split turn whose
    /// calls are all answered is one assistant message and one message of results again.
    /// </summary>
    /// <remarks>
    /// Only the exact shape steps 1 to 3 produce is rewritten: the message(s) with the calls the loop ran, messages
    /// holding only their results, the released message holding only the held calls, then their results. Anything
    /// else in between — or a split whose held calls are not all answered yet — leaves the history as it is.
    /// </remarks>
    private IReadOnlyList<ChatMessage> Consolidated(IReadOnlyList<ChatMessage> history, string thread)
    {
        if (released.Count == 0)
        {
            return history;
        }

        List<ChatMessage>? rewritten = null;
        for (var i = 0; i < history.Count; i++)
        {
            var calls = history[i].Role == ChatRole.Assistant
                ? history[i].Contents.OfType<FunctionCallContent>().ToList()
                : [];
            if (calls.Count == 0 || calls.Count != history[i].Contents.Count
                || !released.TryGetValue(Key(thread, calls.Select(call => call.CallId)), out string[]? callOrder)
                || callOrder is null
                || Merge(history, i, callOrder) is not var (first, last, merged))
            {
                continue;
            }

            // Merges never overlap: the message after a merged range is not a tool message, so it cannot be part of
            // the next one. Each merge shrinks the list, shifting every later index by the same amount.
            rewritten ??= [.. history];
            var offset = rewritten.Count - history.Count;
            rewritten.RemoveRange(first + offset, last - first + 1);
            rewritten.InsertRange(first + offset, merged);
        }

        return rewritten ?? history;
    }

    /// <summary>
    /// The split turn whose released message is at <paramref name="releasedMessage"/>, as the range of messages it
    /// spans and the two messages that replace them; <see langword="null"/> unless it has exactly the expected shape.
    /// </summary>
    private static (int First, int Last, ChatMessage[] Merged)? Merge(IReadOnlyList<ChatMessage> history, int releasedMessage, string[] callOrder)
    {
        var heldCalls = history[releasedMessage].Contents.OfType<FunctionCallContent>().ToList();
        HashSet<string> runIds = [.. callOrder.Except(heldCalls.Select(call => call.CallId))];

        // Before it: the results of the calls the loop ran, and before those the assistant message(s) with the calls —
        // more than one when approvals answered one at a time made the loop rebuild them separately.
        var resultsStart = releasedMessage;
        while (resultsStart > 0 && history[resultsStart - 1].Role == ChatRole.Tool)
        {
            resultsStart--;
        }

        if (resultsStart == releasedMessage
            || LatestAssistantTurn(history, resultsStart - 1) is not var (first, last)
            || last != resultsStart - 1
            || !CallIds(history, first, last).ToHashSet().SetEquals(runIds))
        {
            return null;
        }

        var runResults = history.Skip(resultsStart).Take(releasedMessage - resultsStart).ToList();
        if (!AnswersExactly(runResults, runIds))
        {
            return null;
        }

        // After it: the results of the held calls.
        var end = releasedMessage;
        while (end + 1 < history.Count && history[end + 1].Role == ChatRole.Tool)
        {
            end++;
        }

        var heldResults = history.Skip(releasedMessage + 1).Take(end - releasedMessage).ToList();
        if (!AnswersExactly(heldResults, [.. heldCalls.Select(call => call.CallId)]))
        {
            return null;
        }

        int Position(string callId) => Array.IndexOf(callOrder, callId);
        var assistants = history.Skip(first).Take(last - first + 1).ToList();

        var assistant = WithContents(history[first],
        [
            .. assistants.SelectMany(message => message.Contents).Where(content => content is not FunctionCallContent),
            .. assistants.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Concat(heldCalls)
                .OrderBy(call => Position(call.CallId)),
        ]);
        var results = WithContents(runResults[0],
        [
            .. runResults.Concat(heldResults).SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().OrderBy(result => Position(result.CallId)),
        ]);

        return (first, end, [assistant, results]);
    }

    /// <summary>
    /// The run of assistant messages ending at the last assistant message at or before <paramref name="from"/> — one
    /// response can span several. <see langword="null"/> when there is none.
    /// </summary>
    private static (int First, int Last)? LatestAssistantTurn(IReadOnlyList<ChatMessage> history, int from)
    {
        var last = from;
        while (last >= 0 && history[last].Role != ChatRole.Assistant)
        {
            last--;
        }

        if (last < 0)
        {
            return null;
        }

        var first = last;
        while (first > 0 && history[first - 1].Role == ChatRole.Assistant)
        {
            first--;
        }

        return (first, last);
    }

    private static List<string> CallIds(IReadOnlyList<ChatMessage> history, int first, int last) =>
        [.. history.Skip(first).Take(last - first + 1).SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(call => call.CallId)];

    /// <summary>
    /// Whether <paramref name="messages"/> hold exactly one result for each of <paramref name="callIds"/>, and nothing
    /// else. Empty <paramref name="messages"/> answer nothing, so they never match a non-empty set.
    /// </summary>
    private static bool AnswersExactly(List<ChatMessage> messages, HashSet<string> callIds)
    {
        var contents = messages.SelectMany(message => message.Contents).ToList();
        return contents.Count == callIds.Count
            && contents.OfType<FunctionResultContent>().Select(result => result.CallId).ToHashSet().SetEquals(callIds);
    }

    /// <summary>
    /// Whether to hold <paramref name="bareCalls"/> back: they are some but not all of the turn's
    /// <paramref name="calls"/>, and none of the others is a client tool the SDK installed as an approval proxy.
    /// </summary>
    /// <remarks>
    /// The SDK installs a client tool as an invocable, approval-required proxy when the run's own input carries an
    /// earlier call to it and that call's result — i.e. for clients that send the full history. The loop escalates a
    /// call to it into an approval request that reaches the client as a tool call; the history normalizer then
    /// removes that request once the client has answered, so no assistant message ever carries the call, and the
    /// held calls — keyed by it — would never be released. Such a turn is left to the loop, which escalates every one
    /// of its calls, the bare ones included (the sibling escalation in human-in-the-loop.md). Every call then reaches
    /// its executor, though that path has its own gaps in what the model is told afterwards (human-in-the-loop.md,
    /// Constraints) — better than a client call silently never sent.
    /// </remarks>
    private static bool ShouldSplit(List<FunctionCallContent> calls, List<FunctionCallContent> bareCalls, ChatOptions? options) =>
        bareCalls.Count > 0
        && bareCalls.Count < calls.Count
        && !calls.Except(bareCalls).Any(call => IsClientToolProxy(call, options));

    /// <summary>A call to a tool the AG-UI request declares as a client tool, but which the loop can invoke: a proxy.</summary>
    private static bool IsClientToolProxy(FunctionCallContent call, ChatOptions? options) =>
        options is not null && options.TryGetRunAgentInput(out var input)
        && input.Tools?.Any(tool => tool.Name == call.Name) == true
        && !IsBareDeclaration(call, options);

    /// <summary>
    /// Whether the loop would stop on this call rather than run it: the tool it finds for the name — the first
    /// declaration with that name — has no implementation.
    /// </summary>
    private static bool IsBareDeclaration(FunctionCallContent call, ChatOptions? options) =>
        options?.Tools?.OfType<AIFunctionDeclaration>().FirstOrDefault(tool => tool.Name == call.Name) is { } tool
        && tool is not AIFunction;

    /// <summary>The AG-UI thread the request belongs to; empty outside an AG-UI request.</summary>
    private static string ThreadOf(ChatOptions? options) =>
        options is not null && options.TryGetRunAgentInput(out var input) ? input.ThreadId ?? "" : "";

    /// <summary>Step 1: the message minus the held-back calls, copied so the original stays intact.</summary>
    private static ChatMessage WithoutCalls(ChatMessage message, IReadOnlyCollection<FunctionCallContent> calls) =>
        message.Contents.Any(calls.Contains) ? WithContents(message, [.. message.Contents.Except(calls)]) : message;

    /// <summary>
    /// A copy of <paramref name="message"/> with other contents. Its <see cref="ChatMessage.RawRepresentation"/> is
    /// dropped: it is the provider's form of the original contents, and an adapter sending it as-is would send those.
    /// </summary>
    private static ChatMessage WithContents(ChatMessage message, IList<AIContent> contents)
    {
        var copy = message.Clone();
        copy.Contents = contents;
        copy.RawRepresentation = null;
        return copy;
    }

    /// <summary>Key for a set of call ids within a thread, independent of their order.</summary>
    private static string Key(string thread, IEnumerable<string> callIds) =>
        thread + "\n\n" + string.Join('\n', callIds.Order(StringComparer.Ordinal));

    /// <summary>The cache clock, from a <see cref="TimeProvider"/> so tests can drive expiry.</summary>
    private sealed class TimeProviderClock(TimeProvider timeProvider) : ISystemClock
    {
        public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    }
}

internal static class ClientToolDeferralMiddlewareExtensions
{
    extension(ChatClientBuilder builder)
    {
        /// <summary>See <see cref="ClientToolDeferralMiddleware"/>.</summary>
        public ChatClientBuilder UseClientToolDeferral() => builder.Use(inner => new ClientToolDeferralMiddleware(inner));
    }
}
