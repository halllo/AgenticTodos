var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("agentic-todos");

var mcpserver = builder.AddProject<Projects.AgenticTodos_McpServer>("AgenticTodos-McpServer")
    .WithExternalHttpEndpoints()
    ;

var backend = builder.AddProject<Projects.AgenticTodos_Backend>("AgenticTodos-Backend")
    .WithReference(mcpserver)
    .WithExternalHttpEndpoints()
    ;

var element = builder.AddViteApp("AgenticTodos-Frontend", "../frontend")
    .WithEndpoint("http", (endpointAnnotation) =>
    {
        endpointAnnotation.Port = 3000;
    })
    .WithReference(backend)
    .WithExternalHttpEndpoints();

#pragma warning disable ASPIREJAVASCRIPT001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
element.PublishAsStaticWebsite("/agents", backend, options =>
    {
        options.OutputPath = "dist/agentic-todos/browser";
    });
#pragma warning restore ASPIREJAVASCRIPT001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

builder.Build().Run();
