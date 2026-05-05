// AG-UI ACTIVITY_SNAPSHOT Conformance Tests
//
// Validates that McpAppsActivityMiddleware + SseEventInjectionMiddleware correctly emit
// ACTIVITY_SNAPSHOT events for MCP tools that carry a ui.resourceUri in their metadata,
// and that the mcp-activity STATE_SNAPSHOT marker is suppressed before reaching the client.
//
// These are integration tests that require a running backend and McpServer.
// All tests are skipped by default; set env var AG_UI_ENDPOINT (or run them explicitly).
//
// Environment variables:
//   AG_UI_ENDPOINT  - Full URL of the AG-UI endpoint to test
//                     (default: http://localhost:5288/agents/static/openai/agui)

using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AgenticTodos.Tests;

public sealed class ActivitySnapshotConformanceTests
{
    private const string? Skip = null; // set to a non-empty string to skip

    private const string GetTimeResourceUri = "ui://get-time.html";

    private static readonly string s_endpoint =
        Environment.GetEnvironmentVariable("AG_UI_ENDPOINT")
        ?? "http://localhost:5288/agents/static/openai/agui";

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

    private static CancellationTokenSource CreateLinkedCts(TimeSpan timeout) =>
        new CancellationTokenSource(timeout);

    private static async Task<List<JsonElement>> CollectTimeQueryEventsWithRetryAsync(
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var events = await SendAgUiRequestAsync(BuildTimeQueryBody(), cancellationToken);
            if (events.Any(e => GetEventType(e) == "ACTIVITY_SNAPSHOT"))
                return events;
        }
        // Return the last attempt's events even if no ACTIVITY_SNAPSHOT was found.
        return await SendAgUiRequestAsync(BuildTimeQueryBody(), cancellationToken);
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
        using var request = new HttpRequestMessage(HttpMethod.Post, s_endpoint)
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

    [Fact(Skip = Skip)]
    public async Task HttpResponse_HasContentType_TextEventStream_Async()
    {
        using var cts = CreateLinkedCts(s_testTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, s_endpoint)
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

    [Fact(Skip = Skip)]
    public async Task AskingWhatTimeIsIt_EmitsAtLeastOneToolCallStart_Async()
    {
        using var cts = CreateLinkedCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);

        Assert.True(
            events.Any(e => GetEventType(e) == "TOOL_CALL_START"),
            $"Expected at least one TOOL_CALL_START. Full sequence: {EventSequence(events)}");
    }

    // ---------------------------------------------------------------------------
    // ACTIVITY_SNAPSHOT presence
    // ---------------------------------------------------------------------------

    [Fact(Skip = Skip)]
    public async Task AskingWhatTimeIsIt_EmitsActivitySnapshot_Async()
    {
        using var cts = CreateLinkedCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);

