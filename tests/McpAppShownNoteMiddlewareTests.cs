using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using AgenticTodos.Backend;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

public class McpAppShownNoteMiddlewareTests
{
    [Fact]
    public void WithNote_AIContents_AppendsNoteAsTextContent()
    {
        var result = Assert.IsType<List<AIContent>>(
            McpAppShownNoteMiddleware.Injector.WithNote(new List<AIContent> { new TextContent("Counter is at 3.") }));

        Assert.Equal(["Counter is at 3.", McpAppShownNoteMiddleware.Injector.AppShownNote],
            result.Cast<TextContent>().Select(c => c.Text));
    }

    [Fact]
    public void WithNote_Json_AppendsNoteToSerializedResult()
    {
        var text = Assert.IsType<string>(
            McpAppShownNoteMiddleware.Injector.WithNote(JsonDocument.Parse("""{"time":"12:00"}""").RootElement));

        Assert.Contains("\"12:00\"", text);
        Assert.EndsWith(McpAppShownNoteMiddleware.Injector.AppShownNote, text);
    }

    private static ChatOptions Run(string? forwardedProps) => new RunAgentInput
    {
        ThreadId = "t1",
        RunId = "r1",
        ForwardedProperties = forwardedProps is null ? null : JsonDocument.Parse(forwardedProps).RootElement,
    }.ToChatRequestContext(AguiJson.Options).ChatOptions;

    [Theory]
    [InlineData("{\"mcpApps\":true}", true)]
    [InlineData("{\"mcpApps\":false}", false)]
    [InlineData("{\"other\":true}", false)]
    [InlineData(null, false)]
    public void ClientRendersApps_ReadsForwardedProps(string? forwardedProps, bool expected) =>
        Assert.Equal(expected, McpAppShownNoteMiddleware.Injector.ClientRendersApps(Run(forwardedProps)));

    [Fact]
    public void ForModel_TextOnlyClient_LeavesMessagesUntouched()
    {
        List<ChatMessage> messages = [new(ChatRole.User, "hi")];
        Assert.Same(messages, McpAppShownNoteMiddleware.Injector.ForModel(messages, Run(null)));
    }
}
