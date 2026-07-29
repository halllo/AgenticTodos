using System.Text.Json;
using AGUI.Abstractions;
using AgenticTodos.Cli.Verbs;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// The CLI's three pure helpers. They are small but each encodes a contract that is invisible at the
/// call site: how a client-side tool's result is marshalled back to the model, what makes a tool
/// "client-side" at all, and where the pending call rides on an approval interrupt.
/// </summary>
public class CliAgentTests
{
    // ---------------------------------------------------------------------------
    // ToToolResultText — mirrors the frontend's
    // `typeof result === 'string' ? result : JSON.stringify(result)`
    // ---------------------------------------------------------------------------

    [Fact]
    public void ToToolResultText_Null_IsEmpty()
        => Assert.Equal(string.Empty, Agent.ToToolResultText(null));

    [Fact]
    public void ToToolResultText_PlainString_PassesThrough()
        => Assert.Equal("Success: done.", Agent.ToToolResultText("Success: done."));

    [Fact]
    public void ToToolResultText_JsonStringElement_IsUnwrapped()
    {
        // The load-bearing case: AIFunctionFactory marshals a string-returning function's value through
        // JSON, so it arrives as a JSON string element. Left alone it would reach the model quoted.
        var element = JsonSerializer.SerializeToElement("Success: done.");

        Assert.Equal(JsonValueKind.String, element.ValueKind);
        Assert.Equal("Success: done.", Agent.ToToolResultText(element));
    }

    [Fact]
    public void ToToolResultText_JsonObjectElement_KeepsItsJson()
    {
        var element = JsonSerializer.SerializeToElement(new { ok = true });

        Assert.Equal("""{"ok":true}""", Agent.ToToolResultText(element));
    }

    [Fact]
    public void ToToolResultText_OtherObject_IsSerialized()
        => Assert.Equal("""{"ok":true}""", Agent.ToToolResultText(new { ok = true }));

    // ---------------------------------------------------------------------------
    // AsDeclaration — the wire half of a client-side tool
    // ---------------------------------------------------------------------------

    [Fact]
    public void AsDeclaration_KeepsTheContractButDropsTheImplementation()
    {
        // FunctionInvokingChatClient hands a call to a declaration-only tool back to the caller instead
        // of invoking it — that is exactly what makes the client the one that runs it.
        var function = AIFunctionFactory.Create(
            (string title) => "created",
            name: "add_todo",
            description: "Adds a todo.");

        var declaration = Agent.AsDeclaration(function);

        Assert.Equal("add_todo", declaration.Name);
        Assert.Equal("Adds a todo.", declaration.Description);
        Assert.Equal(function.JsonSchema.ToString(), declaration.JsonSchema.ToString());
        Assert.IsNotType<AIFunction>(declaration, exactMatch: false);
    }

    // ---------------------------------------------------------------------------
    // GetInterruptToolCall — reads what ToolApprovalInterruptMiddleware attaches
    // ---------------------------------------------------------------------------

    [Fact]
    public void GetInterruptToolCall_ReadsTheBackendsMetadataShape()
    {
        var interrupt = new InterruptRequestContent("i1")
        {
            Metadata = JsonSerializer.SerializeToElement(new
            {
                toolCall = new { callId = "call_1", name = "add_todo", arguments = new { title = "milk" } },
            }),
        };

        var toolCall = Agent.GetInterruptToolCall(interrupt);

        Assert.NotNull(toolCall);
        Assert.Equal("call_1", toolCall!.Value.GetProperty("callId").GetString());
        Assert.Equal("add_todo", toolCall.Value.GetProperty("name").GetString());
    }

    [Fact]
    public void GetInterruptToolCall_MissingMetadata_IsNull()
        => Assert.Null(Agent.GetInterruptToolCall(new InterruptRequestContent("i1")));

    [Fact]
    public void GetInterruptToolCall_MetadataWithoutAToolCall_IsNull()
    {
        // The CLI refuses to answer such an interrupt: the server rebuilds the approval pair from the
        // echoed toolCall, so a resume without one resolves nothing and leaves the interrupt open.
        var interrupt = new InterruptRequestContent("i1")
        {
            Metadata = JsonSerializer.SerializeToElement(new { somethingElse = 1 }),
        };

        Assert.Null(Agent.GetInterruptToolCall(interrupt));
    }

    /// <summary>
    /// A present-but-unusable <c>toolCall</c> must be rejected as firmly as an absent one, because the
    /// consequences downstream are identical: <c>TryDecodeToolApprovalResume</c> bails out unless the
    /// payload's <c>toolCall</c> deserializes to a non-null <c>AGUIToolCallInfo</c>, so a resume echoing
    /// any of these shapes degrades to a plain <see cref="InterruptResponseContent"/> that resolves
    /// nothing and leaves the gated call unanswered forever. Rejecting it early turns a silent hang into
    /// a reported error — and lets the prompt promise the user a tool name it can actually print.
    /// <para>
    /// The frontend's <c>parseApprovalToolCall</c> enforces the same shape (plain object, <c>callId</c>
    /// and <c>name</c> both really strings), so these cases pin the parity between the two clients.
    /// </para>
    /// </summary>
    [Theory]
    // Verified: TryGetProperty returns true for an explicit JSON null, with ValueKind == Null. Before
    // the object/string checks this passed the guard, and the CLI went on to prompt the user for a
    // decision about nothing and echo `toolCall: null` back.
    [InlineData("""{"toolCall": null}""")]
    // Not an object at all — the shape a hand-written or older server might send.
    [InlineData("""{"toolCall": "oops"}""")]
    [InlineData("""{"toolCall": 42}""")]
    [InlineData("""{"toolCall": []}""")]
    // An object, but missing the two members the CLI and the server both require.
    [InlineData("""{"toolCall": {}}""")]
    [InlineData("""{"toolCall": {"name": "add_todo"}}""")]
    [InlineData("""{"toolCall": {"callId": "call_1"}}""")]
    // Present but the wrong JSON type: `name` is dereferenced with GetString()! right after this guard.
    [InlineData("""{"toolCall": {"callId": "call_1", "name": null}}""")]
    [InlineData("""{"toolCall": {"callId": 1, "name": "add_todo"}}""")]
    // And the metadata itself has to be an object before any of that is asked.
    [InlineData("\"oops\"")]
    [InlineData("""[]""")]
    public void GetInterruptToolCall_MalformedToolCall_IsNull(string metadata)
    {
        var interrupt = new InterruptRequestContent("i1")
        {
            Metadata = JsonSerializer.Deserialize<JsonElement>(metadata),
        };

        Assert.Null(Agent.GetInterruptToolCall(interrupt));
    }
}
