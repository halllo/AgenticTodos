using System.Text.Json;
using CommandLine;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

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
            // currentStateBytes holds the last STATE_SNAPSHOT bytes received from the server.
            // Per the AG-UI C# client docs, state is sent as DataContent("application/json") in
            // a ChatRole.System message, which the AG-UI hosting layer extracts into ag_ui_state.
            byte[]? currentStateBytes = State is not null
                ? JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<JsonElement>(State))
                : null;

            try
            {
                while (true)
                {
                    // Get user message
                    Console.Write("\nUser (:q or quit to exit): ");
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

                    // Per AG-UI C# client docs, state is sent as DataContent("application/json")
                    // in a ChatRole.System message. The hosting layer extracts it into ag_ui_state.
                    if (currentStateBytes is not null)
                        messages.Add(new(ChatRole.System, [new DataContent(currentStateBytes, "application/json")]));

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
                        if (isFirstUpdate && threadId != null && update.ResponseId != null)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"\n[Run Started - Thread: {threadId}, Run: {update.ResponseId}]");
                            Console.ResetColor();
                            isFirstUpdate = false;
                        }

                        // Display different content types with appropriate formatting
                        foreach (AIContent content in update.Contents)
                        {
                            //Console.WriteLine($"[Content {content.GetType().Name} received]");
                            switch (content)
                            {
                                case TextContent textContent:
                                    Console.ForegroundColor = ConsoleColor.Cyan;
                                    Console.Write(textContent.Text);
                                    Console.ResetColor();
                                    break;

                                case FunctionCallContent functionCallContent:
                                    Console.ForegroundColor = ConsoleColor.Green;
                                    Console.WriteLine($"\n[Function Call - Name: {functionCallContent.Name}, Arguments: {JsonSerializer.Serialize(functionCallContent.Arguments)}]");
                                    Console.ResetColor();
                                    break;

                                case FunctionResultContent functionResultContent:
                                    Console.ForegroundColor = ConsoleColor.Magenta;
                                    if (functionResultContent.Exception != null)
                                    {
                                        Console.WriteLine($"\n[Function Result - Exception: {functionResultContent.Exception}]");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"\n[Function Result - Result: {functionResultContent.Result}]");
                                    }
                                    Console.ResetColor();
                                    break;

                                case ErrorContent errorContent:
                                    Console.ForegroundColor = ConsoleColor.Red;
                                    string code = errorContent.AdditionalProperties?["Code"] as string ?? "Unknown";
                                    Console.WriteLine($"\n[Error - Code: {code}, Message: {errorContent.Message}]");
                                    Console.ResetColor();
                                    break;
                            }
                        }
                    }
                    if (updates.Count > 0 && !updates[^1].Contents.Any(c => c is TextContent))
                    {
                        var lastUpdate = updates[^1];
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine();
                        Console.WriteLine($"[Run Ended - Thread: {threadId}, Run: {lastUpdate.ResponseId}]");
                        Console.ResetColor();
                    }

                    // Capture STATE_SNAPSHOT from response updates.
                    // AGUIChatClient surfaces STATE_SNAPSHOT as DataContent("application/json").
                    foreach (var u in updates)
                    {
                        foreach (var content in u.Contents.OfType<DataContent>())
                        {
                            if (content.MediaType == "application/json" && content.Data is { } data)
                            {
                                currentStateBytes = data.ToArray();
                                var stateJson = JsonSerializer.Deserialize<JsonElement>(data.Span);
                                Console.ForegroundColor = ConsoleColor.DarkGray;
                                Console.WriteLine($"[State: {stateJson}]");
                                Console.ResetColor();
                            }
                        }
                    }

                    // Remove the ephemeral state system message so it isn't re-sent as history
                    messages.RemoveAll(m => m.Role == ChatRole.System
                        && m.Contents.Any(c => c is DataContent dc && dc.MediaType == "application/json"));

                    var chatResponse = updates.ToChatResponse();
                    messages.AddRange(chatResponse.Messages);
                    Console.WriteLine();
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
