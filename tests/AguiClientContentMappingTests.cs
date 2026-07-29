using System.Text.Json;
using AGUI.Abstractions;
using AgenticTodos.Backend;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// Covers the mappings registered on <c>AGUIStreamOptions</c> that turn this app's client-facing
/// content (see <c>AguiClientContent.cs</c>) into AG-UI events.
/// </summary>
public class AguiClientContentMappingTests
{
    // ---------------------------------------------------------------------------
    // Content the SDK handles itself — the mapping must not claim it
    // ---------------------------------------------------------------------------

    [Fact]
    public void TextContent_NotClaimed()
    {
        Assert.Null(AGUIEndpoint.MapClientContent(new TextContent("hello")));
    }

    [Fact]
    public void DataContent_NotClaimed()
    {
        Assert.Null(AGUIEndpoint.MapClientContent(new DataContent("data:application/json;base64,e30=")));
    }

    // ---------------------------------------------------------------------------
    // Conversation state → STATE_SNAPSHOT
    // ---------------------------------------------------------------------------

    [Fact]
    public void ConversationState_MappedToStateSnapshot()
    {
        var snapshot = JsonSerializer.SerializeToElement(new { conversation = new { counter = 3 } });

        var evt = Assert.IsType<StateSnapshotEvent>(Assert.Single(Map(new ConversationStateContent(snapshot))));

        Assert.Equal(AGUIEventTypes.StateSnapshot, evt.Type);
        Assert.Equal(3, evt.Snapshot.GetProperty("conversation").GetProperty("counter").GetInt32());
    }

    // ---------------------------------------------------------------------------
    // MCP Apps → ACTIVITY_SNAPSHOT
    // ---------------------------------------------------------------------------

    [Fact]
    public void McpAppActivity_MappedToActivitySnapshot()
    {
        var content = new McpAppActivityContent(
            messageId: "msg-abc",
            resourceUri: "ui://get-time.html",
            result: JsonSerializer.SerializeToElement(new { content = new[] { new { type = "text", text = "12:00" } } }),
            toolInput: JsonSerializer.SerializeToElement(new { zone = "UTC" }));

        var evt = Assert.IsType<ActivitySnapshotEvent>(Assert.Single(Map(content)));

        Assert.Equal(AGUIEventTypes.ActivitySnapshot, evt.Type);
        Assert.Equal("mcp-apps", evt.ActivityType);
        Assert.Equal("msg-abc", evt.MessageId);
        Assert.True(evt.Replace);
        Assert.Equal("ui://get-time.html", evt.Content.GetProperty("resourceUri").GetString());
        Assert.Equal("UTC", evt.Content.GetProperty("toolInput").GetProperty("zone").GetString());

        var first = evt.Content.GetProperty("result").GetProperty("content")[0];
        Assert.Equal("text", first.GetProperty("type").GetString());
        Assert.Equal("12:00", first.GetProperty("text").GetString());
    }

    // ---------------------------------------------------------------------------
    // EU AI Act risk → ACTIVITY_SNAPSHOT
    // ---------------------------------------------------------------------------

    [Fact]
    public void EUAIActRisk_MappedToActivitySnapshot()
    {
        var content = new EUAIActRiskActivityContent(
            messageId: "risk-abc",
            risk: "High",
            category: "Annex III(4) employment",
            reason: "The assistant screens job applicants.");

        var evt = Assert.IsType<ActivitySnapshotEvent>(Assert.Single(Map(content)));

        Assert.Equal("eu-ai-act-risk", evt.ActivityType);
        Assert.Equal("risk-abc", evt.MessageId);
        Assert.True(evt.Replace);
        Assert.Equal("High", evt.Content.GetProperty("risk").GetString());
        Assert.Equal("Annex III(4) employment", evt.Content.GetProperty("category").GetString());
        Assert.Equal("The assistant screens job applicants.", evt.Content.GetProperty("reason").GetString());
    }

    private static List<BaseEvent> Map(AIContent content)
    {
        var events = AGUIEndpoint.MapClientContent(content);
        Assert.NotNull(events);
        return events.ToList();
    }
}
