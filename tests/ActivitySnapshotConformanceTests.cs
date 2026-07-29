// AG-UI ACTIVITY_SNAPSHOT Conformance Tests
//
// Validates that DetectMcpAppsActivityMiddleware plus the AGUIStreamOptions mappings emit
// ACTIVITY_SNAPSHOT events for MCP tools that carry a ui.resourceUri in their metadata, and that
// the app's own content never leaks onto the wire as a text message.
//
// These are integration tests: they require a running backend plus McpServer and they make real LLM
// calls. They are skipped unless AG_UI_ENDPOINT is set (see AgUiEndpointFactAttribute), so a plain
// `dotnet test` neither depends on a live server nor spends money.
//
//   AG_UI_ENDPOINT=http://localhost:5288/agents/routed/openai/agui dotnet test

using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AgenticTodos.Tests;

public sealed class ActivitySnapshotConformanceTests
{
    private const string GetTimeResourceUri = "ui://get-time.html";

    private static string Endpoint => AgUiEndpointFactAttribute.Endpoint;

    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_operationTimeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient s_httpClient = new();

    // Shared event list — one LLM call shared across all tests in the class.
    // Retries up to 3 times in case the LLM does not call the get-time tool on the first attempt.
    private static readonly Lazy<Task<List<JsonElement>>> s_events =
        new(() => CollectTimeQueryEventsWithRetryAsync(CancellationToken.None));

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static CancellationTokenSource CreateTimeoutCts(TimeSpan timeout) =>
        new CancellationTokenSource(timeout);

    private static async Task<List<JsonElement>> CollectTimeQueryEventsWithRetryAsync(
        CancellationToken cancellationToken)
    {
        List<JsonElement> events = [];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            events = await SendAgUiRequestAsync(BuildTimeQueryBody(), cancellationToken);
            if (events.Any(e => GetEventType(e) == "ACTIVITY_SNAPSHOT"))
                return events;
        }

