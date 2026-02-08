using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Reflection;
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
        var inputType = Type.GetType("Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.Shared.RunAgentInput, Microsoft.Agents.AI.Hosting.AGUI.AspNetCore", throwOnError: true)!;
        var input = await AguiReflector.Parse(inputType, HttpContext.Request, cancellationToken);
        if (input is null)
        {
            return Results.BadRequest();
        }

        var jsonOptions = HttpContext.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>();
        var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;

        var messages = AguiReflector.AsChatMessages(inputType.GetProperty("Messages")?.GetValue(input)!, jsonSerializerOptions);
        var clientTools = ((inputType.GetProperty("Tools")?.GetValue(input) as IEnumerable<object>) ?? [])
            .Select(t => AIFunctionFactory.CreateDeclaration(
                name: t.GetType().GetProperty("Name")?.GetValue(t) as string ?? string.Empty,
                description: t.GetType().GetProperty("Description")?.GetValue(t) as string ?? string.Empty,
                jsonSchema: t.GetType().GetProperty("Parameters")?.GetValue(t) as JsonElement? ?? new JsonElement()))
            .Cast<AITool>()
            .ToList();

        // Create run options with AG-UI context in AdditionalProperties
        var runOptions = new ChatClientAgentRunOptions
        {
            ChatOptions = new ChatOptions
            {
                Tools = clientTools,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["ag_ui_state"] = inputType.GetProperty("State")?.GetValue(input),
                    ["ag_ui_context"] = (inputType.GetProperty("Context")?.GetValue(input) as object[])?.Select(c => new KeyValuePair<string, string>(c.GetType().GetProperty("Description")?.GetValue(c) as string ?? string.Empty, c.GetType().GetProperty("Value")?.GetValue(c) as string ?? string.Empty)).ToArray(),
                    ["ag_ui_forwarded_properties"] = inputType.GetProperty("ForwardedProperties")?.GetValue(input),
                    ["ag_ui_thread_id"] = inputType.GetProperty("ThreadId")?.GetValue(input),
                    ["ag_ui_run_id"] = inputType.GetProperty("RunId")?.GetValue(input)
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
                inputType.GetProperty("ThreadId")?.GetValue(input) as string ?? string.Empty,
                inputType.GetProperty("RunId")?.GetValue(input) as string ?? string.Empty,
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

    public static async Task<object?> Parse(Type type, HttpRequest request, CancellationToken cancellationToken)
    {
        var readFromJsonAsyncDefinition = typeof(HttpRequestJsonExtensions)
            .GetMethod(
                name: "ReadFromJsonAsync",
                bindingAttr: System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                types: [typeof(HttpRequest), typeof(CancellationToken)]);

        var readFromJsonAsync = readFromJsonAsyncDefinition!.MakeGenericMethod(type);
        var invoked = readFromJsonAsync.Invoke(null, [request, cancellationToken]);
        var result = await AwaitReflectedAsync(invoked!);
        return result;
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