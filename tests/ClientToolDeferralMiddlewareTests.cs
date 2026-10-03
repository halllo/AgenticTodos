using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using AgenticTodos.Backend;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// A model turn that calls a server tool and a client (WebMCP) tool in parallel — Bedrock does this
/// readily — must not leave the server call unanswered: Bedrock then rejects every later request of the
/// thread with <i>"Expected toolResult blocks at messages.N.content for the following Ids: …"</i>.
/// <see cref="ClientToolDeferralMiddleware"/> splits such a turn in two, and puts it back together for
/// the model afterwards. Driven mostly through <c>GetStreamingResponseAsync</c>, the path production
/// takes, with a scripted inner client standing in for the model; the tests under "the loop" put the
/// real <see cref="FunctionInvokingChatClient"/> on top.
/// </summary>
public class ClientToolDeferralMiddlewareTests
{
    // ---------------------------------------------------------------------------
    // The split
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Streaming_MixedTurn_PassesOnlyTheServerCalls_ThenReleasesTheClientCalls()
    {
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model);
        var options = Options();

        // Step 1: the loop sees the server call only, so it runs it.
        var first = await StreamAsync(client, [User()], options);
        Assert.Equal(["s1"], CallIds(first));

        // Step 2: the loop's next round-trip gets the held client call, without asking the model.
        var second = await StreamAsync(client, [User(), Assistant(ServerCall("s1")), ToolResult("s1")], options);
        var released = Assert.Single(second);
        Assert.Equal(["c1"], CallIds(second));
        Assert.Equal(ChatFinishReason.ToolCalls, released.FinishReason);
        Assert.False(string.IsNullOrEmpty(released.MessageId));
        Assert.NotEqual("msg_1", released.MessageId);
        Assert.Equal(1, model.Calls);

        // Step 3: the client's follow-up run goes to the model again.
        await StreamAsync(client, AfterClientResults(), options);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task NonStreaming_MixedTurn_PassesOnlyTheServerCalls_ThenReleasesTheClientCalls()
    {
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model);
        var options = Options();

        var first = await client.GetResponseAsync([User()], options);
        Assert.Equal(["s1"], CallIds(first.Messages));

        var second = await client.GetResponseAsync([User(), Assistant(ServerCall("s1")), ToolResult("s1")], options);
        Assert.Equal(["c1"], CallIds(second.Messages));
        Assert.Equal(ChatFinishReason.ToolCalls, second.FinishReason);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Streaming_CallsSpreadOverSeveralUpdates_AreSplitAcrossThem()
    {
        // As a provider streams: text, then one update per call — the client call first — then the finish
        // reason on an update of its own. An update left with nothing once its client call is held back
        // is not passed on.
        List<ChatResponseUpdate> turn =
        [
            new(ChatRole.Assistant, "Sure.") { MessageId = "msg_1" },
            new(ChatRole.Assistant, [ClientCall("c1")]) { MessageId = "msg_1" },
            new(ChatRole.Assistant, [ServerCall("s1")]) { MessageId = "msg_1" },
            new() { MessageId = "msg_1", FinishReason = ChatFinishReason.ToolCalls },
        ];
        using var client = new ClientToolDeferralMiddleware(new ScriptedChatClient(turn));
        var options = Options();

        var first = await StreamAsync(client, [User()], options);

        Assert.Equal(["Sure.", "call:s1", "finish"], first.Select(Describe));

        var second = await StreamAsync(client, [User(), Assistant(ServerCall("s1")), ToolResult("s1")], options);
        Assert.Equal(["c1"], CallIds(second));
    }

    [Fact]
    public async Task Streaming_AHeldCallCarryingTheFinishReason_KeepsTheFinish()
    {
        List<ChatResponseUpdate> turn =
        [
            new(ChatRole.Assistant, [ServerCall("s1")]) { MessageId = "msg_1" },
            new(ChatRole.Assistant, [ClientCall("c1")]) { MessageId = "msg_1", FinishReason = ChatFinishReason.ToolCalls },
        ];
        using var client = new ClientToolDeferralMiddleware(new ScriptedChatClient(turn));

        var first = await StreamAsync(client, [User()], Options());

        Assert.Equal(["call:s1", "finish"], first.Select(Describe));
    }

    [Fact]
    public async Task NonStreaming_AMessageLeftWithNothing_IsDropped()
    {
        // The held calls came back under a message id of their own, so trimming them leaves an empty message.
        List<ChatResponseUpdate> turn =
        [
            new(ChatRole.Assistant, [ServerCall("s1")]) { MessageId = "msg_1" },
            new(ChatRole.Assistant, [ClientCall("c1")]) { MessageId = "msg_2" },
        ];
        using var client = new ClientToolDeferralMiddleware(new ScriptedChatClient(turn));

        var response = await client.GetResponseAsync([User()], Options());

        var message = Assert.Single(response.Messages);
        Assert.Equal(["s1"], CallIds([message]));
    }

