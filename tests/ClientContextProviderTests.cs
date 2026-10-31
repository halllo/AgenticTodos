using AGUI.Abstractions;
using AGUI.Server;
using AgenticTodos.Backend;
using Microsoft.Agents.AI;

namespace AgenticTodos.Tests;

public class ClientContextProviderTests
{
    private static ChatClientAgentRunOptions Run(params AGUIContext[] context) => new()
    {
        ChatOptions = new RunAgentInput { ThreadId = "t1", RunId = "r1", Context = context }
            .ToChatRequestContext(AguiJson.Options).ChatOptions,
    };

    [Fact]
    public void Instructions_JoinEntries()
    {
        var text = ClientContextProvider.Instructions(Run(
            new AGUIContext { Description = "MCP apps open in the side panel", Value = "- A2UI canvas" },
            new AGUIContext { Description = "Other", Value = "42" }));

        Assert.NotNull(text);
        Assert.Contains("## MCP apps open in the side panel\n- A2UI canvas", text);
        Assert.Contains("## Other\n42", text);
    }

    [Fact]
    public void Instructions_NoContext_ReturnsNull()
    {
        Assert.Null(ClientContextProvider.Instructions(Run()));
        Assert.Null(ClientContextProvider.Instructions(new ChatClientAgentRunOptions()));
    }
}
