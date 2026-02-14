using System.Text;
using System.Text.Json;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Agents.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// In this test I try to get the same behavior as https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Hosting.AGUI.AspNetCore/AGUIEndpointRouteBuilderExtensions.csTests so that I can us it more flexibly.
/// </summary>
public class AguiControllerTest
{
    [Fact]
    public async Task Request()
    {
        var requestBody = """
        {
            "threadId": "08f18960-f2c2-4745-9c5a-06e3e3a8964b",
            "runId": "e89024c7-dfc9-4652-82ad-71a3f183d017",
            "tools": [
                {
                    "name": "change_background_color",
                    "description": "Change the left panel background color. Can accept solid colors (e.g., '#1e3a8a', 'red') or gradients (e.g., 'linear-gradient(135deg, #667eea 0%, #764ba2 100%)').",
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "color": {
                                "type": "string",
                                "description": "The background color or gradient to apply to the left panel. Can be a hex color, named color, or CSS gradient."
                            }
                        },
                        "required": [
                            "color"
                        ]
                    }
                },
                {
                    "name": "add_todo",
                    "description": "Add a todo to the todo list in the left panel.",
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "title": {
                                "type": "string",
                                "description": "The todo title text."
                            }
                        },
                        "required": [
                            "title"
                        ]
                    }
                }
            ],
            "context": [],
            "forwardedProps": {},
            "state": {},
            "messages": [
                {
                "id": "",
                "role": "user",
                "content": "hallo"
                }
            ]
        }
        """;

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddOptions();
        serviceCollection.AddAGUI();
        serviceCollection.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
        });
        var services = serviceCollection.BuildServiceProvider();

        var bodyBytes = Encoding.UTF8.GetBytes(requestBody);
        await using var responseBody = new MemoryStream();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services
        };
        httpContext.Request.ContentType = "application/json; charset=utf-8";
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Response.Body = responseBody;

        using IChatClient client = new DeterministicChatClient();
        var agent = client.AsAIAgent(name: "Agent", tools: []);
        var agentProvider = new StaticAgentProvider(agent);

        var controller = new AguiController(agentProvider)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };

        var result = await controller.Agui("", CancellationToken.None);

        var expectedOutput = """
        data: {"threadId":"08f18960-f2c2-4745-9c5a-06e3e3a8964b","runId":"e89024c7-dfc9-4652-82ad-71a3f183d017","type":"RUN_STARTED"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","role":"assistant","type":"TEXT_MESSAGE_START"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":"Hello! How","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":" can I help you today? ","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":"\n\nI","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":" can","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":" assist","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":" you with:","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":"\n- Adding","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":" todos","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":" to your list","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":"\n-","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":" Changing","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":" the background color of the left panel","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":"\n- Getting the current UTC","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":" time\n\nWhat","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","delta":" would you like to do?","type":"TEXT_MESSAGE_CONTENT"}

        data: {"messageId":"e8c477f4c4da4efd8cedd0117e43a08d","type":"TEXT_MESSAGE_END"}

        data: {"threadId":"08f18960-f2c2-4745-9c5a-06e3e3a8964b","runId":"e89024c7-dfc9-4652-82ad-71a3f183d017","result":null,"type":"RUN_FINISHED"}
        """;

        await result.ExecuteAsync(httpContext);

        responseBody.Position = 0;
        var actualOutput = await new StreamReader(responseBody, Encoding.UTF8).ReadToEndAsync();

        AssertAguiSseLikeExpected(actualOutput, expectedOutput);
    }

    private static void AssertAguiSseLikeExpected(string actualSse, string expectedSse)
    {
        var actualEvents = ParseSseDataEvents(actualSse);
        var expectedEvents = ParseSseDataEvents(expectedSse);

        if (expectedEvents.Count != actualEvents.Count)
        {
            var snippet = actualSse.Length <= 6000
                ? actualSse
                : actualSse[..6000] + "\n... (truncated)";

            throw new Xunit.Sdk.XunitException(
                $"SSE event count mismatch. Expected {expectedEvents.Count}, got {actualEvents.Count}.\n\nActual SSE:\n{snippet}");
        }

        string? actualMessageId = null;

        for (var i = 0; i < expectedEvents.Count; i++)
        {
            var expected = expectedEvents[i];
            var actual = actualEvents[i];

            Assert.Equal(GetRequiredString(expected, "type"), GetRequiredString(actual, "type"));

            var type = GetRequiredString(actual, "type");
            switch (type)
            {
                case "RUN_STARTED":
                    Assert.Equal(GetRequiredString(expected, "threadId"), GetRequiredString(actual, "threadId"));
                    Assert.Equal(GetRequiredString(expected, "runId"), GetRequiredString(actual, "runId"));
                    break;

                case "RUN_FINISHED":
                    Assert.Equal(GetRequiredString(expected, "threadId"), GetRequiredString(actual, "threadId"));
                    Assert.Equal(GetRequiredString(expected, "runId"), GetRequiredString(actual, "runId"));
                    if (actual.TryGetProperty("result", out var result))
                    {
                        Assert.True(result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
                    }
                    break;

                case "TEXT_MESSAGE_START":
                    Assert.Equal(GetRequiredString(expected, "role"), GetRequiredString(actual, "role"));
                    actualMessageId = GetRequiredString(actual, "messageId");
                    Assert.False(string.IsNullOrWhiteSpace(actualMessageId));
                    break;

                case "TEXT_MESSAGE_CONTENT":
                    Assert.Equal(GetRequiredString(expected, "delta"), GetRequiredString(actual, "delta"));
                    EnsureMessageIdConsistent(actual, ref actualMessageId);
                    break;

                case "TEXT_MESSAGE_END":
                    EnsureMessageIdConsistent(actual, ref actualMessageId);
                    break;
            }
        }
    }

    private static void EnsureMessageIdConsistent(JsonElement @event, ref string? messageId)
    {
        var current = GetRequiredString(@event, "messageId");
        messageId ??= current;
        Assert.Equal(messageId, current);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            throw new Xunit.Sdk.XunitException($"Expected JSON string property '{propertyName}'.");
        }

        return prop.GetString()!;
    }

    private static List<JsonElement> ParseSseDataEvents(string sse)
    {
        var normalized = sse.Replace("\r\n", "\n");
        var events = new List<JsonElement>();

        using var docs = new ListDisposer<JsonDocument>();
        foreach (var line in normalized.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var json = line[6..].Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            var doc = JsonDocument.Parse(json);
            docs.Add(doc);
            events.Add(doc.RootElement.Clone());
        }

        return events;
    }

    private sealed class ListDisposer<T> : IDisposable where T : IDisposable
    {
        private readonly List<T> items = [];
        public void Add(T item) => items.Add(item);
        public void Dispose()
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }

    private sealed class StaticAgentProvider(AIAgent agent) : IAgentProvider
    {
        public AIAgent? Get(string alias) => agent;
        public IReadOnlyList<string> GetAliases() => [nameof(agent)];
    }

    private sealed class DeterministicChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;

            const string threadId = "08f18960-f2c2-4745-9c5a-06e3e3a8964b";
            const string runId = "e89024c7-dfc9-4652-82ad-71a3f183d017";
            const string messageId = "e8c477f4c4da4efd8cedd0117e43a08d";

            string[] deltas =
            [
                "Hello! How",
                " can I help you today? ",
                "\n\nI",
                " can",
                " assist",
                " you with:",
                "\n- Adding",
                " todos",
                " to your list",
                "\n-",
                " Changing",
                " the background color of the left panel",
                "\n- Getting the current UTC",
                " time\n\nWhat",
                " would you like to do?",
            ];

            for (var i = 0; i < deltas.Length; i++)
            {
                var isLast = i == deltas.Length - 1;
                yield return CreateTextDeltaUpdate(
                    threadId: threadId,
                    runId: runId,
                    messageId: messageId,
                    delta: deltas[i],
                    isLast: isLast);
            }
        }

        private static ChatResponseUpdate CreateTextDeltaUpdate(
            string threadId,
            string runId,
            string messageId,
            string delta,
            bool isLast)
        {
            var update = (ChatResponseUpdate)CreateInstanceWithDefaults(typeof(ChatResponseUpdate));

            TrySet(update, "ConversationId", threadId);
            TrySet(update, "ResponseId", runId);
            TrySet(update, "MessageId", messageId);
            TrySet(update, "Role", ChatRole.Assistant);

            var contents = new List<AIContent> { new TextContent(delta) };
            TrySet(update, "Contents", contents);

            if (isLast)
            {
                // Different preview versions use different names; try a few.
                TrySet(update, "IsFinal", true);
                TrySet(update, "IsComplete", true);
                TrySet(update, "IsCompleted", true);
            }

            return update;
        }

        private static object CreateInstanceWithDefaults(Type type)
        {
            var ctors = type
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .OrderBy(c => c.GetParameters().Length)
                .ToArray();

            foreach (var ctor in ctors)
            {
                var parameters = ctor.GetParameters();
                var args = new object?[parameters.Length];

                for (var i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    if (p.HasDefaultValue)
                    {
                        args[i] = p.DefaultValue;
                        continue;
                    }

                    args[i] = p.ParameterType.IsValueType
                        ? Activator.CreateInstance(p.ParameterType)
                        : null;
                }

                try
                {
                    return ctor.Invoke(args);
                }
                catch
                {
                    // Try next ctor.
                }
            }

            throw new MissingMethodException($"No usable constructor found for {type.FullName}.");
        }

        private static void TrySet(object target, string propertyName, object? value)
        {
            var prop = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (prop is null || !prop.CanWrite)
            {
                return;
            }

            if (value is null)
            {
                if (!prop.PropertyType.IsValueType)
                {
                    prop.SetValue(target, null);
                }
                return;
            }

            if (prop.PropertyType.IsInstanceOfType(value))
            {
                prop.SetValue(target, value);
                return;
            }

            // Handle common collection covariance (e.g. IList<AIContent> vs List<AIContent>)
            if (prop.PropertyType.IsAssignableFrom(value.GetType()))
            {
                prop.SetValue(target, value);
            }
        }
    }
}
