using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Reflection;
using System.Collections;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

[ApiController]
[Route("agui")]
public class AguiController([FromKeyedServices("mainagent")] AIAgent agent) : ControllerBase
{
    /// <summary>
    /// Inlined from https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.Hosting.AGUI.AspNetCore/AGUIEndpointRouteBuilderExtensions.cs
    /// </summary>
    [HttpPost]
    public async Task<IResult> Agui(CancellationToken cancellationToken)
    {
        var jsonOptions = HttpContext.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>();
        var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;

        var input = await AguiRunAgentInputCompatible.Parse(HttpContext.Request, jsonSerializerOptions, cancellationToken);
        if (input is null)
        {
            return Results.BadRequest();
        }

        var messages = input.Messages;
        var clientTools = input.Tools;

        // Create run options with AG-UI context in AdditionalProperties
        var runOptions = new ChatClientAgentRunOptions
        {
            ChatOptions = new ChatOptions
            {
                Tools = clientTools,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["ag_ui_state"] = input.State,
                    ["ag_ui_context"] = input.Context,
                    ["ag_ui_forwarded_properties"] = input.ForwardedProperties,
                    ["ag_ui_thread_id"] = input.ThreadId,
                    ["ag_ui_run_id"] = input.RunId
                }
            }
        };

        // Run the agent and convert to AG-UI events
        var events = agent.RunStreamingAsync(
            messages: messages,
            options: runOptions,
            cancellationToken: cancellationToken)
            .AsChatResponseUpdatesAsync()
            .FilterServerToolsFromMixedToolInvocationsAsync(clientTools, cancellationToken)
            .AsAGUIEventStreamAsync(
                input.ThreadId,
                input.RunId,
                jsonSerializerOptions,
                cancellationToken);

        var outputType = Type.GetType("Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.AGUIServerSentEventsResult, Microsoft.Agents.AI.Hosting.AGUI.AspNetCore", throwOnError: true)!;
        var loggerFactory = HttpContext.RequestServices.GetRequiredService<ILoggerFactory>();
        var sseLogger = loggerFactory.CreateLogger(outputType);

        var output = AguiReflector.CreateAguiServerSentEventsResult(
            outputType,
            events,
            jsonSerializerOptions,
            sseLogger,
            cancellationToken,
            HttpContext.RequestServices);

        return (IResult)output;
    }
}

internal sealed class AguiRunAgentInputCompatible
{
    private AguiRunAgentInputCompatible(
        string threadId,
        string runId,
        IReadOnlyList<ChatMessage> messages,
        List<AITool> tools,
        object? state,
        object? forwardedProperties,
        KeyValuePair<string, string>[]? context)
    {
        ThreadId = threadId;
        RunId = runId;
        Messages = messages;
        Tools = tools;
        State = state;
        ForwardedProperties = forwardedProperties;
        Context = context;
    }

    public string ThreadId { get; }
    public string RunId { get; }

    public object? State { get; }
    public object? ForwardedProperties { get; }
    public KeyValuePair<string, string>[]? Context { get; }

    public IReadOnlyList<ChatMessage> Messages { get; }
    public List<AITool> Tools { get; }

