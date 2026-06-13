using System.Text.Json;
using AgenticTodos.Backend;

namespace AgenticTodos.Tests;

public class EUAIActRiskActivityInjectorTests
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
    public void TextMessageContent_PlainText_ForwardedUnchanged()
    {
        var result = Inject("""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":"hello"}""");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public void TextMessageContent_McpActivityMarker_ForwardedUnchanged()
    {
        // The MCP-apps marker is not ours — forward it unchanged so the MCP-apps injector can claim it.
        var delta = JsonSerializer.Serialize(new { type = "mcp-activity", messageId = "x", resourceUri = "ui://y.html" });
        var eventJson = $$"""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":{{JsonSerializer.Serialize(delta)}}}""";

        var result = Inject(eventJson);
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    // ---------------------------------------------------------------------------
    // eu-ai-act-activity marker — replaced with ACTIVITY_SNAPSHOT
    // ---------------------------------------------------------------------------

    [Fact]
    public void RiskMarker_ReplacedWithActivitySnapshot()
    {
        var delta = JsonSerializer.Serialize(new
        {
            type = "eu-ai-act-activity",
            messageId = "risk-abc",
            risk = "High",
            category = "Annex III(4) employment",
            reason = "The assistant screens job applicants."
        });
        var eventJson = $$"""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":{{JsonSerializer.Serialize(delta)}}}""";

        var result = Inject(eventJson)?.ToList();

        Assert.NotNull(result);
        Assert.Single(result!);

        using var doc = JsonDocument.Parse(result![0]);
        var root = doc.RootElement;
        Assert.Equal("ACTIVITY_SNAPSHOT", root.GetProperty("type").GetString());
        Assert.Equal("risk-abc", root.GetProperty("messageId").GetString());
        Assert.Equal("eu-ai-act-risk", root.GetProperty("activityType").GetString());
        Assert.True(root.GetProperty("replace").GetBoolean());

        var content = root.GetProperty("content");
        Assert.Equal("High", content.GetProperty("risk").GetString());
        Assert.Equal("Annex III(4) employment", content.GetProperty("category").GetString());
        Assert.Equal("The assistant screens job applicants.", content.GetProperty("reason").GetString());
    }

    [Fact]
    public void RiskMarker_InnerMissingMessageId_FallsBackToOuterMessageId()
    {
        var delta = JsonSerializer.Serialize(new
        {
            type = "eu-ai-act-activity",
            // no inner messageId — should fall back to outer TEXT_MESSAGE_CONTENT messageId
            risk = "Unacceptable",
            category = "Article 5(1)(c) social scoring",
            reason = "Ranks citizens by behaviour."
        });
        var eventJson = $$"""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":{{JsonSerializer.Serialize(delta)}}}""";

        var result = Inject(eventJson)?.ToList();

        Assert.NotNull(result);
        Assert.Single(result!);
        using var doc = JsonDocument.Parse(result![0]);
        Assert.Equal("m", doc.RootElement.GetProperty("messageId").GetString());
    }

    [Fact]
    public void RiskMarker_MissingFields_DefaultToUnknownAndEmpty()
    {
        var delta = JsonSerializer.Serialize(new { type = "eu-ai-act-activity", messageId = "r1" });
        var eventJson = $$"""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":{{JsonSerializer.Serialize(delta)}}}""";

        var result = Inject(eventJson)?.ToList();

        Assert.NotNull(result);
        Assert.Single(result!);
        using var doc = JsonDocument.Parse(result![0]);
        var content = doc.RootElement.GetProperty("content");
        Assert.Equal("Unknown", content.GetProperty("risk").GetString());
        Assert.Equal("", content.GetProperty("category").GetString());
        Assert.Equal("", content.GetProperty("reason").GetString());
    }

    // ---------------------------------------------------------------------------
    // Composition — ActivitySnapshotInjectionMiddleware routes each marker to the right injector
    // ---------------------------------------------------------------------------

    [Fact]
    public void Composed_RiskMarker_ProducesRiskSnapshot()
    {
        var delta = JsonSerializer.Serialize(new { type = "eu-ai-act-activity", messageId = "r1", risk = "High", category = "c", reason = "r" });
        var eventJson = $$"""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":{{JsonSerializer.Serialize(delta)}}}""";

        var result = ActivitySnapshotInjectionMiddleware.TryInject(eventJson)?.ToList();

        Assert.NotNull(result);
        Assert.Single(result!);
        using var doc = JsonDocument.Parse(result![0]);
        Assert.Equal("eu-ai-act-risk", doc.RootElement.GetProperty("activityType").GetString());
    }

    [Fact]
    public void Composed_McpActivityMarker_ProducesMcpAppsSnapshot()
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

        var result = ActivitySnapshotInjectionMiddleware.TryInject(eventJson)?.ToList();

        Assert.NotNull(result);
        Assert.Single(result!);
        using var doc = JsonDocument.Parse(result![0]);
        Assert.Equal("mcp-apps", doc.RootElement.GetProperty("activityType").GetString());
    }

    [Fact]
    public void Composed_PlainText_ForwardedUnchanged()
    {
        var result = ActivitySnapshotInjectionMiddleware.TryInject("""{"type":"TEXT_MESSAGE_CONTENT","messageId":"m","delta":"hello"}""");
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    // ---------------------------------------------------------------------------
    // Helper
    // ---------------------------------------------------------------------------

    private static IEnumerable<string>? Inject(string eventJson) =>
        EUAIActRiskActivityInjector.TryInjectActivitySnapshot(eventJson);
}
