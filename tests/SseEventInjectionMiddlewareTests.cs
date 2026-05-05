using System.Text.Json;
using AgenticTodos.Backend;

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
    public void McpActivityMarker_NullMessageId_ProducesNullInSnapshot()
    {
        var delta = JsonSerializer.Serialize(new
        {
            type = "mcp-activity",
            // no messageId
            resourceUri = "ui://get-time.html",
            result = new { content = Array.Empty<object>() },
            toolInput = new { }
        });
        var eventJson = $$"""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":{{JsonSerializer.Serialize(delta)}}}""";

        var result = Inject(eventJson)?.ToList();

        Assert.NotNull(result);
        Assert.Single(result!);
        using var doc = JsonDocument.Parse(result![0]);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("messageId").ValueKind);
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
