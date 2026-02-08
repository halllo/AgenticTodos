using AgenticTodos.Backend;
using Amazon.BedrockRuntime;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using OpenAI;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

OpenTelemetryExtensions.ConfigureOpenTelemetry(builder);
builder.Services.AddOpenApi();
builder.Services.AddAGUI();
builder.Services.AddKeyedSingleton("mainagent", (sp, key) => CreateAgent(
    chatClient: OpenAI(builder.Configuration, builder.Environment.ApplicationName),
    tools: GetTools(),
    services: sp));


var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/", () => "Hello World!");

var tools = GetTools();
app.MapAGUI("/openai/agui", CreateAgent(
    chatClient: OpenAI(builder.Configuration, builder.Environment.ApplicationName),
    tools: tools,
    services: app.Services));
app.MapAGUI("/amazonbedrock/agui", CreateAgent(
    chatClient: AmazonBedrock(builder.Configuration, app.Services),
    tools: tools,
    services: app.Services));

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
            name: "AGUIAssistant",
            tools: tools,
            services: services)
        .AsBuilder()
        .UseOpenTelemetry(sourceName: applicationName, configure: c => c.EnableSensitiveData = true)
        .Build(services)
        ;
}

static AIFunction[] GetTools()
{
    return [
        AIFunctionFactory.Create(
            method: (IServiceProvider services) =>
            {
                var loggerFactory = services.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("GetCurrentTimeFunction");
                logger.LogInformation("GetCurrentTimeFunction called.");
                return DateTimeOffset.UtcNow;
            },
            name: "get_current_time",
            description: "Get the current UTC time."
        )
    ];
}
