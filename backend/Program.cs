using AGUI.Abstractions;
using AgenticTodos.Backend;
using Amazon.BedrockRuntime;
using EUAIActClassifier;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

OpenTelemetryExtensions.ConfigureOpenTelemetry(builder);
builder.Services.AddOpenApi();
builder.Services.AddAGUIServer();
// The app's own AG-UI content types, on the endpoint's JsonSerializerOptions — see AddAGUIJson.
builder.Services.AddAGUIJson();

builder.Services.AddSingleton(_ =>
    new Lazy<Task<AIFunction[]>>(() => GetTools(builder.Configuration)));
builder.Services.AddKeyedSingleton("openai", (sp, key) => CreateAgent(
    chatClient: OpenAI(builder.Configuration, builder.Environment.ApplicationName),
    tools: sp.GetRequiredService<Lazy<Task<AIFunction[]>>>().Value.GetAwaiter().GetResult(),
    services: sp));
builder.Services.AddKeyedSingleton("amazonbedrock", (sp, key) => CreateAgent(
    chatClient: AmazonBedrock(builder.Configuration, sp),
    tools: sp.GetRequiredService<Lazy<Task<AIFunction[]>>>().Value.GetAwaiter().GetResult(),
    services: sp,
    // Claude Sonnet on Bedrock supports extended thinking. The AWS MEAI adapter maps this to
    // AdditionalModelRequestFields["thinking"] and the AGUI extension streams the resulting
    // TextReasoningContent as REASONING_* events. (gpt-4o has no reasoning, so OpenAI is left unset.)
    reasoning: new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Full },
    // An explicit output cap is REQUIRED for ExtraHigh: the adapter derives the thinking budget from
    // MaxTokens (ExtraHigh => budget = MaxTokens - 1) and, when MaxTokens is unset, auto-raises it to
    // budget*4 = 131072 — which exceeds Claude's 128000 output limit and is rejected. Pinning it to
    // the model limit keeps the request valid; the budget is a ceiling, so short answers still fit.
    maxOutputTokens: 128_000));

builder.Services.AddKeyedSingleton("agentAliases", builder.Services
    .Where(sd => sd.IsKeyedService && sd.ServiceType == typeof(AIAgent))
    .Select(sd => sd.ServiceKey?.ToString())
    .Where(key => key is not null && key != "*")
    .Select(key => key!)
    .OrderBy(key => key)
    .ToList());
builder.Services.AddScoped<IAgentProvider, AgentProvider>();
// The store the app actually uses. Its lifetime is free to change — AddAGUISessionStore() puts a
// forwarding stand-in between it and the endpoint, which resolves it per request; the endpoint itself
// could only ever hold a singleton.
builder.Services.AddSingleton<AgentSessionStore, FileSystemSessionStore>();
builder.Services.AddSingleton<IUploadedFileStore, UploadedFileStore>();
builder.Services.AddAGUISessionStore();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();



var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/", () => "Hello Agents!");
app.MapGet("/ping", () => Results.Ok());
app.MapGet("/agents", async (IAgentProvider agents, CancellationToken ct) => await agents.GetAliasesAsync(ct));
app.MapFileEndpoints();

// CSP headers for the outer sandbox iframe — built dynamically from the ?csp= query param
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.Equals("/sandbox.html", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        var csp = ctx.Request.Query["csp"].FirstOrDefault()?.ToMcpUiResourceCsp();
        ctx.Response.Headers["Content-Security-Policy"] = csp.BuildHeader();
    }
    await next();
});
app.UseStaticFiles();

// Transparent HTTP proxy that forwards MCP Streamable HTTP traffic to the MCP server.
// A terminal app.Use() branch rather than a mapped endpoint: the MCP Streamable HTTP transport needs
// GET (SSE) as well as POST on this path, and an endpoint under /agents answered GET with 405.
// Middleware short-circuits before endpoint execution, so it cannot collide with the AG-UI endpoint.
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

// This app once also mapped one static AG-UI endpoint per agent (/agents/static/{alias}/agui). They
// were dropped for having no session management, and the API they used is gone with the discontinued
// Microsoft.Agents.AI.AGUI package: the hosting package offers MapAGUIServer only.

// A failure inside an AG-UI request has to reach the client as RUN_STARTED + RUN_ERROR; an HTTP 500
// with a non-SSE body is invisible to every AG-UI client. Registered before the endpoint mapping so
// the catch surrounds endpoint execution. The prefix comes from AGUIEndpoint so it cannot drift away
// from the route the endpoint is actually mapped on.
app.UseAguiRunErrorStream(AGUIEndpoint.RoutedPathPrefix);

