using System.Runtime.CompilerServices;
using System.Text.Json;
using CommandLine;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
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

        [Option("state", HelpText = "Initial state as JSON, e.g. '{\"conversation\":{\"selectedResources\":[\"a.txt\"],\"metadata\":{}}}'")]
        public string? State { get; set; }

        public async Task Do(ILogger<Agent> logger)
        {
            var cancellationToken = CancellationToken.None;
            var serverUrl =
                // "http://localhost:5288/agents/static/amazonbedrock/agui" // no session management
                // "http://localhost:5288/agents/static/openai/agui" // no session management
                "http://localhost:5288/agents/routed/amazonbedrock/agui"
                ;
            logger.LogInformation("Connecting to AG-UI server at: {ServerUrl}", serverUrl);

            // Create the AG-UI client agent
            using HttpClient httpClient = new()
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            IChatClient chatClient = new AGUIChatClient(
                httpClient,
                serverUrl)
                .AsBuilder()
                .Build()
                ;

            JsonElement? currentState = State is not null ? JsonSerializer.Deserialize<JsonElement>(State) : null;
            async IAsyncEnumerable<AgentResponseUpdate> StateInjectionMiddleware(
                IEnumerable<ChatMessage> messages,
                AgentSession? session,
                AgentRunOptions? options,
                AIAgent innerAgent,
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                if (currentState != null)
                {
                    var stateMessage = new ChatMessage(ChatRole.System, [new DataContent(JsonSerializer.SerializeToUtf8Bytes(currentState), "application/json")]);
                    messages = messages.Append(stateMessage);
                }
                await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
                {
                    var stateSnapshots = update.Contents.OfType<DataContent>().Where(c => c.MediaType == "application/json");
                    if (stateSnapshots.Any())
                    {
                        foreach (var dataContent in stateSnapshots)
                        {
                            var newState = JsonSerializer.Deserialize<JsonElement>(dataContent.Data.Span);
                            AnsiConsole.Markup($"\n[dim]{Markup.Escape("[State: ")}[/]");
                            AnsiConsole.Write(new JsonText(newState.ToString()));
                            AnsiConsole.Markup($"[dim]{Markup.Escape("]")}[/]");
                            currentState = newState; // If there are multiple state snapshots, take the last one as the current state
                        }
                    }
                    else
                    {
                        yield return update;
                    }
                }
            }

            var changeBackground = AIFunctionFactory.Create(
                () =>
                {
                    Console.ForegroundColor = ConsoleColor.DarkBlue;
                    Console.WriteLine("Changing color to blue");
                },
                name: "change_background_color",
                description: "Change the console background color to dark blue."
            );

            AIAgent agent = chatClient.AsAIAgent(
                name: "agui-client",
                description: "AG-UI Client Agent",
                tools: [changeBackground])
                .AsBuilder()
                .Use(runFunc: null, runStreamingFunc: StateInjectionMiddleware)
                .Build()
                ;

            AgentSession thread = await agent.CreateSessionAsync();
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

                    // Human-in-the-loop: the backend surfaces approval-gated tools as synthetic
                    // "request_approval" client tool calls (see human-in-the-loop.md). Collect them,
                    // prompt the user, answer each with a tool result and re-run until none remain.
                    var pendingApprovals = new List<(string CallId, IDictionary<string, object?>? Arguments)>();
                    do
                    {
                        pendingApprovals.Clear();

                        var runOptions = new ChatClientAgentRunOptions();
                        bool isFirstUpdate = true;
                        string? threadId = null;
                        var updates = new List<ChatResponseUpdate>();
                        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, thread, runOptions, cancellationToken: cancellationToken))
                        {
                            // Use AsChatResponseUpdate to access ChatResponseUpdate properties
                            ChatResponseUpdate chatUpdate = update.AsChatResponseUpdate();
                            updates.Add(chatUpdate);

                            if (chatUpdate.ConversationId != null)
                            {
                                threadId = chatUpdate.ConversationId;
                            }

                            // Display run started information from the first update
                            if (isFirstUpdate && threadId != null && chatUpdate.ResponseId != null)
                            {
                                AnsiConsole.MarkupLine($"\n[dim]{Markup.Escape($"[Run Started - Thread: {threadId}, Run: {chatUpdate.ResponseId}]")}[/]");
                                isFirstUpdate = false;
                            }

                            // Display different content types with appropriate formatting
                            foreach (AIContent content in chatUpdate.Contents)
                            {
                                switch (content)
                                {
                                    case TextContent textContent:
                                        AnsiConsole.Markup($"[cyan]{Markup.Escape(textContent.Text)}[/]");
                                        break;

                                    case FunctionCallContent { Name: "request_approval" } approvalCall:
                                        pendingApprovals.Add((approvalCall.CallId, approvalCall.Arguments));
                                        break;

                                    case FunctionCallContent functionCallContent:
                                        AnsiConsole.MarkupLine($"\n[green]{Markup.Escape($"[Function Call - Name: {functionCallContent.Name}, Arguments: {JsonSerializer.Serialize(functionCallContent.Arguments)}]")}[/]");
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

                                    case ErrorContent errorContent:
                                        string code = errorContent.AdditionalProperties?["Code"] as string ?? "Unknown";
                                        AnsiConsole.MarkupLine($"\n[red]{Markup.Escape($"[Error - Code: {code}, Message: {errorContent.Message}]")}[/]");
                                        break;
                                }
                            }
                        }

                        if (updates.Count > 0 && !updates[^1].Contents.Any(c => c is TextContent))
                        {
                            var lastUpdate = updates[^1];
                            AnsiConsole.MarkupLine($"\n[dim]{Markup.Escape($"[Run Ended - Thread: {threadId}, Run: {lastUpdate.ResponseId}]")}[/]");
                        }

                        messages.Clear(); // server supports session management, only send new messages

                        foreach (var (callId, arguments) in pendingApprovals)
                        {
                            var (toolName, toolArgsJson) = DescribeApprovalRequest(arguments);
                            AnsiConsole.MarkupLine($"\n[yellow]{Markup.Escape($"[Approval required - Tool: {toolName}, Arguments: {toolArgsJson}]")}[/]");
                            var choice = AnsiConsole.Prompt(
                                new TextPrompt<string>("Approve? (y)es / (a)lways allow / (n)o:")
                                    .AddChoices(["y", "a", "n"])
                                    .DefaultValue("y"));

                            // Echo the request payload back, plus the decision — the backend bridge
                            // reconstructs the approval from it (and records an "always allow" rule
                            // for this conversation when requested).
                            var response = new Dictionary<string, object?>(arguments ?? new Dictionary<string, object?>())
                            {
                                ["approved"] = choice is "y" or "a",
                                ["reason"] = null,
                                ["always_approve"] = choice == "a" ? "tool" : null,
                            };
                            messages.Add(new(ChatRole.Tool, [new FunctionResultContent(callId, JsonSerializer.SerializeToElement(response))]));
                        }
                    } while (pendingApprovals.Count > 0);
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("AGUIClient operation was canceled.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not ThreadAbortException and not AccessViolationException)
            {
                logger.LogError(ex, "An error occurred while running the AGUIClient");
                return;
            }
        }

        /// <summary>
        /// Extracts the wrapped tool call's name and arguments from a <c>request_approval</c>
        /// payload (<c>{ "id": ..., "tool_call": { "id", "name", "arguments" } }</c>) for display.
        /// </summary>
        private static (string ToolName, string ArgumentsJson) DescribeApprovalRequest(IDictionary<string, object?>? arguments)
        {
            if (arguments?.TryGetValue("tool_call", out var toolCallObj) == true)
            {
                var toolCall = toolCallObj switch
                {
                    JsonElement el => el,
                    not null => JsonSerializer.SerializeToElement(toolCallObj),
                    _ => default,
                };
                if (toolCall.ValueKind == JsonValueKind.Object)
                {
                    var name = toolCall.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    var args = toolCall.TryGetProperty("arguments", out var argsEl) ? argsEl.GetRawText() : "{}";
                    return (name ?? "unknown tool", args);
                }
            }
            return ("unknown tool", "{}");
        }
    }
}
