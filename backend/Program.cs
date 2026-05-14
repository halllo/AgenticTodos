using AgenticTodos.Backend;
using Amazon.BedrockRuntime;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

OpenTelemetryExtensions.ConfigureOpenTelemetry(builder);
builder.Services.AddOpenApi();
builder.Services.AddAGUI();
builder.Services.AddControllers();

builder.Services.AddSingleton(_ =>
    new Lazy<Task<AIFunction[]>>(() => GetTools(builder.Configuration)));
builder.Services.AddKeyedSingleton("openai", (sp, key) => CreateAgent(
    chatClient: OpenAI(builder.Configuration, builder.Environment.ApplicationName),
    tools: sp.GetRequiredService<Lazy<Task<AIFunction[]>>>().Value.GetAwaiter().GetResult(),
    services: sp));
builder.Services.AddKeyedSingleton("amazonbedrock", (sp, key) => CreateAgent(
    chatClient: AmazonBedrock(builder.Configuration, sp),
    tools: sp.GetRequiredService<Lazy<Task<AIFunction[]>>>().Value.GetAwaiter().GetResult(),
    services: sp));

builder.Services.AddKeyedSingleton("agentAliases", builder.Services
    .Where(sd => sd.IsKeyedService && sd.ServiceType == typeof(AIAgent))
    .Select(sd => sd.ServiceKey?.ToString())
    .Where(key => key is not null && key != "*")
    .Select(key => key!)
    .OrderBy(key => key)
    .ToList());
builder.Services.AddScoped<IAgentProvider, AgentProvider>();
builder.Services.AddSingleton<AgentSessionStore, FileSystemSessionStore>();
builder.Services.AddAGUISessionStore();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();



var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/", () => "Hello Agents!");
app.MapGet("/ping", () => Results.Ok());
app.MapGet("/agents", (IAgentProvider agents) => agents.GetAliases());

// CSP headers for the outer sandbox iframe
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.Equals("/sandbox.html", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; connect-src *;";
    }
    await next();
});
app.UseStaticFiles();

// Inject unsupported AGUI events for endpoints ending with "/agui"
app.UseWhen(
    ctx => ctx.Request.Path.Value?.EndsWith("/agui", StringComparison.OrdinalIgnoreCase) == true,
    branch => branch.UseMiddleware<SseEventInjectionMiddleware>((Func<string, IEnumerable<string>?>)McpAppsActivityInjector.TryInjectActivitySnapshot)
);