// Routing agent (suggested workaround)
app.MapAGUIViaHttpRoutingAgent();

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
            // "eu.anthropic.claude-sonnet-4-5-20250929-v1:0"
            "eu.anthropic.claude-sonnet-4-6"
        )
        .AsBuilder()
        .UseOpenTelemetry(sourceName: applicationName, configure: c => c.EnableSensitiveData = true)
        // .ConfigureOptions(c =>
        // {
        //     c.AllowMultipleToolCalls = false; // does not seem to have any effect
        // })
        // Two app-internal objects ride on ChatOptions.AdditionalProperties by the time a request
        // reaches the provider: the whole RunAgentInput (stashed by the AG-UI server SDK under an
        // internal key, hence the match by value type) and this app's ConversationState (published for
        // tools by StateSnapshotMiddleware, then copied into ChatOptions by ChatClientAgent). Neither
        // belongs in a model request — an adapter that forwards AdditionalProperties as
        // AdditionalModelRequestFields makes Claude reject the call with "Extra inputs are not
        // permitted". AWSSDK.Extensions.Bedrock.MEAI 4.0.101.7 happens not to read AdditionalProperties
        // at all, so today this is defence-in-depth rather than load-bearing.
        .Use(client => new OmitAdditionalPropertiesMiddleware(
            inner: client,
            propertyValueTypesToOmit: [typeof(RunAgentInput), typeof(StateSnapshotMiddleware.ConversationState)]))
        .Use(client => new ConsolidateToolResultsMiddleware(inner: client))
        .Use((client, services) => new LoggingMiddleware(inner: client, logger: services.GetRequiredService<ILogger<LoggingMiddleware>>()))
        .Build(services)
        ;
}

static AIAgent CreateAgent(IChatClient chatClient, AIFunction[] tools, IServiceProvider services, IChatClient? classifier = null, ReasoningOptions? reasoning = null, int? maxOutputTokens = null)
{
    var applicationName = services.GetRequiredService<IHostEnvironment>().ApplicationName;
    var fileStore = services.GetRequiredService<IUploadedFileStore>();
    return chatClient
        .AsAIAgent(
            options: new ChatClientAgentOptions
            {
                Name = "AGUIAssistant",
                ChatOptions = new ChatOptions()
                {
                    Tools = tools,
                    Reasoning = reasoning,
                    MaxOutputTokens = maxOutputTokens,
                },
                // History lives behind ChatHistoryProvider rather than an IConversationStorage: the
                // provider seam is the one ChatClientAgent consults on every run, and it is where this
                // app's two history repairs hook in (see FileSystemChatHistoryProvider).
                ChatHistoryProvider = new FileSystemChatHistoryProvider(),
                AIContextProviders = [],
            },
            services: services)
        .AsBuilder()
        .UseOpenTelemetry(sourceName: applicationName, configure: c => c.EnableSensitiveData = true)
        // Order matters: the interrupt middleware (outer) translates approval content to/from the
        // AG-UI interrupt/resume wire format; ToolApprovalAgent (inner) applies "always allow" rules
        // and queues multi-approval batches, persisting ToolApprovalState in the session.
        .UseToolApprovalInterrupts()
        .UseToolApproval(new ToolApprovalAgentOptions())
        .UseAttachmentResolution(fileStore)
        .UseOmitEmptyMessages()
        .UseStateSnapshot()
        .UseDetectMcpAppsActivity()
        .UseEUAIActRiskActivity()
        // No caller passes a classifier today — both keyed registrations omit it — so classification
        // runs on the agent's own client. The parameter stays as the seam for pointing it at a cheaper
        // model: it is the one step in the pipeline that has no need of the conversational model.
        .Use(inner => inner.UseEUAIActClassification(classifier ?? chatClient))
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

    // Tools listed under HumanInTheLoop:ApprovalRequiredTools pause for user approval before
    // executing (see human-in-the-loop.md). Works for local functions and MCP tools alike.
    var approvalRequired = configuration.GetSection("HumanInTheLoop:ApprovalRequiredTools").Get<string[]>() ?? [];
    AIFunction Gate(AIFunction function) =>
        approvalRequired.Contains(function.Name, StringComparer.OrdinalIgnoreCase)
            ? new ApprovalRequiredAIFunction(function)
            : function;

    return [
        .. mcpTools.Select(Gate),

        Gate(AIFunctionFactory.Create(
            name: "increment_counter",
            description: "Increment the counter.",
            method: (IServiceProvider services) =>
            {
                var loggerFactory = services.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("IncrementCounterFunction");

                // TryGetState, not a direct index: AdditionalPropertiesDictionary's indexer throws on a
                // missing key, and a run that carried no state has none.
                StateSnapshotMiddleware.TryGetState(AIAgent.CurrentRunContext?.RunOptions, out var state);
                if (state != null)
                {
                    state.Counter++;
                }

                logger.LogInformation("IncrementCounterFunction called. Counter: {Counter}", state?.Counter);

                return state?.Counter;
            }
        ))
    ];
}