    [Fact]
    public async Task Streaming_TrimmedUpdate_IsACopy_TheModelsUpdateKeepsItsCalls()
    {
        // The loop above may build the next request from the update objects it received.
        var update = Turn("msg_1", ServerCall("s1"), ClientCall("c1"))[0];
        using var client = new ClientToolDeferralMiddleware(new ScriptedChatClient([update]));

        await StreamAsync(client, [User()], Options());

        Assert.Equal(["s1", "c1"], update.Contents.OfType<FunctionCallContent>().Select(c => c.CallId));
    }

    [Fact]
    public async Task Streaming_TrimmedUpdate_DropsTheProvidersRawObject()
    {
        // The raw object is the provider's form of the whole update, held calls included.
        var update = Turn("msg_1", ServerCall("s1"), ClientCall("c1"))[0];
        update.RawRepresentation = "provider chunk";
        using var client = new ClientToolDeferralMiddleware(new ScriptedChatClient([update]));

        var first = await StreamAsync(client, [User()], Options());

        Assert.Null(Assert.Single(first).RawRepresentation);
    }

    [Fact]
    public async Task SameNamedTools_TheFirstDeclarationDecides_AsInTheLoop()
    {
        // The loop looks a call's tool up by name and takes the first match.
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model);
        var shadowing = AIFunctionFactory.Create(() => "ok", ClientTool.Name);

        var updates = await StreamAsync(client, [User()], Options(ServerTool, ClientTool, shadowing));

