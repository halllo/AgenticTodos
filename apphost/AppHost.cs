var builder = DistributedApplication.CreateBuilder(args);

var backend = builder.AddProject<Projects.AgenticTodos_Backend>("AgenticTodos-Backend");

var element = builder.AddViteApp("AgenticTodos-Frontend", "../frontend")
    .WithEndpoint("http", (endpointAnnotation) =>
    {
        endpointAnnotation.Port = 3000;
    })
    .WithReference(backend);

builder.Build().Run();
