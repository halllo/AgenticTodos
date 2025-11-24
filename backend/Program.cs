using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using OpenAI;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

OpenTelemetryExtensions.ConfigureOpenTelemetry(builder);
builder.Services.AddOpenApi();
builder.Services.AddAGUI();


var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/", () => "Hello World!");
app.MapAGUI("/agui", CreateAgent(builder));

app.Run();


static AIAgent CreateAgent(WebApplicationBuilder builder)
{
    var agent = new OpenAIClient(builder.Configuration["OPENAI_API_KEY"] ?? throw new InvalidOperationException("OPENAI_API_KEY is not set."))
       .GetChatClient("gpt-4o")
       .AsIChatClient()
       .CreateAIAgent(
           name: "AGUIAssistant",
           tools: [
               AIFunctionFactory.Create(
                () => DateTimeOffset.UtcNow,
                name: "get_current_time",
                description: "Get the current UTC time."
            )
           ]);
    return agent;
}