using System.Text.Json;
using Amazon.BedrockRuntime;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace AgenticTodos.Tests;

public class AmazonBedrockToolCallTest(ITestOutputHelper output)
{
    [Fact]
    public async Task WithTwoToolCalls()
    {
        using IChatClient client = NewChatClient();

        List<string> toolCalls = [];

        var agent = client.CreateAIAgent(
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

    private static IChatClient NewChatClient()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddUserSecrets("99db47a8-e571-40ad-829f-0733c2f6e62b")
            .Build();

        var runtime = new AmazonBedrockRuntimeClient(
            awsAccessKeyId: config["AWSBedrockAccessKeyId"]!,
            awsSecretAccessKey: config["AWSBedrockSecretAccessKey"]!,
            region: Amazon.RegionEndpoint.GetBySystemName(config["AWSBedrockRegion"]!));
        
        var client = runtime
            .AsIChatClient("eu.anthropic.claude-sonnet-4-20250514-v1:0")
            .AsBuilder()
            .Build();

        //how to rewrite messages for sequential tool calls?

        return client;
    }
}