    public static async Task<AguiRunAgentInputCompatible?> Parse(
        HttpRequest request,
        JsonSerializerOptions jsonSerializerOptions,
        CancellationToken cancellationToken)
    {
        var inputType = Type.GetType(
            "Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.Shared.RunAgentInput, Microsoft.Agents.AI.Hosting.AGUI.AspNetCore",
            throwOnError: true)!;

        object? input;
        try
        {
            input = await JsonSerializer.DeserializeAsync(
                utf8Json: request.Body,
                returnType: inputType,
                options: jsonSerializerOptions,
                cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }

        if (input is null)
        {
            return null;
        }

        var messagesProp = inputType.GetProperty("Messages");
        var toolsProp = inputType.GetProperty("Tools");
        var stateProp = inputType.GetProperty("State");
        var contextProp = inputType.GetProperty("Context");
        var threadIdProp = inputType.GetProperty("ThreadId");
        var runIdProp = inputType.GetProperty("RunId");

        // Property name differs across preview versions and/or JSON naming policies.
        var forwardedPropsProp = inputType.GetProperty("ForwardedProperties") ?? inputType.GetProperty("ForwardedProps");

        var aguiMessages = messagesProp?.GetValue(input);
        if (aguiMessages is null)
        {
            return null;
        }

        var parsedMessages = AguiReflector.AsChatMessages(aguiMessages, jsonSerializerOptions).ToList();

        var toolDefinitions = BuildTools(toolsProp?.GetValue(input));
        var parsedTools = toolDefinitions
            .Select(t => AIFunctionFactory.CreateDeclaration(name: t.Name, description: t.Description, jsonSchema: t.Parameters))
            .Cast<AITool>()
            .ToList();

        var context = BuildContextPairs(contextProp?.GetValue(input));

        return new AguiRunAgentInputCompatible(
            threadId: threadIdProp?.GetValue(input) as string ?? string.Empty,
            runId: runIdProp?.GetValue(input) as string ?? string.Empty,
            messages: parsedMessages,
            tools: parsedTools,
            state: stateProp?.GetValue(input),
            forwardedProperties: forwardedPropsProp?.GetValue(input),
            context: context);
    }

    private static IReadOnlyList<AguiToolCompatible> BuildTools(object? toolsObj)
    {
        if (toolsObj is null)
        {
            return [];
        }

        if (toolsObj is not IEnumerable enumerable)
        {
            return [];
        }

        var tools = new List<AguiToolCompatible>();
        foreach (var item in enumerable)
        {
            if (item is null)
            {
                continue;
            }

            var itemType = item.GetType();

            var name = itemType.GetProperty("Name")?.GetValue(item) as string ?? string.Empty;
            var description = itemType.GetProperty("Description")?.GetValue(item) as string ?? string.Empty;

            var parametersObj = itemType.GetProperty("Parameters")?.GetValue(item);
            var parameters = parametersObj switch
            {
                JsonElement je => je,
                JsonDocument jd => jd.RootElement.Clone(),
                _ => default
            };

            tools.Add(new AguiToolCompatible(name, description, parameters));
        }

        return tools;
    }

    private static KeyValuePair<string, string>[]? BuildContextPairs(object? contextObj)
    {
        if (contextObj is null)
        {
            return null;
        }

        if (contextObj is not IEnumerable enumerable)
        {
            return null;
        }

        var items = new List<KeyValuePair<string, string>>();
        foreach (var item in enumerable)
        {
            if (item is null)
            {
                continue;
            }

            var itemType = item.GetType();
            var description = itemType.GetProperty("Description")?.GetValue(item) as string ?? string.Empty;
            var value = itemType.GetProperty("Value")?.GetValue(item) as string ?? string.Empty;
            items.Add(new KeyValuePair<string, string>(description, value));
        }

        return items.Count == 0 ? null : items.ToArray();
    }
}

internal readonly record struct AguiToolCompatible(string Name, string Description, JsonElement Parameters);

public static class AguiReflector
{
    public static object CreateAguiServerSentEventsResult(
        Type resultType,
        IAsyncEnumerable<object> events,
        JsonSerializerOptions jsonSerializerOptions,
        ILogger sseLogger,
        CancellationToken cancellationToken,
        IServiceProvider services)
    {
        var ctors = resultType.GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            .OrderByDescending(c => c.GetParameters().Length)
            .ToArray();

        foreach (var ctor in ctors)
        {
            var parameters = ctor.GetParameters();
            var args = new object?[parameters.Length];

            var compatible = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var parameterType = parameter.ParameterType;

                object? arg = parameterType switch
                {
                    _ when parameterType == typeof(JsonSerializerOptions) => jsonSerializerOptions,
                    _ when parameterType == typeof(CancellationToken) => cancellationToken,
                    _ when parameterType == typeof(ILogger) => sseLogger,
                    _ when parameterType.IsGenericType &&
                           parameterType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>) =>
                        BindAsyncEnumerable(parameterType, events),
                    _ => services.GetService(parameterType) ??
                         (parameterType.IsInstanceOfType(sseLogger) ? sseLogger : null)
                };

                if (arg is null)
                {
                    if (parameter.HasDefaultValue)
                    {
                        arg = parameter.DefaultValue;
                    }
                    else
                    {
                        compatible = false;
                        break;
                    }
                }

                args[i] = arg;
            }

            if (!compatible)
            {
                continue;
            }

            try
            {
                return ctor.Invoke(args!);
            }
            catch
            {
                // Try the next constructor.
            }
        }

        var signatures = string.Join(", ", ctors.Select(c => $"({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name))})"));

