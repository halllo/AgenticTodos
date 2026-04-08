var builder = DistributedApplication.CreateBuilder(args);

var mcpserver = builder.AddProject<Projects.AgenticTodos_McpServer>("AgenticTodos-McpServer");

var backend = builder.AddProject<Projects.AgenticTodos_Backend>("AgenticTodos-Backend")
    .WithReference(mcpserver);

var element = builder.AddViteApp("AgenticTodos-Frontend", "../frontend")
    .WithEndpoint("http", (endpointAnnotation) =>
    {
        endpointAnnotation.Port = 3000;
    })
    .WithReference(backend);

builder.Build().Run();
