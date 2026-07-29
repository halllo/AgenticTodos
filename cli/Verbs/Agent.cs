using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;
using CommandLine;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Json;

namespace AgenticTodos.Cli.Verbs
{
    [Verb("agent", HelpText = "Invoke the agent.")]
    class Agent
    {
        [Value(0, MetaName = "prompt", HelpText = "Initial prompt for the agent", Required = false)]
        public string? Prompt { get; set; }

        [Option("state", HelpText = "Initial state as JSON, e.g. '{\"conversation\":{\"selectedResources\":[\"a.txt\"],\"counter\":0}}'")]
        public string? State { get; set; }

        public async Task Do(ILogger<Agent> logger)
        {
            // Nothing here ever asks for cancellation: the REPL blocks in Console.ReadLine, which no
            // token can interrupt, so wiring one up would only affect the runs. That is why the
            // per-run handler below must treat an OperationCanceledException as a failed turn rather
            // than as a requested stop — the only thing that can raise one is HttpClient.Timeout.
            var cancellationToken = CancellationToken.None;
            var serverUrl =
                // "http://localhost:5288/agents/static/amazonbedrock/agui" // no session management
                // "http://localhost:5288/agents/static/openai/agui" // no session management
                "http://localhost:5288/agents/routed/amazonbedrock/agui"
                ;
            logger.LogInformation("Connecting to AG-UI server at: {ServerUrl}", serverUrl);

            // Create the AG-UI client agent.
            // The timeout caps the wait for the response *headers* only — AGUIHttpTransport sends with
            // HttpCompletionOption.ResponseHeadersRead, so a run whose deltas keep trickling in is
            // never cut off by it, however long it takes. Exceeding it raises TaskCanceledException.
            using HttpClient httpClient = new()
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            IChatClient chatClient = new AGUIChatClient(new AGUIChatClientOptions(httpClient, serverUrl));

            JsonElement? currentState = State is not null ? JsonSerializer.Deserialize<JsonElement>(State) : null;

            // Client-side tool, the CLI's counterpart to the frontend's WebMCP tools: it is declared to
            // the server, but only this process can run it.
            var changeBackground = AIFunctionFactory.Create(
                () =>
                {
                    // The background, because that is what the declaration below promises the model.
                    // Console.ForegroundColor would tint the REPL's own text instead — dark blue on a
                    // dark terminal, for the rest of the session.
                    Console.BackgroundColor = ConsoleColor.DarkBlue;
                    Console.WriteLine("Changing background color to dark blue");
                    return "Success: Background changed.";
                },
                name: "change_background_color",
                description: "Change the console background color to dark blue."
            );

            var clientTools = new Dictionary<string, AIFunction>(StringComparer.Ordinal)
            {
                [changeBackground.Name] = changeBackground,
            };

            AIAgent agent = chatClient.AsAIAgent(
                name: "agui-client",
                description: "AG-UI Client Agent",
                // Declarations rather than the functions themselves. A declaration-only tool is one no
                // FunctionInvokingChatClient in the pipeline can invoke, so a call to it is handed back
                // to us instead of being answered inside the run — which is what keeps the client from
                // issuing a second AG-UI request that re-sends this turn's messages (they would land in
                // the server's history twice). The tool still reaches the model: AGUIChatClient turns
                // ChatOptions.Tools into the request's `tools` array either way.
                // Same split as the frontend, where WebMCP tools travel in RunAgentParameters.tools and
                // the browser executes them once the run has finished.
                tools: [.. clientTools.Values.Select(AsDeclaration)]);

            // One AG-UI thread for the whole REPL. Without this the client mints a fresh thread id per
            // run (AGUIChatClient falls back to AGUIIdGenerator.NewThreadId() when neither the
            // RunAgentInput nor the options carry one), which starts a new server-side session every
            // turn — losing the history and the "always allow" rules recorded in it.
            // No AgentSession is passed to the runs on purpose: the server owns the history for this
            // thread, so replaying a client-side copy would duplicate every message.
            var threadId = Guid.NewGuid().ToString("N");
            List<ChatMessage> messages = [new(ChatRole.System, "You are a helpful assistant.")];
            string? firstUserMessage = Prompt;

            try
            {
                while (true)
                {
                    // Get user message
                    AnsiConsole.Markup("\n[dim]User:[/] ");
                    string? message = firstUserMessage ?? Console.ReadLine();
                    if (firstUserMessage != null)
                    {
                        Console.WriteLine(firstUserMessage);
                        firstUserMessage = null;
                    }
                    if (message is null) break;
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        Console.WriteLine("Request cannot be empty.");
                        continue;
                    }

                    if (message is ":q" or "quit")
                    {
                        break;
                    }

                    messages.Add(new(ChatRole.User, message));

                    // Work a finished run leaves for the client to resolve before the conversation can
                    // continue. Client-side tool calls are resolved by executing them, and their results
                    // travel back as tool messages; approval interrupts (see human-in-the-loop.md) are
                    // resolved by a user decision, which travels back as a resume entry. Both are
                    // collected during the run and answered after it — never mid-run — so each run sends
                    // only what is new, exactly like the frontend's pending-client-call list.
                    var pendingToolCalls = new List<FunctionCallContent>();
                    var pendingInterrupts = new List<InterruptRequestContent>();
                    var interruptResponses = new List<InterruptResponseContent>();
                    do
                    {
                        pendingToolCalls.Clear();
                        pendingInterrupts.Clear();
                        interruptResponses.Clear();

                        // The AG-UI client reads the run's thread and state off a RunAgentInput handed to
                        // it via RawRepresentationFactory. The `resume` entries come from the messages
                        // instead — see where the decisions are appended at the end of this loop.
                        var runOptions = new ChatClientAgentRunOptions
                        {
                            ChatOptions = new ChatOptions
                            {
                                RawRepresentationFactory = _ => new RunAgentInput
                                {
                                    ThreadId = threadId,
                                    State = currentState,
                                },
                            }
                        };
                        bool isFirstUpdate = true;
                        string? runId = null;
                        try
                        {
                            await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session: null, runOptions, cancellationToken: cancellationToken))
                            {
                                // Use AsChatResponseUpdate to access ChatResponseUpdate properties
                                ChatResponseUpdate chatUpdate = update.AsChatResponseUpdate();

                                // Display run started information from the first update. Every update of
                                // a run carries the same ResponseId — EventStreamConverter sets it once
                                // from RUN_STARTED.runId — so remembering it here is enough to name the
                                // run again when the stream ends.
                                if (isFirstUpdate && chatUpdate.ResponseId != null)
                                {
                                    runId = chatUpdate.ResponseId;
                                    AnsiConsole.MarkupLine($"\n[dim]{Markup.Escape($"[Run Started - Thread: {threadId}, Run: {runId}]")}[/]");
                                    isFirstUpdate = false;
                                }

                                // Events the protocol models but Microsoft.Extensions.AI has no content for
                                // (state, activities, …) arrive as the update's raw representation.
                                if (chatUpdate.RawRepresentation is StateSnapshotEvent stateSnapshot)
                                {
                                    AnsiConsole.Markup($"\n[dim]{Markup.Escape("[State: ")}[/]");
                                    AnsiConsole.Write(new JsonText(stateSnapshot.Snapshot.ToString()));
                                    AnsiConsole.Markup($"[dim]{Markup.Escape("]")}[/]");
                                    currentState = stateSnapshot.Snapshot;
                                    continue;
                                }

                                // The two activity kinds the backend maps from its own content types
                                // (see custom-agui-events.md). The frontend renders them as cards; here
                                // they are printed inline.
                                if (chatUpdate.RawRepresentation is ActivitySnapshotEvent activity)
                                {
                                    var label = activity.ActivityType switch
                                    {
                                        "eu-ai-act-risk" => "EU AI Act risk",
                                        "mcp-apps" => "MCP app",
                                        _ => activity.ActivityType,
                                    };
                                    AnsiConsole.Markup($"\n[dim]{Markup.Escape($"[{label}: ")}[/]");
                                    AnsiConsole.Write(new JsonText(activity.Content.ToString()));
                                    AnsiConsole.Markup($"[dim]{Markup.Escape("]")}[/]");
                                    continue;
                                }

                                // Display different content types with appropriate formatting
                                foreach (AIContent content in chatUpdate.Contents)
                                {
                                    switch (content)
                                    {
                                        case TextContent textContent:
                                            AnsiConsole.Markup($"[cyan]{Markup.Escape(textContent.Text)}[/]");
                                            break;

                                        // Extended thinking. The endpoint above is the only agent with
                                        // reasoning turned on (backend/Program.cs), and EventStreamConverter
                                        // maps REASONING_MESSAGE_CONTENT to TextReasoningContent(delta) and
                                        // REASONING_ENCRYPTED_VALUE to a TextReasoningContent carrying only
                                        // ProtectedData. The latter is the provider's signature over the
                                        // thought, not text for a human — and Text is empty for it (the
                                        // property substitutes string.Empty for a null value), so an empty
                                        // delta is skipped rather than printed. Grey keeps the thinking
                                        // visually apart from the answer's cyan, mirroring the frontend's
                                        // separate reasoning bubble.
                                        case TextReasoningContent reasoningContent:
                                            if (!string.IsNullOrEmpty(reasoningContent.Text))
                                            {
                                                AnsiConsole.Markup($"[grey]{Markup.Escape(reasoningContent.Text)}[/]");
                                            }
                                            break;

                                        case InterruptRequestContent interrupt:
                                            pendingInterrupts.Add(interrupt);
                                            break;

                                        case FunctionCallContent functionCallContent:
                                            AnsiConsole.MarkupLine($"\n[green]{Markup.Escape($"[Function Call - Name: {functionCallContent.Name}, Arguments: {JsonSerializer.Serialize(functionCallContent.Arguments)}]")}[/]");
                                            // A call to one of the tools this client declared is ours to
                                            // run, once the run has ended.
                                            if (clientTools.ContainsKey(functionCallContent.Name))
                                            {
                                                pendingToolCalls.Add(functionCallContent);
                                            }
                                            break;

                                        case FunctionResultContent functionResultContent:
                                            if (functionResultContent.Exception != null)
                                            {
                                                AnsiConsole.MarkupLine($"\n[magenta]{Markup.Escape($"[Function Result - Exception: {functionResultContent.Exception}]")}[/]");
                                            }
                                            else
                                            {
                                                AnsiConsole.MarkupLine($"\n[magenta]{Markup.Escape($"[Function Result - Result: {functionResultContent.Result}]")}[/]");
                                            }
                                            break;
                                    }
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                        {
                            // A RUN_ERROR event surfaces as a thrown exception (AGUIChatClient rethrows
                            // it as InvalidOperationException), so this is the only place the run's own
                            // failure can be observed. One bad turn must not end the REPL.
                            // The filter lets an OperationCanceledException through unless the token
                            // above actually asked for one: HttpClient.Timeout expiring raises
                            // TaskCanceledException, which is a failed turn and not a stop the user
                            // asked for. Excluding it outright would send it to the outer handler, which
                            // ends the session — a timeout would silently kill the REPL.
                            AnsiConsole.MarkupLine($"\n[red]{Markup.Escape($"[Run Failed - {ex.Message}]")}[/]");
                            logger.LogError(ex, "The agent run failed");
                            // A failed run invalidates the client calls it surfaced: its interrupts can
                            // never be answered, and a result for its tool calls must not be sent to a
                            // later run. The messages stay: a run that failed did not reach the server's
                            // history, so clearing them here would silently lose this turn (and, on the
                            // first turn, the system prompt) for the rest of the session — except the
                            // decisions this run carried, which are spent. Only the LAST message's
                            // interrupt responses become `resume` entries, so a leftover one would sit
                            // in front of the next turn's user message and be serialized as an ordinary
                            // user message whose text is the content's ToString() — observed on the wire
                            // as `{"role":"user","content":"AGUI.Abstractions.InterruptResponseContent"}`.
                            pendingToolCalls.Clear();
                            pendingInterrupts.Clear();
                            interruptResponses.Clear();
                            messages.RemoveAll(m => m.Contents.Any(c => c is InterruptResponseContent));
                            break;
                        }

                        AnsiConsole.MarkupLine($"\n[dim]{Markup.Escape($"[Run Ended - Thread: {threadId}, Run: {runId}]")}[/]");

                        messages.Clear(); // server owns the history for this thread, only send new messages

                        // Run the client-side calls now that the run has ended, and queue each result as a
                        // tool message for the next run — the only messages it carries, since the server
                        // holds the assistant message the call came from.
                        foreach (var call in pendingToolCalls)
                        {
                            object? result;
                            try
                            {
                                result = await clientTools[call.Name]
                                    .InvokeAsync(new AIFunctionArguments(call.Arguments), cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                logger.LogError(ex, "Client-side tool {ToolName} failed", call.Name);
                                result = "Error: Tool execution failed.";
                            }

                            var resultText = ToToolResultText(result);
                            AnsiConsole.MarkupLine($"\n[magenta]{Markup.Escape($"[Function Result - Result: {resultText}]")}[/]");
                            messages.Add(new(ChatRole.Tool, [new FunctionResultContent(call.CallId, resultText)]));
                        }

                        // An interrupt this client cannot answer is reported and dropped, which is where
                        // the CLI diverges from the frontend: there, declineInterrupt answers it with a
                        // `{ status: "cancelled" }` resume entry, because the TypeScript client keeps its
                        // own ledger of open interrupts and AbstractAgent.onInitialize rejects the next
                        // run over anything left in it. AGUI.Client keeps no such ledger — the resume
                        // array is built purely from the InterruptResponseContent found on the last
                        // message — so here the next turn goes out normally, and the request that was
                        // never answered is closed server-side on this thread's next run by
                        // ToolApprovalHistoryNormalizer's third repair (a synthetic rejection, so the
                        // gated call is refused rather than left to block every later turn). Dropping
                        // without a response is also what lets this do-while end: it repeats only while
                        // there is something to send.
                        foreach (var interrupt in pendingInterrupts)
                        {
                            // Only a `confirmation` interrupt is an approval. The backend picks that
                            // reason deliberately over `tool_call` for a gated call
                            // (backend/ToolApprovalInterruptMiddleware.cs), and InterruptReasons also
                            // defines `input_required` — a request for data, not a decision. Answering
                            // one of those with the payload below would be silently wrong: the server
                            // SDK's TryDecodeToolApprovalResume turns ANY resume payload carrying a
                            // `toolCall` into an approval request/response pair, whatever the interrupt
                            // asked for, so a mismatched reason produces an approval nobody requested
                            // rather than an error. Report and skip instead, like the missing-`toolCall`
                            // case below.
                            if (interrupt.Reason != InterruptReasons.Confirmation)
                            {
                                AnsiConsole.MarkupLine($"\n[red]{Markup.Escape($"[Interrupt {interrupt.RequestId} has an unsupported reason ({interrupt.Reason ?? "none"}) - cannot be answered]")}[/]");
                                logger.LogError("Interrupt {RequestId} has unsupported reason {Reason}", interrupt.RequestId, interrupt.Reason);
                                continue;
                            }

                            if (GetInterruptToolCall(interrupt) is not JsonElement toolCall)
                            {
                                // Nothing to approve and nothing the server could act on: the AG-UI
                                // server SDK rebuilds the approval request/response pair only from a
                                // resume payload whose `toolCall` deserializes to a non-null
                                // AGUIToolCallInfo (TryDecodeToolApprovalResume bails out otherwise),
                                // and the schema the backend advertises makes both `approved` and
                                // `toolCall` required. A payload with `toolCall: null` would degrade to
                                // a plain InterruptResponseContent that no part of the pipeline maps to
                                // an approval, leaving the gated call unanswered, so the interrupt is
                                // reported and dropped instead.
                                AnsiConsole.MarkupLine($"\n[red]{Markup.Escape($"[Approval request {interrupt.RequestId} arrived without the tool call it refers to - cannot be answered]")}[/]");
                                logger.LogError("Approval interrupt {RequestId} has no usable toolCall in its metadata", interrupt.RequestId);
                                continue;
                            }

                            // Same vocabulary as the frontend's approval card: tool name, then arguments.
                            // GetInterruptToolCall has already established that `name` is a JSON string,
                            // so this cannot be the empty label the user would have nothing to judge by.
                            var toolName = toolCall.GetProperty("name").GetString()!;
                            AnsiConsole.MarkupLine($"\n[yellow]{Markup.Escape($"[Approval required - {interrupt.Message ?? interrupt.Reason}]")}[/]");
                            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape($"  Tool: {toolName}")}[/]");
                            if (toolCall.TryGetProperty("arguments", out var argsEl))
                            {
                                AnsiConsole.Markup("[yellow]  Arguments: [/]");
                                AnsiConsole.Write(new JsonText(argsEl.ToString()));
                                AnsiConsole.WriteLine();
                            }
                            // Choices are matched case-insensitively (Spectre's default comparer), so the
                            // two "always" scopes need distinct letters rather than a/A.
                            var choice = AnsiConsole.Prompt(
                                new TextPrompt<string>("Approve? (y)es / (a)lways this tool / (s)ame arguments always / (n)o:")
                                    .AddChoices(["y", "a", "s", "n"])
                                    .DefaultValue("y"));

                            // Echo the tool call from the interrupt's metadata back, plus the decision —
                            // the backend rebuilds the approval from it (and records an "always allow"
                            // rule for this conversation when requested).
                            interruptResponses.Add(new InterruptResponseContent(interrupt.RequestId)
                            {
                                Payload = JsonSerializer.SerializeToElement(new
                                {
                                    toolCall,
                                    approved = choice is "y" or "a" or "s",
                                    alwaysApprove = choice switch
                                    {
                                        "a" => "tool",
                                        "s" => "tool_with_arguments",
                                        _ => null,
                                    },
                                }),
                            });
                        }

                        if (interruptResponses.Count > 0)
                        {
                            // The supported handover: AGUIChatClient.GetStreamingResponseAsync scans the
                            // LAST message for InterruptResponseContent, moves what it finds onto a
                            // cloned ChatOptions and strips the message before sending, which is what
                            // turns the decisions into the request's `resume` array. Doing it here rather
                            // than writing AGUIClientInternalKeys.InterruptResponses into
                            // ChatOptions.AdditionalProperties ourselves keeps the CLI off an internal
                            // key — one that is not in the package's public surface, so renaming it would
                            // make resume silently stop working, with no compile or runtime error.
                            // Position matters: only the last message is scanned, so this has to go after
                            // the tool result messages above.
                            messages.Add(new(ChatRole.User, [.. interruptResponses]));
                        }
                    } while (interruptResponses.Count > 0 || pendingToolCalls.Count > 0);
                }
            }
            catch (OperationCanceledException)
            {
                // Reserved for a cancellation that was actually asked for, which is why it may end the
                // session quietly. Nothing reaches it today: a run's OperationCanceledException is
                // handled as a failed turn above unless the token requested the stop, and this token
                // never does.
                logger.LogInformation("AGUIClient operation was canceled.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not ThreadAbortException and not AccessViolationException)
            {
                logger.LogError(ex, "An error occurred while running the AGUIClient");
                return;
            }
        }

        /// <summary>
        /// The wire half of a client-side tool: same name, description and schema, without the
        /// implementation. <c>FunctionInvokingChatClient</c> passes a call to a declaration-only tool back
        /// to the caller rather than invoking it, which is what makes the client the one that runs it.
        /// </summary>
        internal static AIFunctionDeclaration AsDeclaration(AIFunction function) =>
            AIFunctionFactory.CreateDeclaration(
                function.Name,
                function.Description,
                function.JsonSchema,
                function.ReturnJsonSchema);

        /// <summary>
        /// The text a client-side tool's result travels back as, mirroring the frontend's
        /// <c>typeof result === 'string' ? result : JSON.stringify(result)</c>. <c>AIFunctionFactory</c>
        /// marshals return values through JSON, so a plain string arrives here as a JSON string element —
        /// which would otherwise reach the model quoted.
        /// </summary>
        internal static string ToToolResultText(object? result) => result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            JsonElement element => element.GetRawText(),
            _ => JsonSerializer.Serialize(result),
        };

        /// <summary>
        /// The pending tool call the backend puts on an approval interrupt's metadata
        /// (<c>{ "toolCall": { "callId", "name", "arguments" } }</c>). It is displayed to the user and
        /// echoed back verbatim in the resume payload.
        /// <para>
        /// Validated as strictly as the frontend's <c>parseApprovalToolCall</c>: an object carrying
        /// string <c>callId</c> and <c>name</c>, or nothing. <c>TryGetProperty</c> alone would not do —
        /// it also succeeds for an explicit JSON <c>null</c> (<c>ValueKind == Null</c>), which the
        /// caller's <c>is not JsonElement</c> pattern cannot tell from a real call, so the user would be
        /// prompted and the decision then echoed back as <c>toolCall: null</c> — the payload the caller
        /// explains the server cannot rebuild an approval from. The two string checks are not cosmetic
        /// either: <c>AGUIToolCallInfo</c> declares both as nullable, so a <c>toolCall</c> missing them
        /// deserializes fine server-side and the approval is rebuilt around
        /// <c>FunctionCallContent(callId ?? "", name ?? "")</c> — an approval for a nameless call.
        /// </para>
        /// </summary>
        internal static JsonElement? GetInterruptToolCall(InterruptRequestContent interrupt)
            => interrupt.Metadata is { ValueKind: JsonValueKind.Object } metadata &&
               metadata.TryGetProperty("toolCall", out var toolCall) &&
               toolCall is { ValueKind: JsonValueKind.Object } &&
               toolCall.TryGetProperty("callId", out var callId) && callId.ValueKind == JsonValueKind.String &&
               toolCall.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? toolCall
                : null;
    }
}
