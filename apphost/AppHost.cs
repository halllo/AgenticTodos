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
    .WithExternalHttpEndpoints()
    ;

builder.Build().Run();
