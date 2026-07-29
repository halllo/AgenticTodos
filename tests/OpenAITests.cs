using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;
using AgenticTodos.Backend;
using OpenAI;

namespace AgenticTodos.Tests;

public class OpenAITests(ITestOutputHelper output)
{
    [LiveLlmFact(LiveLlmKeys.OpenAIApiKey)]
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
        Assert.False(string.IsNullOrWhiteSpace(response.Text));
    }

    /// <summary>
    /// The counterpart to <see cref="AmazonBedrockFieldsTests.AppInternalAdditionalProperties_DoNotReachTheModel"/>:
    /// the OpenAI adapter ignores unknown <see cref="ChatOptions.AdditionalProperties"/> instead of
    /// forwarding them, which is why the OpenAI agent needs no stripping middleware.
    /// </summary>
    [LiveLlmFact(LiveLlmKeys.OpenAIApiKey)]
    public async Task AppInternalAdditionalProperties_AreToleratedWithoutStripping()
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
                    { "agui_input", new RunAgentInput { ThreadId = "thread_ba81", RunId = "run_1" } },
                },
            });

        output.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));
        Assert.False(string.IsNullOrWhiteSpace(response.Text));
    }

    private static IChatClient NewChatClient()
    {
        // LiveLlmConfiguration, not a builder of this test's own: the [LiveLlmFact] guard admits the test
        // by reading exactly this, so a second set of sources here is how a test gets past the guard and
        // then fails on the very credential the guard reported as present.
        var config = LiveLlmConfiguration.Instance;

        var client = new OpenAIClient(
                config[LiveLlmKeys.OpenAIApiKey] ?? throw new InvalidOperationException($"{LiveLlmKeys.OpenAIApiKey} is not set."))
            .GetChatClient("gpt-4o")
            .AsIChatClient();

        return client;
    }
}
