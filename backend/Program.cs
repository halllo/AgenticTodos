using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

OpenTelemetryExtensions.ConfigureOpenTelemetry(builder);
builder.Services.AddOpenApi();


var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/", () => "Hello World!");

app.Run();
