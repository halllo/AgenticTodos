using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

public class PdfViewerApp
{
    const string URI = "ui://pdf-viewer/mcp-app.html";
    const int MaxChunkBytes = 512 * 1024;

    [McpServerTool, Description("Display an interactive PDF viewer for the given URL or absolute local file path. Supports navigation, zoom, text search, and annotations.")]
    [McpMeta("ui", JsonValue = $$"""{"resourceUri":"{{URI}}"}""")]
    public IEnumerable<ContentBlock> display_pdf(
        [Description("HTTP/HTTPS URL or absolute local file path of the PDF to display")] string url,
        [Description("Viewer height in pixels")] int height = 600)
    {
        var data = System.Text.Json.JsonSerializer.Serialize(new { url, height });
        return [new TextContentBlock { Text = data }];
    }

    [McpServerTool, Description("Read a byte range from a PDF URL or local file. Called by the viewer frontend to load PDFs in chunks.")]
    public async Task<IEnumerable<ContentBlock>> read_pdf_bytes(
        [Description("HTTP/HTTPS URL or absolute local file path")] string url,
        [Description("Start offset in bytes")] int offset,
        [Description("Number of bytes to read")] int byteCount)
    {
        byteCount = Math.Min(byteCount, MaxChunkBytes);

        byte[] data;
        long totalLength;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var client = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(offset, offset + byteCount - 1);
            var response = await client.SendAsync(request);
            data = await response.Content.ReadAsByteArrayAsync();

            totalLength = response.Content.Headers.ContentRange?.Length ?? -1;
            if (totalLength < 0)
            {
                // Range not supported — fall back to full download for size probe
                totalLength = response.Content.Headers.ContentLength ?? -1;
            }
        }
        else
        {
            var path = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(url).LocalPath
                : url;

            var fileInfo = new FileInfo(path);
            totalLength = fileInfo.Length;

            var readCount = (int)Math.Min(byteCount, totalLength - offset);
            data = new byte[readCount];
            using var fs = File.OpenRead(path);
            fs.Seek(offset, SeekOrigin.Begin);
            _ = await fs.ReadAsync(data);
        }

        var result = System.Text.Json.JsonSerializer.Serialize(new
        {
            bytes = Convert.ToBase64String(data),
            offset,
            byteCount = data.Length,
            totalBytes = totalLength,
            hasMore = offset + data.Length < totalLength,
        });
        return [new TextContentBlock { Text = result }];
    }

    [McpServerResource(UriTemplate = URI, MimeType = "text/html;profile=mcp-app")]
    public async Task<ReadResourceResult> PdfViewerUIResource()
    {
        var html = await File.ReadAllTextAsync(
            Path.Combine(Directory.GetCurrentDirectory(), "pdf-viewer-app", "dist", "mcp-app.html"));

        return new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = URI,
                    MimeType = "text/html;profile=mcp-app",
                    Text = html,
                    Meta = new JsonObject
                    {
                        ["ui"] = new JsonObject
                        {
                            ["permissions"] = new JsonObject
                            {
                                ["clipboardWrite"] = new JsonObject(),
                            },
                            ["csp"] = new JsonObject
                            {
                                // PDF.js fetches Standard-14 fonts from unpkg.com two ways:
                                // fetch() the .ttf bytes (connect-src) and FontFace url() (font-src).
                                ["connectDomains"] = new JsonArray("https://unpkg.com"),
                                ["resourceDomains"] = new JsonArray("https://unpkg.com"),
                            },
                        },
                    },
                },
            ],
        };
    }
}