        // Return the last attempt's events even if no ACTIVITY_SNAPSHOT was found — asking a fourth
        // time would only add another billed call to a run that is already going to fail.
        return events;
    }

    private static object BuildTimeQueryBody() => new
    {
        threadId = Guid.NewGuid().ToString(),
        runId = Guid.NewGuid().ToString(),
        messages = new[] { new { id = Guid.NewGuid().ToString(), role = "user", content = "What time is it?" } },
        tools = Array.Empty<object>(),
        context = Array.Empty<object>(),
        state = new { conversation = new { selectedResources = Array.Empty<string>(), counter = 0 } },
        forwardedProps = new { }
    };

    private static async Task<List<JsonElement>> SendAgUiRequestAsync(
        object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await s_httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .WaitAsync(s_operationTimeout, cancellationToken);

        return await CollectSseEventsAsync(response, cancellationToken);
    }

    private static async Task<List<JsonElement>> CollectSseEventsAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var events = new List<JsonElement>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)
                   .AsTask().WaitAsync(s_operationTimeout, cancellationToken)) is not null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line[6..].Trim();
            if (string.IsNullOrEmpty(payload) || payload == "[DONE]") continue;

            try
            {
                var evt = JsonDocument.Parse(payload).RootElement.Clone();
                events.Add(evt);
                if (GetEventType(evt) is "RUN_FINISHED" or "RUN_ERROR") break;
            }
            catch (JsonException) { /* skip malformed lines */ }
        }
        return events;
    }

    private static string GetEventType(JsonElement evt) =>
        evt.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

    private static string EventSequence(List<JsonElement> events) =>
        string.Join(", ", events.Select(GetEventType));

    private static JsonElement? FindActivitySnapshot(List<JsonElement> events) =>
        events.Cast<JsonElement?>()
            .FirstOrDefault(e => e.HasValue && GetEventType(e.Value) == "ACTIVITY_SNAPSHOT");

    // ---------------------------------------------------------------------------
    // HTTP layer
    // ---------------------------------------------------------------------------

    [AgUiEndpointFact]
    public async Task HttpResponse_HasContentType_TextEventStream()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(BuildTimeQueryBody()), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await s_httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .WaitAsync(s_operationTimeout, cts.Token);

        var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
        Assert.Contains("text/event-stream", contentType, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // LLM routing
    // ---------------------------------------------------------------------------

    [AgUiEndpointFact]
    public async Task AskingWhatTimeIsIt_EmitsAtLeastOneToolCallStart()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);

        Assert.True(
            events.Any(e => GetEventType(e) == "TOOL_CALL_START"),
            $"Expected at least one TOOL_CALL_START. Full sequence: {EventSequence(events)}");
    }

    // ---------------------------------------------------------------------------
    // ACTIVITY_SNAPSHOT presence
    // ---------------------------------------------------------------------------

    [AgUiEndpointFact]
    public async Task AskingWhatTimeIsIt_EmitsActivitySnapshot()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);

        Assert.True(
            FindActivitySnapshot(events).HasValue,
            $"Expected an ACTIVITY_SNAPSHOT event. Full sequence: {EventSequence(events)}");
    }

    [AgUiEndpointFact]
    public async Task ActivitySnapshot_AppearsAfterToolCallResult()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);

        int toolResultIdx = events.FindLastIndex(e => GetEventType(e) == "TOOL_CALL_RESULT");
        int snapshotIdx = events.FindIndex(e => GetEventType(e) == "ACTIVITY_SNAPSHOT");

        Assert.True(toolResultIdx >= 0,
            $"Expected TOOL_CALL_RESULT. Sequence: {EventSequence(events)}");
        Assert.True(snapshotIdx > toolResultIdx,
            $"ACTIVITY_SNAPSHOT (idx {snapshotIdx}) must come after TOOL_CALL_RESULT (idx {toolResultIdx}). " +
            $"Sequence: {EventSequence(events)}");
    }

    // ---------------------------------------------------------------------------
    // ACTIVITY_SNAPSHOT shape
    // ---------------------------------------------------------------------------

    [AgUiEndpointFact]
    public async Task ActivitySnapshot_ActivityType_IsMcpApps()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);
        var snapshot = FindActivitySnapshot(events);

        Assert.True(snapshot.HasValue,
            $"No ACTIVITY_SNAPSHOT in stream. Sequence: {EventSequence(events)}");
        Assert.True(
            snapshot!.Value.TryGetProperty("activityType", out var at) && at.GetString() == "mcp-apps",
            $"activityType must be \"mcp-apps\". Got: {snapshot}");
    }

    [AgUiEndpointFact]
    public async Task ActivitySnapshot_Replace_IsTrue()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);
        var snapshot = FindActivitySnapshot(events);

        Assert.True(snapshot.HasValue,
            $"No ACTIVITY_SNAPSHOT in stream. Sequence: {EventSequence(events)}");
        Assert.True(
            snapshot!.Value.TryGetProperty("replace", out var rep) && rep.GetBoolean(),
            $"replace must be true. Got: {snapshot}");
    }

    [AgUiEndpointFact]
    public async Task ActivitySnapshot_Content_ResourceUri_MatchesGetTime()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);
        var snapshot = FindActivitySnapshot(events);

        Assert.True(snapshot.HasValue,
            $"No ACTIVITY_SNAPSHOT in stream. Sequence: {EventSequence(events)}");
        Assert.True(
            snapshot!.Value.TryGetProperty("content", out var content) &&
            content.TryGetProperty("resourceUri", out var uri) &&
            uri.GetString() == GetTimeResourceUri,
            $"content.resourceUri must be \"{GetTimeResourceUri}\". Got: {snapshot}");
    }

    [AgUiEndpointFact]
    public async Task ActivitySnapshot_Content_Result_IsNormalized()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);
        var snapshot = FindActivitySnapshot(events);

        Assert.True(snapshot.HasValue,
            $"No ACTIVITY_SNAPSHOT in stream. Sequence: {EventSequence(events)}");

        var content = snapshot!.Value.GetProperty("content");
        var result = content.GetProperty("result");
        var firstItem = result.GetProperty("content")[0];

        Assert.True(
            firstItem.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "text",
            $"content.result.content[0].type must be \"text\". Got: {firstItem}");
        Assert.True(
            firstItem.TryGetProperty("text", out var textProp) &&
            !string.IsNullOrWhiteSpace(textProp.GetString()),
            $"content.result.content[0].text must be non-empty. Got: {firstItem}");
    }

    // ---------------------------------------------------------------------------
    [AgUiEndpointFact]
    public async Task ActivitySnapshot_MessageId_IsPresent()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);
        var snapshot = FindActivitySnapshot(events);

        Assert.True(snapshot.HasValue,
            $"No ACTIVITY_SNAPSHOT in stream. Sequence: {EventSequence(events)}");
        Assert.True(
            snapshot!.Value.TryGetProperty("messageId", out var mid) &&
            !string.IsNullOrEmpty(mid.GetString()),
            $"messageId must be present and non-empty. Got: {snapshot}");
    }

    // ---------------------------------------------------------------------------
    // No leaks — the MCP-apps payload must arrive as ACTIVITY_SNAPSHOT, never as chat text
    // ---------------------------------------------------------------------------

    [AgUiEndpointFact]
    public async Task McpAppPayload_NeverArrivesAsTextMessage()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);

        // The payload rides on a dedicated content type that the stream mappings turn into an
        // ACTIVITY_SNAPSHOT; if a mapping goes missing it would either vanish or surface as text.
        var leaked = events.Where(e =>
            GetEventType(e) == "TEXT_MESSAGE_CONTENT" &&
            e.TryGetProperty("delta", out var delta) &&
            delta.GetString() is { } text &&
            text.Contains("resourceUri", StringComparison.Ordinal)).ToList();

        Assert.True(leaked.Count == 0,
            $"Found {leaked.Count} TEXT_MESSAGE_CONTENT event(s) carrying an MCP-apps payload; it must be an ACTIVITY_SNAPSHOT.");
    }

    [AgUiEndpointFact]
    public async Task RegularStateSnapshot_IsStillPresent()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);

        var regularSnapshot = events.FirstOrDefault(e =>
            GetEventType(e) == "STATE_SNAPSHOT" &&
            e.TryGetProperty("snapshot", out var snap) &&
            snap.TryGetProperty("conversation", out _));

        Assert.True(
            regularSnapshot.ValueKind != JsonValueKind.Undefined,
            $"Expected a regular STATE_SNAPSHOT with a \"conversation\" key. Sequence: {EventSequence(events)}");
    }

    // ---------------------------------------------------------------------------
    // Non-MCP tool — increment_counter must NOT produce ACTIVITY_SNAPSHOT
    // ---------------------------------------------------------------------------

    // Separate lazy request for the increment-counter scenario; shared across tests in this group.
    private static readonly Lazy<Task<List<JsonElement>>> s_incrementEvents =
        new(() => CollectIncrementEventsWithApprovalAsync(CancellationToken.None));

    // increment_counter is approval-gated (HumanInTheLoop:ApprovalRequiredTools, see
    // human-in-the-loop.md): the first run ends with RUN_FINISHED carrying an interrupt outcome.
    // Approve it with a resume entry on the same thread, returning both runs' events combined.
    private static async Task<List<JsonElement>> CollectIncrementEventsWithApprovalAsync(
        CancellationToken cancellationToken)
    {
        var threadId = Guid.NewGuid().ToString();
        var events = await SendAgUiRequestAsync(BuildIncrementBody(threadId), cancellationToken);

        var interrupts = FindInterrupts(events);
        if (interrupts.Count == 0)
            return events; // no approval requested — return the single run as-is

        // The decision echoes the tool call from the interrupt's metadata back verbatim; that is what
        // lets the backend rebuild the approval without correlation state between the two runs.
        var resume = interrupts.Select(interrupt => new
        {
            interruptId = interrupt.GetProperty("id").GetString(),
            status = "resolved",
            payload = new
            {
                toolCall = interrupt.GetProperty("metadata").GetProperty("toolCall").Clone(),
                approved = true,
                reason = (string?)null,
                alwaysApprove = (string?)null,
            }
        }).ToArray();

        var resumeBody = new
        {
            threadId,
            runId = Guid.NewGuid().ToString(),
            messages = Array.Empty<object>(),
            tools = Array.Empty<object>(),
            context = Array.Empty<object>(),
            state = new { conversation = new { selectedResources = Array.Empty<string>(), counter = 0 } },
            forwardedProps = new { },
            resume,
        };
        events.AddRange(await SendAgUiRequestAsync(resumeBody, cancellationToken));
        return events;
    }

    /// <summary>Interrupts carried by a run's <c>RUN_FINISHED</c> outcome, if any.</summary>
    private static List<JsonElement> FindInterrupts(List<JsonElement> events) =>
    [
        .. events
            .Where(e => GetEventType(e) == "RUN_FINISHED")
            .SelectMany(e => e.TryGetProperty("outcome", out var outcome) &&
                             outcome.ValueKind == JsonValueKind.Object &&
                             outcome.TryGetProperty("interrupts", out var list) &&
                             list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray()
                : [])
    ];

    private static object BuildIncrementBody(string threadId) => new
    {
        threadId,
        runId = Guid.NewGuid().ToString(),
        messages = new[] { new { id = Guid.NewGuid().ToString(), role = "user", content = "Please increment the counter." } },
        tools = Array.Empty<object>(),
        context = Array.Empty<object>(),
        state = new { conversation = new { selectedResources = Array.Empty<string>(), counter = 0 } },
        forwardedProps = new { }
    };

    [AgUiEndpointFact]
    public async Task NonMcpTool_IncrementCounter_DoesNotEmitActivitySnapshot()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);
        var events = await s_incrementEvents.Value.WaitAsync(cts.Token);

        Assert.False(
            events.Any(e => GetEventType(e) == "ACTIVITY_SNAPSHOT"),
            $"Expected no ACTIVITY_SNAPSHOT for non-MCP tool. Full sequence: {EventSequence(events)}");
    }

    [AgUiEndpointFact]
    public async Task ApprovalGatedTool_PausesWithConfirmationInterrupt()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);
        var events = await s_incrementEvents.Value.WaitAsync(cts.Token);

        var interrupts = FindInterrupts(events);
        Assert.True(interrupts.Count > 0,
            $"Expected an interrupt outcome for the approval-gated tool. Full sequence: {EventSequence(events)}");

        var interrupt = interrupts[0];
        Assert.Equal("confirmation", interrupt.GetProperty("reason").GetString());
        Assert.Equal(
            "increment_counter",
            interrupt.GetProperty("metadata").GetProperty("toolCall").GetProperty("name").GetString());
    }

    [AgUiEndpointFact]
    public async Task NonMcpTool_IncrementCounter_EmitsToolCallResult()
    {
        using var cts = CreateTimeoutCts(s_testTimeout);
        var events = await s_incrementEvents.Value.WaitAsync(cts.Token);

        Assert.True(
            events.Any(e => GetEventType(e) == "TOOL_CALL_RESULT"),
            $"Expected TOOL_CALL_RESULT for increment_counter. Full sequence: {EventSequence(events)}");
    }
}
