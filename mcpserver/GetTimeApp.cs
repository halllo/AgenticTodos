using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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
    public async Task<string> GetTimeUIResource() =>
        await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "get-time-app", "dist", "get-time.html"));
}