        throw new MissingMethodException($"No compatible constructor found for {resultType.FullName}. Available constructors: {signatures}");
    }

    private static object? BindAsyncEnumerable(Type asyncEnumerableType, IAsyncEnumerable<object> events)
    {
        var elementType = asyncEnumerableType.GetGenericArguments()[0];
        if (elementType == typeof(object))
        {
            return events;
        }

        var castMethod = typeof(AguiReflector)
            .GetMethod(nameof(CastAsyncEnumerable), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(elementType);
        return castMethod.Invoke(null, [events]);
    }

    private static async IAsyncEnumerable<T> CastAsyncEnumerable<T>(IAsyncEnumerable<object> source)
    {
        await foreach (var item in source)
        {
            yield return (T)item;
        }
    }

    public static IEnumerable<ChatMessage> AsChatMessages(object aguidMessages, JsonSerializerOptions jsonSerializerOptions)
    {
        var aGUIChatMessageExtensions = Type.GetType("Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.Shared.AGUIChatMessageExtensions, Microsoft.Agents.AI.Hosting.AGUI.AspNetCore", throwOnError: true);
        var asChatMessages = aGUIChatMessageExtensions!
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Single(m =>
                m.Name == "AsChatMessages" &&
                m.GetParameters() is { Length: 2 } p &&
                p[1].ParameterType == typeof(JsonSerializerOptions) &&
                p[0].ParameterType.IsGenericType &&
                p[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                p[0].ParameterType.GetGenericArguments()[0].Name == "AGUIMessage" &&
                m.ReturnType.IsGenericType &&
                m.ReturnType.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                m.ReturnType.GetGenericArguments()[0].Name == "ChatMessage");

        var result = asChatMessages!.Invoke(null, [aguidMessages, jsonSerializerOptions]);
        return (IEnumerable<ChatMessage>)result!;
    }

    public static async IAsyncEnumerable<ChatResponseUpdate> FilterServerToolsFromMixedToolInvocationsAsync(
        this IAsyncEnumerable<ChatResponseUpdate> updates,
        List<AITool>? clientTools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var aGUIChatResponseUpdateStreamExtensions = Type.GetType("Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.AGUIChatResponseUpdateStreamExtensions, Microsoft.Agents.AI.Hosting.AGUI.AspNetCore", throwOnError: true);
        var filterServerToolsFromMixedToolInvocationsAsync = aGUIChatResponseUpdateStreamExtensions!
            .GetMethod("FilterServerToolsFromMixedToolInvocationsAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var invoked = (IAsyncEnumerable<ChatResponseUpdate>)filterServerToolsFromMixedToolInvocationsAsync.Invoke(null, [updates, clientTools, cancellationToken])!;
        await foreach (var update in invoked.WithCancellation(cancellationToken))
        {
            yield return update;
        }
    }

    public static async IAsyncEnumerable<object> AsAGUIEventStreamAsync(
        this IAsyncEnumerable<ChatResponseUpdate> updates,
        string threadId,
        string runId,
        JsonSerializerOptions jsonSerializerOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var aguiAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "Microsoft.Agents.AI.Hosting.AGUI.AspNetCore", StringComparison.Ordinal))
            ?? Assembly.Load("Microsoft.Agents.AI.Hosting.AGUI.AspNetCore");

        // Extension method type names have changed across preview versions; instead of hard-coding,
        // locate the public static method by name and parameter shape.
        var asAGUIEventStreamAsync = aguiAssembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m =>
                m.Name == "AsAGUIEventStreamAsync" &&
                m.ReturnType.IsGenericType &&
                m.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>) &&
                m.GetParameters() is { Length: >= 4 and <= 5 } p &&
                p[0].ParameterType.IsGenericType &&
                p[0].ParameterType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>) &&
                p[0].ParameterType.GetGenericArguments()[0].Name == nameof(ChatResponseUpdate) &&
                p[1].ParameterType == typeof(string) &&
                p[2].ParameterType == typeof(string) &&
                p[3].ParameterType == typeof(JsonSerializerOptions) &&
                (p.Length == 4 || p[4].ParameterType == typeof(CancellationToken)))
            .OrderByDescending(m => m.GetParameters().Length)
            .FirstOrDefault();

        if (asAGUIEventStreamAsync is null)
        {
            throw new TypeLoadException("Could not locate AsAGUIEventStreamAsync extension method in Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.");
        }

        object? invokedObj = asAGUIEventStreamAsync.GetParameters().Length == 5
            ? asAGUIEventStreamAsync.Invoke(null, [updates, threadId, runId, jsonSerializerOptions, cancellationToken])
            : asAGUIEventStreamAsync.Invoke(null, [updates, threadId, runId, jsonSerializerOptions]);

        var invoked = (IAsyncEnumerable<object>)invokedObj!;

        await foreach (var @event in invoked.WithCancellation(cancellationToken))
        {
            yield return @event;
        }
    }

    public static async Task<object?> AwaitReflectedAsync(object awaitable)
    {
        if (awaitable is Task task)
        {
            await task;
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        var type = awaitable.GetType();

        if (type == typeof(ValueTask))
        {
            await (ValueTask)awaitable;
            return null;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = type.GetMethod("AsTask", Type.EmptyTypes);
            var taskObj = asTask!.Invoke(awaitable, null);
            if (taskObj is not Task awaitedTask)
            {
                throw new InvalidOperationException($"Expected AsTask() to return Task, got {taskObj?.GetType().FullName ?? "null"}");
            }

            await awaitedTask;
            var result = taskObj.GetType().GetProperty("Result")?.GetValue(taskObj);

            return result;
        }

        throw new InvalidOperationException($"Unsupported awaitable type: {type.FullName}");
    }
}