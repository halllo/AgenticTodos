var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithMcpApp<GetTimeApp>()
    .WithMcpApp<ThreejsApp>()
    .WithMcpApp<PdfViewerApp>()
    ;

var app = builder.Build();

app.MapGet("/", () => "Hello World!");
app.MapGet("/ping", () => Results.Ok());
app.MapMcp("/mcp");

app.Run();

public static class McpAppsExtensions
{
  extension(IMcpServerBuilder mcpServerBuilder)
  {
    public IMcpServerBuilder WithMcpApp<TApp>() => mcpServerBuilder.WithTools<TApp>().WithResources<TApp>();
  }
}