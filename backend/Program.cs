using AgenticTodos.Backend;
using Amazon.BedrockRuntime;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

OpenTelemetryExtensions.ConfigureOpenTelemetry(builder);
builder.Services.AddOpenApi();
builder.Services.AddDevUI();
builder.Services.AddAGUI();
builder.Services.AddControllers();

var tools = await GetTools(builder.Configuration);
builder.Services.AddKeyedSingleton("openai", (sp, key) => CreateAgent(
    chatClient: OpenAI(builder.Configuration, builder.Environment.ApplicationName),
    tools: tools,
    services: sp));
builder.Services.AddKeyedSingleton("amazonbedrock", (sp, key) => CreateAgent(
    chatClient: AmazonBedrock(builder.Configuration, sp),
    tools: tools,
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



var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapDevUI();
app.MapGet("/", () => "Hello Agents!");
app.MapGet("/agents", (IAgentProvider agents) => agents.GetAliases());

// Singleton agents with official AGUI endpoints
app.MapAGUI("/agents/static/openai/agui", CreateAgent(
    chatClient: OpenAI(builder.Configuration, builder.Environment.ApplicationName),
    tools: tools,
    services: app.Services))
    .AddOpenApiOperationTransformer((operation, context, ct) =>
    {
        operation.Deprecated = true; // no session management
        return Task.CompletedTask;
    })
    ;
app.MapAGUI("/agents/static/amazonbedrock/agui", CreateAgent(
    chatClient: AmazonBedrock(builder.Configuration, app.Services),
    tools: tools,
    services: app.Services))
    .AddOpenApiOperationTransformer((operation, context, ct) =>
    {
        operation.Deprecated = true; // no session management
        return Task.CompletedTask;
    })
    ;

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
        .Build(services);
}

static async Task<AIFunction[]> GetTools(IConfiguration configuration)
{
    var mcpClient = await McpClient.CreateAsync(new HttpClientTransport(new()
    {
        Endpoint = new Uri($"{configuration["services:AgenticTodos-McpServer:https:0"]}/mcp"),
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
