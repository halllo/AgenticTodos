using System.Text.Json;
using AGUI.Abstractions;
using Amazon.BedrockRuntime;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;
using AgenticTodos.Backend;

namespace AgenticTodos.Tests;

public class AmazonBedrockFieldsTests(ITestOutputHelper output)
{
    [LiveLlmFact(LiveLlmKeys.BedrockAccessKeyId, LiveLlmKeys.BedrockSecretAccessKey, LiveLlmKeys.BedrockRegion)]
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
    /// A live counterpart to <see cref="OmitAdditionalPropertiesMiddlewareTests"/>: the app-internal
    /// objects that ride on <see cref="ChatOptions.AdditionalProperties"/> by the time a request reaches
    /// Bedrock must not break the call. An adapter that forwarded them as
    /// <c>AdditionalModelRequestFields</c> would make Claude answer <i>"Extra inputs are not
    /// permitted"</i>; the pipeline strips them first (see <c>Program.cs</c>).
    /// </summary>
    [LiveLlmFact(LiveLlmKeys.BedrockAccessKeyId, LiveLlmKeys.BedrockSecretAccessKey, LiveLlmKeys.BedrockRegion)]
    public async Task AppInternalAdditionalProperties_DoNotReachTheModel()
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
                    // The real shape: the AG-UI server SDK stashes the whole RunAgentInput under a key
                    // that is its own internal detail, which is why the middleware matches by value type.
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

        var runtime = new AmazonBedrockRuntimeClient(
            awsAccessKeyId: config[LiveLlmKeys.BedrockAccessKeyId]!,
            awsSecretAccessKey: config[LiveLlmKeys.BedrockSecretAccessKey]!,
            region: Amazon.RegionEndpoint.GetBySystemName(config[LiveLlmKeys.BedrockRegion]!));

        var client = runtime
            .AsIChatClient("eu.anthropic.claude-sonnet-4-20250514-v1:0")
            .AsBuilder()
            .Use(client => new OmitAdditionalPropertiesMiddleware(client, [typeof(RunAgentInput)]))
            .Build();

        return client;
    }
}
