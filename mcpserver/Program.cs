using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMcpServer()
    .WithHttpTransport(o =>
    {
        o.Stateless = true;
    })
    .WithTools<GetTimeApp>()
    .WithResources<GetTimeApp>()
    ;


var app = builder.Build();

app.MapGet("/", () => "Hello World!");
app.MapMcp("/mcp");

app.Run();


public class GetTimeApp()
{
    const string URI = "ui://get-time.html";

    [McpServerTool, Description("Gets the current time.")]
    [McpMeta("ui", JsonValue = $$"""{"resourceUri":"{{URI}}"}""")]
    public IEnumerable<ContentBlock> GetTime() =>
    [
        new TextContentBlock { Text = $"{DateTime.Now}" },
    ];
    
    [McpServerResource(UriTemplate = URI, MimeType = "text/html;profile=mcp-app")]
    public async Task<string> GetTimeUIResource() => await File.ReadAllTextAsync("/Users/Manuel.Naujoks/Projects/AgenticTodos/mcpserver/get-time-app/dist/get-time.html");
}