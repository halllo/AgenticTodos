var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.AgenticTodos_Backend>("AgenticTodos-Backend");

builder.AddNpmApp("AgenticTodos-Frontend", "../frontend")
    .WithUrl("http://localhost:4200");

builder.Build().Run();