        Assert.Equal(["s1"], CallIds(updates));
    }

    [Fact]
    public async Task NonStreaming_TrimmedMessage_IsACopy_TheModelsMessageKeepsItsCalls()
    {
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model);

        await client.GetResponseAsync([User()], Options());

        Assert.Equal(["s1", "c1"], CallIds(model.LastResponseMessages));
    }

    [Fact]
    public async Task HeldCalls_AreReleasedOnlyOnceEveryServerCallIsAnswered_InAnyOrder()
    {
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ServerCall("s2", OtherServerTool.Name), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model);
        var options = Options();
        await StreamAsync(client, [User()], options);
        var serverTurn = Assistant(ServerCall("s2", OtherServerTool.Name), ServerCall("s1"));

        var partial = await StreamAsync(client, [User(), serverTurn, ToolResult("s1")], options);
        Assert.DoesNotContain("c1", CallIds(partial));
        Assert.Equal(2, model.Calls);

        var complete = await StreamAsync(client, [User(), serverTurn, ToolResult("s2"), ToolResult("s1")], options);
        Assert.Equal(["c1"], CallIds(complete));
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task HeldCalls_AreFoundBehindEarlierTurns_AndAcrossSeveralAssistantMessages()
    {
        // The split turn is the latest one, not the first; and the calls it ran can come back as more than one
        // assistant message — e.g. when approvals answered one at a time make the loop rebuild them separately.
        var model = new ScriptedChatClient(
            Turn("msg_1", ServerCall("s1"), ServerCall("s2", OtherServerTool.Name), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model);
        var options = Options();
        List<ChatMessage> earlier = [User(), Assistant(new TextContent("Hello.")), User()];
        await StreamAsync(client, earlier, options);

        var second = await StreamAsync(client,
            [
                .. earlier, Assistant(ServerCall("s1")), Assistant(ServerCall("s2", OtherServerTool.Name)),
                ToolResult("s1"), ToolResult("s2"),
            ],
            options);

        Assert.Equal(["c1"], CallIds(second));
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task HeldCalls_AreNotReleased_WhenOneResultIsDuplicatedAndAnotherMissing()
    {
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ServerCall("s2", OtherServerTool.Name), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model);
        var options = Options();
        await StreamAsync(client, [User()], options);

        var second = await StreamAsync(client,
            [User(), Assistant(ServerCall("s1"), ServerCall("s2", OtherServerTool.Name)), ToolResult("s1"), ToolResult("s1")],
            options);

        Assert.DoesNotContain("c1", CallIds(second));
    }

    [Fact]
    public async Task HeldCalls_AreDropped_WhenTheThreadMovesOnWithoutAnsweringTheServerCalls()
    {
        // E.g. the user ignored the approval card of a gated server call and asked something else. The
        // history provider then answers that call with a synthetic rejection *before* the new message;
        // releasing the held call now would run a stale request and leave the new message unanswered.
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model);
        var options = Options();
        await StreamAsync(client, [User()], options);

        var movedOn = await StreamAsync(client, [User(), Assistant(ServerCall("s1")), ToolResult("s1"), User()], options);
        Assert.DoesNotContain("c1", CallIds(movedOn));
        Assert.Equal(2, model.Calls);

        // And they are gone for good, not merely skipped.
        var later = await StreamAsync(client, [User(), Assistant(ServerCall("s1")), ToolResult("s1")], options);
        Assert.DoesNotContain("c1", CallIds(later));
        Assert.Equal(3, model.Calls);
    }

    [Fact]
    public async Task HeldCalls_Expire_AfterTheRetentionPeriodWithoutUse()
    {
        // A run that never continues (failed, abandoned) must not hold its calls forever.
        var time = new ManualTimeProvider();
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model, TimeSpan.FromMinutes(10), time);
        var options = Options();
        await StreamAsync(client, [User()], options);

        time.Advance(TimeSpan.FromMinutes(11));
        var second = await StreamAsync(client, [User(), Assistant(ServerCall("s1")), ToolResult("s1")], options);

        Assert.DoesNotContain("c1", CallIds(second));
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task HeldCalls_Retention_IsSliding()
    {
        // An approval that is still pending keeps its held calls alive as long as the thread is in use.
        var time = new ManualTimeProvider();
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model, TimeSpan.FromMinutes(10), time);
        var options = Options();
        await StreamAsync(client, [User()], options);

        time.Advance(TimeSpan.FromMinutes(8));
        await StreamAsync(client, [User(), Assistant(ServerCall("s1"))], options); // still awaiting approval
        time.Advance(TimeSpan.FromMinutes(8));
        var second = await StreamAsync(client, [User(), Assistant(ServerCall("s1")), ToolResult("s1")], options);

        Assert.Equal(["c1"], CallIds(second));
    }

    [Fact]
    public async Task HeldCalls_AreReleasedOnce()
    {
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model);
        var options = Options();
        List<ChatMessage> step2 = [User(), Assistant(ServerCall("s1")), ToolResult("s1")];
        await StreamAsync(client, [User()], options);
        await StreamAsync(client, step2, options);

        var again = await StreamAsync(client, step2, options);

        Assert.DoesNotContain("c1", CallIds(again));
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task ConcurrentRequestsForTheSameStep_ReleaseTheHeldCallsOnlyOnce()
    {
        // A retried or double-submitted request while the first is in flight: releasing twice would send the
        // client call on two runs, and the duplicate tool_use id makes Bedrock reject the thread from then on.
        // The race is narrow, so it is run many times, the requests released together by a barrier.
        const int Requests = 4;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
            using var client = new ClientToolDeferralMiddleware(model);
            var options = Options();
            await StreamAsync(client, [User()], options);
            using var start = new Barrier(Requests);

            var results = await Task.WhenAll(Enumerable.Range(0, Requests).Select(_ => Task.Run(() =>
            {
                start.SignalAndWait();
                return StreamAsync(client, [User(), Assistant(ServerCall("s1")), ToolResult("s1")], options);
            })));

            Assert.Single(results, updates => CallIds(updates).Contains("c1"));
        }
    }

    [Fact]
    public async Task TwoThreads_ReusingTheSameCallIds_EachGetTheirOwnHeldCalls()
    {
        // One middleware instance serves every thread of the agent. Keys include the AG-UI thread id, so
        // even a provider that reused call ids could not hand one thread's call to another.
        var model = new ScriptedChatClient(
            Turn("msg_a", ServerCall("s1"), ClientCall("c1", "for A")),
            Turn("msg_b", ServerCall("s1"), ClientCall("c1", "for B")));
        using var client = new ClientToolDeferralMiddleware(model);
        var threadA = AgUiOptions("thread-a");
        var threadB = AgUiOptions("thread-b");
        List<ChatMessage> step2 = [User(), Assistant(ServerCall("s1")), ToolResult("s1")];

        await StreamAsync(client, [User()], threadA);
        await StreamAsync(client, [User()], threadB);
        var releasedB = await StreamAsync(client, step2, threadB);
        var releasedA = await StreamAsync(client, step2, threadA);

        Assert.Equal("for B", Title(Assert.Single(releasedB)));
        Assert.Equal("for A", Title(Assert.Single(releasedA)));
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task AFailedStream_HoldsNothingBack()
    {
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1"))) { FailFirstCall = true };
        using var client = new ClientToolDeferralMiddleware(model);
        var options = Options();

        await Assert.ThrowsAsync<InvalidOperationException>(() => StreamAsync(client, [User()], options));
        var next = await StreamAsync(client, [User(), Assistant(ServerCall("s1")), ToolResult("s1")], options);

        Assert.DoesNotContain("c1", CallIds(next));
        Assert.Equal(2, model.Calls);
    }

    // ---------------------------------------------------------------------------
    // Turns that are not split
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Streaming_ClientCallsOnly_GoOutUnsplit()
    {
        using var client = new ClientToolDeferralMiddleware(
            new ScriptedChatClient(Turn("msg_1", ClientCall("c1"), ClientCall("c2"))));

        var updates = await StreamAsync(client, [User()], Options());

        Assert.Equal(["c1", "c2"], CallIds(updates));
        var callUpdate = Assert.Single(updates, u => u.Contents.OfType<FunctionCallContent>().Any());
        Assert.Equal("msg_1", callUpdate.MessageId);
        Assert.Equal("resp_1", callUpdate.ResponseId);
        Assert.Equal("model", callUpdate.ModelId);
    }

    [Fact]
    public async Task NonStreaming_ClientCallsOnly_GoOutUnsplit()
    {
        var model = new ScriptedChatClient(Turn("msg_1", ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model);
        var options = Options();

        var response = await client.GetResponseAsync([User()], options);
        Assert.Equal(["c1"], CallIds(response.Messages));

        // Nothing was held either: the next round-trip goes to the model.
        await client.GetResponseAsync([User(), Assistant(ClientCall("c1")), ToolResult("c1")], options);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task ServerCallsOnly_GoOutUnchanged()
    {
        var update = Turn("msg_1", ServerCall("s1"))[0];
        using var client = new ClientToolDeferralMiddleware(new ScriptedChatClient([update]));

        var updates = await StreamAsync(client, [User()], Options());

        Assert.Same(update, Assert.Single(updates));
    }

    [Fact]
    public async Task WithoutClientTools_EveryUpdatePassesThroughAsIs()
    {
        var update = Turn("msg_1", ServerCall("s1"), ClientCall("c1"))[0];
        using var client = new ClientToolDeferralMiddleware(new ScriptedChatClient([update]));

        var updates = await StreamAsync(client, [User()], new ChatOptions { Tools = [ServerTool] });

        Assert.Same(update, Assert.Single(updates));
    }

    [Fact]
    public async Task FullHistoryRun_ClientToolIsAnApprovalProxy_TheTurnIsLeftToTheLoop()
    {
        // When the run's input carries an earlier add_todo call and its result — a client resending the full
        // history — the AG-UI SDK installs add_todo as an invocable approval proxy. The loop does not stop on that — it escalates the turn to approval requests instead (see
        // ToolApprovalSiblingEscalationTests) — so nothing is held back, and nothing reordered.
        var options = AgUiOptions("t1", priorClientCall: "c0");
        Assert.IsAssignableFrom<AIFunction>(options.Tools!.Single(tool => tool.Name == ClientTool.Name));
        using var client = new ClientToolDeferralMiddleware(new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1"))));

        var updates = await StreamAsync(client, [User()], options);

        Assert.Equal(["s1", "c1"], CallIds(updates));
    }

    // ---------------------------------------------------------------------------
    // Under the real function-invocation loop
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task UnderTheLoop_TheServerCallRuns_AndTheRunEndsOnTheClientCall()
    {
        var serverInvocations = 0;
        var serverTool = AIFunctionFactory.Create(() => { serverInvocations++; return "12:00"; }, "get_time");
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new FunctionInvokingChatClient(new ClientToolDeferralMiddleware(model));

        var updates = await StreamAsync(client, [User()], Options(serverTool, ClientTool));

        Assert.Equal(1, serverInvocations);
        Assert.Equal(1, model.Calls);
        var messages = updates.ToChatResponse().Messages;
        Assert.Equal(["assistant:s1", "tool:s1", "assistant:c1"], Describe(messages));
        Assert.Equal("msg_1", messages[0].MessageId);
        Assert.False(string.IsNullOrEmpty(messages[2].MessageId));
        Assert.NotEqual("msg_1", messages[2].MessageId);
    }

    [Fact]
    public async Task UnderTheLoop_AnUnknownServerCall_IsAnsweredWithAnError_AndTheClientCallStillGoesOut()
    {
        // The loop answers a call to an unknown tool with "function not found" — but only if it gets to run
        // anything at all, which it would not with the client call in the same turn.
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1", "no_such_tool"), ClientCall("c1")));
        using var client = new FunctionInvokingChatClient(new ClientToolDeferralMiddleware(model));

        var updates = await StreamAsync(client, [User()], Options());

        var result = Assert.Single(updates.SelectMany(u => u.Contents).OfType<FunctionResultContent>());
        Assert.Equal("s1", result.CallId);
        Assert.Contains("c1", CallIds(updates));
    }

    [Fact]
    public async Task UnderTheLoop_ApprovalGatedServerCall_TheClientCallIsReleasedInTheResumedRun()
    {
        var serverInvocations = 0;
        var gated = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create(() => { serverInvocations++; return "12:00"; }, "get_time"));
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new FunctionInvokingChatClient(new ClientToolDeferralMiddleware(model));
        var options = Options(gated, ClientTool);

        // Run 1 ends on the approval request for the server call alone; the client call is held.
        var run1 = await client.GetResponseAsync([User()], options);
        var request = Assert.Single(run1.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());
        Assert.Equal("s1", Assert.IsType<FunctionCallContent>(request.ToolCall).CallId);
        Assert.DoesNotContain("c1", CallIds(run1.Messages));

        // Run 2 approves it: the server call runs, and the client call goes out — the model not consulted.
        var run2 = await client.GetResponseAsync(
            [User(), .. run1.Messages, new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true)])],
            options);

        Assert.Equal(1, serverInvocations);
        Assert.Contains("c1", CallIds(run2.Messages));
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task UnderTheLoop_InAnAgUiRun_WithTheProviderPipelineBelow_TheClientCallIsReleased()
    {
        // As Program.cs composes it: below this middleware, the Bedrock pipeline strips RunAgentInput off the
        // options before the provider call. It must do so on a copy — the loop hands the same options to its
        // next round-trip, where the thread id keys the held calls.
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new FunctionInvokingChatClient(new ClientToolDeferralMiddleware(
            new OmitAdditionalPropertiesMiddleware(model, [typeof(RunAgentInput)])));

        var updates = await StreamAsync(client, [User()], AgUiOptions("t1"));

        Assert.Equal(["s1", "c1"], CallIds(updates));
        Assert.Contains("s1", updates.SelectMany(u => u.Contents).OfType<FunctionResultContent>().Select(r => r.CallId));
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task UnderTheLoop_RejectedServerCall_TheClientCallIsStillReleased()
    {
        // The held calls are requests in their own right: rejecting the gated call does not cancel them.
        var serverInvocations = 0;
        var gated = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create(() => { serverInvocations++; return "12:00"; }, "get_time"));
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new FunctionInvokingChatClient(new ClientToolDeferralMiddleware(model));
        var options = Options(gated, ClientTool);
        var run1 = await client.GetResponseAsync([User()], options);
        var request = Assert.Single(run1.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>());

        var run2 = await client.GetResponseAsync(
            [User(), .. run1.Messages, new ChatMessage(ChatRole.User, [request.CreateResponse(approved: false)])],
            options);

        Assert.Equal(0, serverInvocations);
        Assert.Contains("c1", CallIds(run2.Messages));
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task WithoutTheMiddleware_TheLoopRunsNoneOfAMixedTurnsCalls()
    {
        // The failure being fixed: the server call is handed back unanswered alongside the client call.
        var serverInvocations = 0;
        var serverTool = AIFunctionFactory.Create(() => { serverInvocations++; return "12:00"; }, "get_time");
        using var client = new FunctionInvokingChatClient(
            new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1"))));

        var updates = await StreamAsync(client, [User()], Options(serverTool, ClientTool));

        Assert.Equal(0, serverInvocations);
        Assert.Empty(updates.SelectMany(u => u.Contents).OfType<FunctionResultContent>());
    }

    // ---------------------------------------------------------------------------
    // Putting the split turn back together for the model
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AfterTheClientResults_TheModelSeesTheSplitTurnAsOne()
    {
        // Seen as two rounds, a prompt that demanded one parallel step made the model take the second
        // round for a failed attempt and call both tools again.
        var (model, client, options) = await SplitAndReleaseAsync();
        using var disposable = client;

        await StreamAsync(client, AfterClientResults(), options);

        Assert.Equal(["user", "assistant:s1+c1", "tool:s1+c1"], Describe(model.LastMessages));
    }

    [Fact]
    public async Task TheMergedTurn_KeepsReasoningAndText_AndTheModelsCallOrder()
    {
        // The model called the client tool first. Its reasoning and text stay on the assistant message,
        // ahead of the calls, as the model produced them.
        var model = new ScriptedChatClient(Turn("msg_1", ClientCall("c1"), ServerCall("s1")));
        using var client = new ClientToolDeferralMiddleware(model);
        var options = Options();
        var serverTurn = Assistant(new TextReasoningContent("thinking"), new TextContent("Sure."), ServerCall("s1"));
        await StreamAsync(client, [User()], options);
        await StreamAsync(client, [User(), serverTurn, ToolResult("s1")], options);

        await StreamAsync(client, [User(), serverTurn, ToolResult("s1"), Assistant(ClientCall("c1")), ToolResult("c1")], options);

        var assistant = model.LastMessages[1];
        Assert.IsType<TextReasoningContent>(assistant.Contents[0]);
        Assert.IsType<TextContent>(assistant.Contents[1]);
        Assert.Equal(["c1", "s1"], assistant.Contents.OfType<FunctionCallContent>().Select(c => c.CallId));
        Assert.Equal(["c1", "s1"], model.LastMessages[2].Contents.OfType<FunctionResultContent>().Select(r => r.CallId));
    }

    [Fact]
    public async Task NonStreaming_TheMergedTurn_KeepsTheModelsCallOrder()
    {
        var model = new ScriptedChatClient(Turn("msg_1", ClientCall("c1"), ServerCall("s1")));
        using var client = new ClientToolDeferralMiddleware(model);
        var options = Options();
        await client.GetResponseAsync([User()], options);
        await client.GetResponseAsync([User(), Assistant(ServerCall("s1")), ToolResult("s1")], options);

        await client.GetResponseAsync(AfterClientResults(), options);

        Assert.Equal(["user", "assistant:c1+s1", "tool:c1+s1"], Describe(model.LastMessages));
    }

    [Fact]
    public async Task TheSplitTurn_StaysMerged_InLaterRequests()
    {
        var (model, client, options) = await SplitAndReleaseAsync();
        using var disposable = client;

        await StreamAsync(client, [.. AfterClientResults(), Assistant(new TextContent("Done.")), User()], options);

        Assert.Equal(["user", "assistant:s1+c1", "tool:s1+c1", "assistant:", "user"], Describe(model.LastMessages));
    }

    [Fact]
    public async Task TwoSplitTurnsInOneHistory_AreBothMerged()
    {
        var model = new ScriptedChatClient(
            Turn("msg_1", ServerCall("s1"), ClientCall("c1")),
            Turn("msg_2", ServerCall("s2"), ClientCall("c2")));
        using var client = new ClientToolDeferralMiddleware(model);
        var options = Options();
        List<ChatMessage> firstTurn = [User(), Assistant(ServerCall("s1")), ToolResult("s1"), Assistant(ClientCall("c1")), ToolResult("c1")];
        await StreamAsync(client, [User()], options);
        await StreamAsync(client, firstTurn[..3], options);
        List<ChatMessage> secondStart = [.. firstTurn, User()];
        await StreamAsync(client, secondStart, options);
        await StreamAsync(client, [.. secondStart, Assistant(ServerCall("s2")), ToolResult("s2")], options);

        await StreamAsync(client,
            [.. secondStart, Assistant(ServerCall("s2")), ToolResult("s2"), Assistant(ClientCall("c2")), ToolResult("c2")],
            options);

        Assert.Equal(
            ["user", "assistant:s1+c1", "tool:s1+c1", "user", "assistant:s2+c2", "tool:s2+c2"],
            Describe(model.LastMessages));
    }

    [Fact]
    public async Task ASplitTurnWhoseRunCallsSpanSeveralAssistantMessages_IsMergedIntoOne()
    {
        // Approvals answered one at a time make the loop rebuild the gated calls as separate messages.
        var model = new ScriptedChatClient(
            Turn("msg_1", ServerCall("s1"), ServerCall("s2", OtherServerTool.Name), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model);
        var options = Options();
        List<ChatMessage> serverRound =
        [
            User(), Assistant(new TextContent("Sure."), ServerCall("s1")), Assistant(ServerCall("s2", OtherServerTool.Name)),
            ToolResult("s1"), ToolResult("s2"),
        ];
        await StreamAsync(client, [User()], options);
        await StreamAsync(client, serverRound, options);

        await StreamAsync(client, [.. serverRound, Assistant(ClientCall("c1")), ToolResult("c1")], options);

        Assert.Equal(["user", "assistant:s1+s2+c1", "tool:s1+s2+c1"], Describe(model.LastMessages));
        Assert.Single(model.LastMessages[1].Contents.OfType<TextContent>());
    }

    [Fact]
    public async Task TheRecordOfASplit_Expires_AfterTheRetentionPeriodWithoutUse()
    {
        // Then the turn reaches the model as two rounds again, as after a restart.
        var time = new ManualTimeProvider();
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model, TimeSpan.FromMinutes(10), time);
        var options = Options();
        await StreamAsync(client, [User()], options);
        await StreamAsync(client, [User(), Assistant(ServerCall("s1")), ToolResult("s1")], options);

        time.Advance(TimeSpan.FromMinutes(11));
        await StreamAsync(client, AfterClientResults(), options);

        Assert.Equal(Describe(AfterClientResults()), Describe(model.LastMessages));
    }

    [Fact]
    public async Task TheRecordOfASplit_Retention_IsSliding()
    {
        // A thread in use keeps its merge, however long ago the split was.
        var time = new ManualTimeProvider();
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall("s1"), ClientCall("c1")));
        using var client = new ClientToolDeferralMiddleware(model, TimeSpan.FromMinutes(10), time);
        var options = Options();
        await StreamAsync(client, [User()], options);
        await StreamAsync(client, [User(), Assistant(ServerCall("s1")), ToolResult("s1")], options);

        time.Advance(TimeSpan.FromMinutes(8));
        await StreamAsync(client, AfterClientResults(), options);
        time.Advance(TimeSpan.FromMinutes(8));
        await StreamAsync(client, AfterClientResults(), options);

        Assert.Equal(["user", "assistant:s1+c1", "tool:s1+c1"], Describe(model.LastMessages));
    }

    [Fact]
    public async Task TheMerge_HappensInTransitOnly_TheCallersHistoryIsUntouched()
    {
        // This client sits below the function-invocation loop; the list it is handed is what the loop
        // (and so the stored history) keeps.
        var (_, client, options) = await SplitAndReleaseAsync();
        using var disposable = client;
        var history = AfterClientResults();
        var before = Describe(history);

        await StreamAsync(client, history, options);

        Assert.Equal(before, Describe(history));
        Assert.Equal(["s1"], history[1].Contents.OfType<FunctionCallContent>().Select(c => c.CallId));
    }

    [Fact]
    public async Task TheMergedMessages_CarryNoRawRepresentation()
    {
        // The raw object of either half is the provider's form of that half alone; an adapter sending it
        // as-is would send the unmerged turn.
        var (model, client, options) = await SplitAndReleaseAsync();
        using var disposable = client;
        var history = AfterClientResults();
        history[1].RawRepresentation = "server half";
        history[2].RawRepresentation = "server results";

        await StreamAsync(client, history, options);

        Assert.Null(model.LastMessages[1].RawRepresentation);
        Assert.Null(model.LastMessages[2].RawRepresentation);
    }

    [Fact]
    public async Task ATurnThatWasNotSplit_IsNotMerged()
    {
        // Client calls the model made on their own, in a turn after a server call, look the same as a
        // released split — only the record of the split tells them apart. An unrelated split is recorded
        // first, so the record is consulted rather than skipped.
        var (model, client, options) = await SplitAndReleaseAsync("s9", "c9");
        using var disposable = client;

        await StreamAsync(client, AfterClientResults(), options);

        Assert.Equal(Describe(AfterClientResults()), Describe(model.LastMessages));
    }

    [Fact]
    public async Task ASplitTurnWithAnythingElseInBetween_IsLeftAsItIs()
    {
        // Only the exact shape the split produces is rewritten; anything else would be guesswork.
        var (model, client, options) = await SplitAndReleaseAsync();
        using var disposable = client;
        List<ChatMessage> history =
        [
            User(), Assistant(ServerCall("s1")), ToolResult("s1"), User(),
            Assistant(ClientCall("c1")), ToolResult("c1"),
        ];

        await StreamAsync(client, history, options);

        Assert.Equal(Describe(history), Describe(model.LastMessages));
    }

    [Fact]
    public async Task ASplitTurnWithAMessageBetweenItsCallsAndTheirResults_IsLeftAsItIs()
    {
        // Merging would delete that message.
        var (model, client, options) = await SplitAndReleaseAsync();
        using var disposable = client;
        List<ChatMessage> history =
        [
            User(), Assistant(ServerCall("s1")), User(), ToolResult("s1"),
            Assistant(ClientCall("c1")), ToolResult("c1"),
        ];

        await StreamAsync(client, history, options);

        Assert.Equal(Describe(history), Describe(model.LastMessages));
    }

    [Fact]
    public async Task AReleasedMessageWithMoreThanTheClientCalls_IsLeftAsItIs()
    {
        var (model, client, options) = await SplitAndReleaseAsync();
        using var disposable = client;
        List<ChatMessage> history =
        [
            User(), Assistant(ServerCall("s1")), ToolResult("s1"),
            Assistant(new TextContent("Also:"), ClientCall("c1")), ToolResult("c1"),
        ];

        await StreamAsync(client, history, options);

        Assert.Equal(Describe(history), Describe(model.LastMessages));
    }

    [Fact]
    public async Task ASplitTurnMissingItsServerResult_IsLeftAsItIs()
    {
        var (model, client, options) = await SplitAndReleaseAsync();
        using var disposable = client;
        List<ChatMessage> history = [User(), Assistant(ServerCall("s1")), Assistant(ClientCall("c1")), ToolResult("c1")];

        await StreamAsync(client, history, options);

        Assert.Equal(Describe(history), Describe(model.LastMessages));
    }

    [Fact]
    public async Task AHistoryStartingWithResults_IsLeftAsItIs()
    {
        // E.g. a history reducer cut the turn's beginning off.
        var (model, client, options) = await SplitAndReleaseAsync();
        using var disposable = client;
        List<ChatMessage> history = [ToolResult("s1"), Assistant(ClientCall("c1")), ToolResult("c1")];

        await StreamAsync(client, history, options);

        Assert.Equal(Describe(history), Describe(model.LastMessages));
    }

    [Fact]
    public async Task ADuplicatedResult_IsLeftAsItIs()
    {
        var (model, client, options) = await SplitAndReleaseAsync();
        using var disposable = client;
        List<ChatMessage> history = [.. AfterClientResults(), ToolResult("c1")];

        await StreamAsync(client, history, options);

        Assert.Equal(Describe(history), Describe(model.LastMessages));
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static readonly AIFunction ServerTool = AIFunctionFactory.Create(() => "12:00", "get_time");

    private static readonly AIFunction OtherServerTool = AIFunctionFactory.Create(() => "ok", "get_weather");

    private static readonly AIFunctionDeclaration ClientTool = AIFunctionFactory.CreateDeclaration(
        "add_todo", "Adds a todo.", JsonSerializer.SerializeToElement(new { type = "object" }));

    /// <summary>Plain chat options, outside an AG-UI request: the bare declaration is the client tool.</summary>
    private static ChatOptions Options(params AITool[] tools) =>
        new() { Tools = tools.Length > 0 ? [.. tools] : [ServerTool, OtherServerTool, ClientTool] };

    /// <summary>
    /// Chat options as the AG-UI SDK builds them for a run of <paramref name="threadId"/> declaring the client
    /// tool, plus the server tool. With <paramref name="priorClientCall"/>, the run's history holds an answered
    /// call to the client tool, so the SDK installs it as an approval proxy.
    /// </summary>
    private static ChatOptions AgUiOptions(string threadId, string? priorClientCall = null)
    {
        List<AGUIMessage> messages = [new AGUIUserMessage { Id = "u1", Content = "hi" }];
        if (priorClientCall is not null)
        {
            messages.Add(new AGUIAssistantMessage
            {
                Id = "a1",
                ToolCalls =
                [
                    new AGUIToolCall
                    {
                        Id = priorClientCall,
                        Function = new AGUIToolCallFunction { Name = ClientTool.Name, Arguments = "{}" },
                    }
                ],
            });
            messages.Add(new AGUIToolMessage { Id = "t1", ToolCallId = priorClientCall, Content = "done" });
            messages.Add(new AGUIUserMessage { Id = "u2", Content = "again" });
        }

        var options = new RunAgentInput
        {
            ThreadId = threadId,
            RunId = "r1",
            Messages = messages,
            Tools = [new AGUITool { Name = ClientTool.Name }],
        }.ToChatRequestContext(AguiJson.Options).ChatOptions;
        options.Tools = [.. options.Tools ?? [], ServerTool];
        return options;
    }

    /// <summary>Steps 1 and 2: splits a mixed turn and releases its client call.</summary>
    private static async Task<(ScriptedChatClient Model, ClientToolDeferralMiddleware Client, ChatOptions Options)> SplitAndReleaseAsync(
        string serverCallId = "s1", string clientCallId = "c1")
    {
        var model = new ScriptedChatClient(Turn("msg_1", ServerCall(serverCallId), ClientCall(clientCallId)));
        var client = new ClientToolDeferralMiddleware(model);
        var options = Options();
        await StreamAsync(client, [User()], options);
        await StreamAsync(client, [User(), Assistant(ServerCall(serverCallId)), ToolResult(serverCallId)], options);
        return (model, client, options);
    }

    /// <summary>The history of step 3: the split turn as two rounds, both answered.</summary>
    private static List<ChatMessage> AfterClientResults() =>
        [User(), Assistant(ServerCall("s1")), ToolResult("s1"), Assistant(ClientCall("c1")), ToolResult("c1")];

    private static FunctionCallContent ServerCall(string callId, string name = "get_time") => new(callId, name);

    private static FunctionCallContent ClientCall(string callId, string title = "milk") =>
        new(callId, ClientTool.Name, new Dictionary<string, object?> { ["title"] = title });

    private static ChatMessage User() => new(ChatRole.User, "hi");

    private static ChatMessage Assistant(params AIContent[] contents) => new(ChatRole.Assistant, [.. contents]);

    private static ChatMessage ToolResult(string callId) => new(ChatRole.Tool, [new FunctionResultContent(callId, "ok")]);

    /// <summary>One model turn: some text and the calls in a single update, with the finish reason.</summary>
    private static List<ChatResponseUpdate> Turn(string messageId, params FunctionCallContent[] calls) =>
    [
        new(ChatRole.Assistant, [new TextContent("Sure."), .. calls])
        {
            MessageId = messageId,
            ResponseId = "resp_1",
            ModelId = "model",
            FinishReason = ChatFinishReason.ToolCalls,
        },
    ];

    private static string Title(ChatResponseUpdate update) =>
        update.Contents.OfType<FunctionCallContent>().Single().Arguments!["title"]!.ToString()!;

    private static List<string> CallIds(IEnumerable<ChatResponseUpdate> updates) =>
        [.. updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().Select(c => c.CallId)];

    private static List<string> CallIds(IEnumerable<ChatMessage> messages) =>
        [.. messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId)];

    private static string Describe(ChatResponseUpdate update) =>
        update.Contents.FirstOrDefault() switch
        {
            FunctionCallContent call => $"call:{call.CallId}",
            TextContent text => text.Text,
            null when update.FinishReason is not null => "finish",
            var other => other?.GetType().Name ?? "empty",
        };

    private static List<string> Describe(IEnumerable<ChatMessage> messages) =>
    [
        .. messages.Select(m => m.Role == ChatRole.Assistant
            ? $"assistant:{string.Join('+', m.Contents.OfType<FunctionCallContent>().Select(c => c.CallId))}"
            : m.Role == ChatRole.Tool
                ? $"tool:{string.Join('+', m.Contents.OfType<FunctionResultContent>().Select(r => r.CallId))}"
                : m.Role.Value),
    ];

    private static async Task<List<ChatResponseUpdate>> StreamAsync(IChatClient client, List<ChatMessage> messages, ChatOptions options)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (var update in client.GetStreamingResponseAsync(messages, options))
        {
            updates.Add(update);
        }

        return updates;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    /// <summary>
    /// Plays the scripted model turns in order, one per call; once they are used up, every call answers with
    /// plain text, so a loop that asks the model again terminates. Records what reached it.
    /// </summary>
    private sealed class ScriptedChatClient(params List<ChatResponseUpdate>[] turns) : IChatClient
    {
        public int Calls { get; private set; }

        /// <summary>The messages of the latest call that reached the model.</summary>
        public List<ChatMessage> LastMessages { get; private set; } = [];

        /// <summary>The messages of the latest non-streaming response, as the model returned them.</summary>
        public List<ChatMessage> LastResponseMessages { get; private set; } = [];

        /// <summary>Throws after streaming the first scripted turn, as a dropped connection would.</summary>
        public bool FailFirstCall { get; init; }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                updates.Add(update);
            }

            var response = updates.ToChatResponse();
            LastResponseMessages = [.. response.Messages];
            return response;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            LastMessages = [.. messages];
            var turn = Calls < turns.Length
                ? turns[Calls]
                : [new ChatResponseUpdate(ChatRole.Assistant, "Done.") { MessageId = $"msg_done_{Calls}" }];
            Calls++;
            foreach (var update in turn)
            {
                yield return update;
            }

            if (FailFirstCall && Calls == 1)
            {
                throw new InvalidOperationException("connection dropped");
            }
        }
    }
}
