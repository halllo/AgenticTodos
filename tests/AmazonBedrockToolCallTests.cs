using System.Runtime.CompilerServices;
using System.Text.Json;
using AgenticTodos.Backend;
using Amazon.BedrockRuntime;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace AgenticTodos.Tests;

public class AmazonBedrockToolCallTests(ITestOutputHelper output)
{
    [LiveLlmFact(LiveLlmKeys.BedrockAccessKeyId, LiveLlmKeys.BedrockSecretAccessKey, LiveLlmKeys.BedrockRegion)]
    public async Task AgentWithTwoToolCalls()
    {
        using IChatClient client = NewChatClient().Build();

        List<string> toolCalls = [];

        var agent = client.AsAIAgent(
            name: "Agent",
            tools: [
                AIFunctionFactory.Create(
                    () =>
                    {
                        toolCalls.Add("get_current_time");
                        var time = DateTime.Now.ToString("HH:mm:ss");
                        return $"The current time is {time}.";
                    },
                    name: "get_current_time",
                    description: "Get the current time."
                ),
                AIFunctionFactory.Create(
                    (string color) =>
                    {
                        toolCalls.Add("change_background_color");
                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                        Console.WriteLine($"Changing color to {color}");
                    },
                    name: "change_background_color",
                    description: "Change the console background color to dark green."
                ),
            ]);

        var response = await agent.RunAsync(messages:
        [
            new ChatMessage(ChatRole.User, "Check the time and change the background to green."),
        ]);

        output.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));
        Assert.NotNull(response);
        Assert.Contains("get_current_time", toolCalls);
        Assert.Contains("change_background_color", toolCalls);
    }

    [LiveLlmFact(LiveLlmKeys.BedrockAccessKeyId, LiveLlmKeys.BedrockSecretAccessKey, LiveLlmKeys.BedrockRegion)]
    public async Task ChatWithTwoSeparatedToolCallsConsolidatesThem()
    {
        using IChatClient client = NewChatClient()
            .UseFunctionInvocation()
            .Use(client => new SeparateToolResultsMiddleware(client))
            .Use(client => new ConsolidateToolResultsMiddleware(client))
            .Build()
            ;

        List<string> toolCalls = [];

        var response = await client.GetResponseAsync(
            messages: [
                new ChatMessage(ChatRole.User, "What is the current time and change the background to green?"),
            ],
            options: new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create(
                        () =>
                        {
                            toolCalls.Add("get_current_time");
                            var time = DateTime.Now.ToString("HH:mm:ss");
                            return $"The current time is {time}.";
                        },
                        name: "get_current_time",
                        description: "Get the current time."
                    ),
                    AIFunctionFactory.Create(
                        (string color) =>
                        {
                            toolCalls.Add("change_background_color");
                            Console.ForegroundColor = ConsoleColor.DarkGreen;
                            Console.WriteLine($"Changing color to {color}");
                        },
                        name: "change_background_color",
                        description: "Change the console background color to dark green."
                    )
                ]
            });

        output.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));
        Assert.NotNull(response);
        Assert.Contains("get_current_time", toolCalls);
        Assert.Contains("change_background_color", toolCalls);
    }

    private static ChatClientBuilder NewChatClient()
    {
        // LiveLlmConfiguration, not a builder of this test's own: the [LiveLlmFact] guard admits the test
        // by reading exactly this, so a second set of sources here is how a test gets past the guard and
        // then fails on the very credential the guard reported as present.
        var config = LiveLlmConfiguration.Instance;

        var runtime = new AmazonBedrockRuntimeClient(
            awsAccessKeyId: config[LiveLlmKeys.BedrockAccessKeyId]!,
            awsSecretAccessKey: config[LiveLlmKeys.BedrockSecretAccessKey]!,
            region: Amazon.RegionEndpoint.GetBySystemName(config[LiveLlmKeys.BedrockRegion]!));

        var client = runtime
            .AsIChatClient("eu.anthropic.claude-sonnet-4-20250514-v1:0")
            .AsBuilder()
            ;

        //how to rewrite messages for sequential tool calls?
        //todo: add MAF and debug agui handling in sczenario of frontend and backend tool calls in same run!

        return client;
    }

    class SeparateToolResultsMiddleware(IChatClient inner) : IChatClient
    {
        public void Dispose() => inner.Dispose();

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var cosolidated = ConsolidateToolResults(messages);
            var response = await inner.GetResponseAsync(cosolidated, options, cancellationToken);
            return response;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var cosolidated = ConsolidateToolResults(messages);
            await foreach (var update in inner.GetStreamingResponseAsync(cosolidated, options, cancellationToken))
            {
                yield return update;
            }
        }

        private IEnumerable<ChatMessage> ConsolidateToolResults(IEnumerable<ChatMessage> messages)
        {
            return messages.SelectMany(msg => 
            {
                if (msg.Role == ChatRole.Tool)
                {
                    return msg.Contents.Select(functionResult => new ChatMessage(ChatRole.Tool, [functionResult])
                    {
                        MessageId = msg.MessageId,
                        AuthorName = msg.AuthorName,
                        AdditionalProperties = msg.AdditionalProperties,
                    }); 
                }
                else return [ msg ];
            });
        }
    }
}