// Transparent HTTP proxy that forwards MCP Streamable HTTP traffic to the MCP server.
// Must be registered BEFORE MapAGUIViaHttpRoutingAgent, which intercepts all /agents/* paths.
app.Use(async (HttpContext ctx, RequestDelegate next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/agents/mcp-relay"))
    {
        await next(ctx);
        return;
    }

    var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
    var factory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>();
    var mcpBaseUrl = config["services:AgenticTodos-McpServer:https:0"]
        ?? config["services:AgenticTodos-McpServer:http:0"]
        ?? throw new InvalidOperationException("MCP server endpoint is not configured.");
    var mcpEndpoint = $"{mcpBaseUrl.TrimEnd('/')}/mcp";
    using var httpClient = factory.CreateClient();

    var forward = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), mcpEndpoint);
    if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
        forward.Content = new StreamContent(ctx.Request.Body);
    if (ctx.Request.ContentType is { } ct && forward.Content != null)
        forward.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ct);
    foreach (var (key, value) in ctx.Request.Headers)
    {
        if (!key.StartsWith("Host", StringComparison.OrdinalIgnoreCase) &&
            !key.StartsWith("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            forward.Headers.TryAddWithoutValidation(key, [.. value]);
    }

    var response = await httpClient.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    ctx.Response.StatusCode = (int)response.StatusCode;
    foreach (var (key, values) in response.Headers.Concat(response.Content.Headers))
    {
        if (!key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            ctx.Response.Headers[key] = values.ToArray();
    }
    await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
});

// Singleton agents with official AGUI endpoints
// app.MapAGUI("/agents/static/openai/agui", CreateAgent(
//     chatClient: OpenAI(builder.Configuration, builder.Environment.ApplicationName),
//     tools: tools,
//     services: app.Services))
//     .AddOpenApiOperationTransformer((operation, context, ct) =>
//     {
//         operation.Deprecated = true; // no session management
//         return Task.CompletedTask;
//     })
//     ;
// app.MapAGUI("/agents/static/amazonbedrock/agui", CreateAgent(
//     chatClient: AmazonBedrock(builder.Configuration, app.Services),
//     tools: tools,
//     services: app.Services))
//     .AddOpenApiOperationTransformer((operation, context, ct) =>
//     {
//         operation.Deprecated = true; // no session management
//         return Task.CompletedTask;
//     })
//     ;

// Routing agent (suggested workaround)
app.MapAGUIViaHttpRoutingAgent();

// Reflection agents (self-made)
app.MapControllers();

app.Run();


static IChatClient OpenAI(IConfiguration configuration, string applicationName)
{
    var openaiApiKey = configuration["OPENAI_API_KEY"] ?? throw new InvalidOperationException("OPENAI_API_KEY is not set.");
    return new OpenAIClient(openaiApiKey)
        .GetChatClient("gpt-4o")
        .AsIChatClient()
        .AsBuilder()
        .UseOpenTelemetry(sourceName: applicationName, configure: c => c.EnableSensitiveData = true)
        .Build()
        ;
}

static IChatClient AmazonBedrock(IConfiguration configuration, IServiceProvider services)
{
    var applicationName = services.GetRequiredService<IHostEnvironment>().ApplicationName;
    var runtime = new AmazonBedrockRuntimeClient(
        awsAccessKeyId: configuration["AWSBedrockAccessKeyId"],
        awsSecretAccessKey: configuration["AWSBedrockSecretAccessKey"],
        region: Amazon.RegionEndpoint.GetBySystemName(configuration["AWSBedrockRegion"]));

    return runtime
        .AsIChatClient(defaultModelId:
            //"eu.anthropic.claude-sonnet-4-20250514-v1:0"
            "eu.anthropic.claude-sonnet-4-5-20250929-v1:0"
        )
        .AsBuilder()
        .UseOpenTelemetry(sourceName: applicationName, configure: c => c.EnableSensitiveData = true)
        // .ConfigureOptions(c =>
        // {
        //     c.AllowMultipleToolCalls = false; // does not seem to have any effect
        // })
        .Use(client => new OmitAdditionalPropertiesMiddleware(
            inner: client,
            propertyKeysToOmit: [ //prevent the "Extra inputs are not permitted" error
                "ag_ui_thread_id",
                "ag_ui_run_id",
                "ag_ui_state",
                "ag_ui_context",
                "ag_ui_forwarded_properties"
            ]))
        .Use((client, services) => new ConsolidateToolResultsMiddleware(inner: client))
        .Use((client, services) => new LoggingMiddleware(inner: client, logger: services.GetRequiredService<ILogger<LoggingMiddleware>>()))
        .Build(services)
        ;
}

static AIAgent CreateAgent(IChatClient chatClient, AIFunction[] tools, IServiceProvider services)
{
    var applicationName = services.GetRequiredService<IHostEnvironment>().ApplicationName;
    return chatClient
        .AsAIAgent(
            options: new ChatClientAgentOptions
            {
                Name = "AGUIAssistant",
                ChatOptions = new ChatOptions()
                {
                    Tools = tools,
                },
                ChatHistoryProvider = new FileSystemChatHistoryProvider(), // DevUI uses InMemoryResponsesService, which stores/loads directly with IConversationStorage.
                AIContextProviders = [],
            },
            services: services)
        .AsBuilder()
        .UseOpenTelemetry(sourceName: applicationName, configure: c => c.EnableSensitiveData = true)
        .Use(sharedFunc: OmitEmptySystemMessagesMiddleware.Invoke)
        .Use(runFunc: StateSnapshotMiddleware.RunAsync, runStreamingFunc: StateSnapshotMiddleware.RunStreamingAsync)
        .UseDetectMcpAppsActivity()
        .Build(services);
}

static async Task<AIFunction[]> GetTools(IConfiguration configuration)
{
    var mcpBaseUrl = configuration["services:AgenticTodos-McpServer:https:0"]
        ?? configuration["services:AgenticTodos-McpServer:http:0"]
        ?? throw new InvalidOperationException("MCP server endpoint is not configured.");
    var mcpClient = await McpClient.CreateAsync(new HttpClientTransport(new()
    {
        Endpoint = new Uri($"{mcpBaseUrl.TrimEnd('/')}/mcp"),
        TransportMode = HttpTransportMode.StreamableHttp,
    }));
    var mcpTools = await mcpClient.ListToolsAsync();

    return [
        .. mcpTools,

        AIFunctionFactory.Create(
            name: "increment_counter",
            description: "Increment the counter.",
            method: (IServiceProvider services) =>
            {
                var loggerFactory = services.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("IncrementCounterFunction");

                var state = AIAgent.CurrentRunContext?.RunOptions?.AdditionalProperties?["my_state"] as StateSnapshotMiddleware.ConversationState;
                if (state != null)
                {
                    state.Counter++;
                }

                logger.LogInformation("IncrementCounterFunction called. Counter: {Counter}", state?.Counter);

                return state?.Counter;
            }
        )
    ];
}
