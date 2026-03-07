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
    /// <summary>
    /// Taken from https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/AGUIClientServer/AGUIClient/Program.cs
    /// </summary>
    [Verb("agent", HelpText = "Invoke the agent.")]
    class Agent
    {
        [Value(0, MetaName = "prompt", HelpText = "Initial prompt for the agent", Required = true)]
        public string? Prompt { get; set; }

        [Option("state", HelpText = "Initial state as JSON, e.g. '{\"conversation\":{\"selectedResources\":[\"a.txt\"],\"metadata\":{}}}'")]
        public string? State { get; set; }

        public async Task Do(ILogger<Agent> logger)
        {
            var cancellationToken = CancellationToken.None;
            var serverUrl =
                "http://localhost:5288/agents/static/amazonbedrock/agui"
                // "http://localhost:5288/agents/static/openai/agui"
                ;
            logger.LogInformation("Connecting to AG-UI server at: {ServerUrl}", serverUrl);

            // Create the AG-UI client agent
            using HttpClient httpClient = new()
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            var changeBackground = AIFunctionFactory.Create(
                () =>
                {
                    Console.ForegroundColor = ConsoleColor.DarkBlue;
                    Console.WriteLine("Changing color to blue");
                },
                name: "change_background_color",
                description: "Change the console background color to dark blue."
            );

            var chatClient = new AGUIChatClient(
                httpClient,
                serverUrl);

            AIAgent agent = chatClient.AsAIAgent(
                name: "agui-client",
                description: "AG-UI Client Agent",
                tools: [changeBackground]);

            AgentSession thread = await agent.CreateSessionAsync();
            List<ChatMessage> messages = [new(ChatRole.System, "You are a helpful assistant.")];
            string? firstUserMessage = Prompt;

            JsonElement? currentState = State is not null ? JsonSerializer.Deserialize<JsonElement>(State) : null;

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

                    // Include current state
                    ChatMessage? currentStateMessage = currentState is not null ? new(ChatRole.System, [new DataContent(JsonSerializer.SerializeToUtf8Bytes(currentState.Value), "application/json")]) : null;
                    if (currentStateMessage is not null)
                    {
                        messages.Add(currentStateMessage);
                    }

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
                        List<AIContent> omitContents = [];
                        foreach (AIContent content in chatUpdate.Contents)
                        {
                            switch (content)
                            {
                                case TextContent textContent:
                                    AnsiConsole.Markup($"[cyan]{Markup.Escape(textContent.Text)}[/]");
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

                                case DataContent { MediaType: "application/json" } dataContent when dataContent.Data is { } data:
                                    currentState = JsonSerializer.Deserialize<JsonElement>(data.Span);
                                    AnsiConsole.Markup($"\n[dim]{Markup.Escape("[State: ")}[/]");
                                    AnsiConsole.Write(new JsonText(currentState?.ToString() ?? "null"));
                                    AnsiConsole.Markup($"[dim]{Markup.Escape("]")}[/]");
                                    omitContents.Add(content); // Mark state snapshot content to be omitted from message history
                                    break;
                            }
                        }

                        omitContents.ForEach(c => chatUpdate.Contents.Remove(c));
                    }

                    if (updates.Count > 0 && !updates[^1].Contents.Any(c => c is TextContent))
                    {
                        var lastUpdate = updates[^1];
                        AnsiConsole.MarkupLine($"\n[dim]{Markup.Escape($"[Run Ended - Thread: {threadId}, Run: {lastUpdate.ResponseId}]")}[/]");
                    }

                    // Prepare messages for the next turn by removing state related snapshots
                    var chatResponse = updates.ToChatResponse();
                    var messagesWithContents = chatResponse.Messages.Where(m => m.Contents.Any());
                    messages.AddRange(messagesWithContents);
                    if (currentStateMessage is not null)
                    {
                        messages.Remove(currentStateMessage);
                    }
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
    }
}