        Assert.True(
            FindActivitySnapshot(events).HasValue,
            $"Expected an ACTIVITY_SNAPSHOT event. Full sequence: {EventSequence(events)}");
    }

    [Fact(Skip = Skip)]
    public async Task ActivitySnapshot_AppearsAfterToolCallResult_Async()
    {
        using var cts = CreateLinkedCts(s_testTimeout);
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

    [Fact(Skip = Skip)]
    public async Task ActivitySnapshot_ActivityType_IsMcpApps_Async()
    {
        using var cts = CreateLinkedCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);
        var snapshot = FindActivitySnapshot(events);

        Assert.True(snapshot.HasValue,
            $"No ACTIVITY_SNAPSHOT in stream. Sequence: {EventSequence(events)}");
        Assert.True(
            snapshot!.Value.TryGetProperty("activityType", out var at) && at.GetString() == "mcp-apps",
            $"activityType must be \"mcp-apps\". Got: {snapshot}");
    }

    [Fact(Skip = Skip)]
    public async Task ActivitySnapshot_Replace_IsTrue_Async()
    {
        using var cts = CreateLinkedCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);
        var snapshot = FindActivitySnapshot(events);

        Assert.True(snapshot.HasValue,
            $"No ACTIVITY_SNAPSHOT in stream. Sequence: {EventSequence(events)}");
        Assert.True(
            snapshot!.Value.TryGetProperty("replace", out var rep) && rep.GetBoolean(),
            $"replace must be true. Got: {snapshot}");
    }

    [Fact(Skip = Skip)]
    public async Task ActivitySnapshot_Content_ResourceUri_MatchesGetTime_Async()
    {
        using var cts = CreateLinkedCts(s_testTimeout);
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

    [Fact(Skip = Skip)]
    public async Task ActivitySnapshot_Content_Result_IsNormalized_Async()
    {
        using var cts = CreateLinkedCts(s_testTimeout);
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
    [Fact(Skip = Skip)]
    public async Task ActivitySnapshot_MessageId_IsPresent_Async()
    {
        using var cts = CreateLinkedCts(s_testTimeout);
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
    // Suppression — mcp-activity TEXT_MESSAGE_CONTENT marker must NOT reach the client
    // ---------------------------------------------------------------------------

    [Fact(Skip = Skip)]
    public async Task McpActivityMarker_IsSuppressed_NotVisibleToClient_Async()
    {
        using var cts = CreateLinkedCts(s_testTimeout);
        var events = await s_events.Value.WaitAsync(cts.Token);

        // The mcp-activity marker travels as TEXT_MESSAGE_CONTENT whose delta is a JSON object
        // with type=="mcp-activity". SseEventInjectionMiddleware must replace it with
        // ACTIVITY_SNAPSHOT before it reaches the client.
        var leaked = events.Where(e =>
        {
            if (GetEventType(e) != "TEXT_MESSAGE_CONTENT") return false;
            if (!e.TryGetProperty("delta", out var delta)) return false;
            var deltaText = delta.GetString();
            if (deltaText is null) return false;
            try
            {
                using var doc = JsonDocument.Parse(deltaText);
                return doc.RootElement.ValueKind == JsonValueKind.Object &&
                       doc.RootElement.TryGetProperty("type", out var t) &&
                       t.GetString() == "mcp-activity";
            }
            catch (JsonException) { return false; }
        }).ToList();

        Assert.True(leaked.Count == 0,
            $"Found {leaked.Count} TEXT_MESSAGE_CONTENT event(s) with type=mcp-activity that should have been replaced with ACTIVITY_SNAPSHOT.");
    }

    [Fact(Skip = Skip)]
    public async Task RegularStateSnapshot_IsStillPresent_Async()
    {
        using var cts = CreateLinkedCts(s_testTimeout);
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
        new(() => SendAgUiRequestAsync(BuildIncrementBody(), CancellationToken.None));

    private static object BuildIncrementBody() => new
    {
        threadId = Guid.NewGuid().ToString(),
        runId = Guid.NewGuid().ToString(),
        messages = new[] { new { id = Guid.NewGuid().ToString(), role = "user", content = "Please increment the counter." } },
        tools = Array.Empty<object>(),
        context = Array.Empty<object>(),
        state = new { conversation = new { selectedResources = Array.Empty<string>(), counter = 0 } },
        forwardedProps = new { }
    };

    [Fact(Skip = Skip)]
    public async Task NonMcpTool_IncrementCounter_DoesNotEmitActivitySnapshot_Async()
    {
        using var cts = CreateLinkedCts(s_testTimeout);
        var events = await s_incrementEvents.Value.WaitAsync(cts.Token);

        Assert.False(
            events.Any(e => GetEventType(e) == "ACTIVITY_SNAPSHOT"),
            $"Expected no ACTIVITY_SNAPSHOT for non-MCP tool. Full sequence: {EventSequence(events)}");
    }

    [Fact(Skip = Skip)]
    public async Task NonMcpTool_IncrementCounter_EmitsToolCallResult_Async()
    {
        using var cts = CreateLinkedCts(s_testTimeout);
        var events = await s_incrementEvents.Value.WaitAsync(cts.Token);

        Assert.True(
            events.Any(e => GetEventType(e) == "TOOL_CALL_RESULT"),
            $"Expected TOOL_CALL_RESULT for increment_counter. Full sequence: {EventSequence(events)}");
    }
}
