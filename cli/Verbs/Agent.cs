using System.Text.Json;
using CommandLine;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace STP.LegalTwin.Cli.Verbs
{
    /// <summary>
    /// Taken from https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/AGUIClientServer/AGUIClient/Program.cs
    /// </summary>
    [Verb("agent", HelpText = "Invoke the agent.")]
    class Agent
    {
        public async Task Do(ILogger<Agent> logger)
        {
            var cancellationToken = CancellationToken.None;
            var serverUrl = "http://localhost:5288/agui";
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

            AIAgent agent = chatClient.CreateAIAgent(
                name: "agui-client",
                description: "AG-UI Client Agent",
                tools: [changeBackground]);

            AgentThread thread = agent.GetNewThread();
            List<ChatMessage> messages = [new(ChatRole.System, "You are a helpful assistant.")];
            try
            {
                while (true)
                {
                    // Get user message
                    Console.Write("\nUser (:q or quit to exit): ");
                    string? message = Console.ReadLine();
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

                    // Call RunStreamingAsync to get streaming updates
                    bool isFirstUpdate = true;
                    string? threadId = null;
                    var updates = new List<ChatResponseUpdate>();
                    await foreach (AgentRunResponseUpdate update in agent.RunStreamingAsync(messages, thread, cancellationToken: cancellationToken))
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
                    //messages.Clear();
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
