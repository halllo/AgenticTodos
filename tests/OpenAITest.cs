using System.Text.Json;
using Amazon.BedrockRuntime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;
using AgenticTodos.Backend;
using OpenAI;

namespace AgenticTodos.Tests;

public class OpenAITest(ITestOutputHelper output)
{
    [Fact]
    public async Task WithoutAdditionalModelRequestFields()
    {
        using IChatClient client = NewChatClient();

        var response = await client.GetResponseAsync(
            messages:
            [
                new ChatMessage(ChatRole.User, "Hello. How are you?"),
            ],
            options: new()
            {
                Temperature = 0.0F,
                Tools = [],
            });

        output.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));
        Assert.NotNull(response);
    }

    [Fact]
    public async Task WithAdditionalModelRequestFields()
    {
        using IChatClient client = NewChatClient();

        var response = await client.GetResponseAsync(
            messages:
            [
                new ChatMessage(ChatRole.User, "Hello. How are you?"),
            ],
            options: new()
            {
                Temperature = 0.0F,
                Tools = [],
                AdditionalProperties = new()
                {
                    { "ag_ui_thread_id", "thread_ba818347681144109377b1c044e4f4f6" },
                },
            });

        output.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));
        Assert.NotNull(response);
    }

    private static IChatClient NewChatClient()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddUserSecrets("99db47a8-e571-40ad-829f-0733c2f6e62b")
            .Build();

        var client = new OpenAIClient(config["OPENAI_API_KEY"] ?? throw new InvalidOperationException("OPENAI_API_KEY is not set."))
            .GetChatClient("gpt-4o")
            .AsIChatClient();

        return client;
    }
}
