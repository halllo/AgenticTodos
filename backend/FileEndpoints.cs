namespace AgenticTodos.Backend;

/// <summary>
/// Upload/download endpoints for chat file attachments. Kept under <c>/agents/*</c> so the frontend dev proxy
/// (frontend/src/proxy.conf.json) forwards them to the backend. No route conflict: <c>/agents</c> is exact-match,
/// AG-UI is <c>/agents/routed/...</c>, the MCP relay is <c>/agents/mcp-relay</c>.
/// </summary>
public static class FileEndpoints
{
    // Cap multipart uploads. Adjust if larger attachments are needed.
    private const long MaxUploadBytes = 50 * 1024 * 1024; // 50 MB

    public static WebApplication MapFileEndpoints(this WebApplication app)
    {
        app.MapPost("/agents/files", async (HttpRequest request, IUploadedFileStore store, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest("Expected multipart/form-data.");
            }

            var form = await request.ReadFormAsync(ct);
            var results = new List<object>();
            foreach (var f in form.Files)
            {
                if (f.Length > MaxUploadBytes)
                {
                    return Results.BadRequest($"File '{f.FileName}' exceeds the {MaxUploadBytes / (1024 * 1024)} MB limit.");
                }

                await using var s = f.OpenReadStream();
                var info = await store.SaveAsync(s, f.FileName, f.ContentType, ct);
                results.Add(new { fileId = info.FileId, fileName = info.OriginalFileName });
            }

            return Results.Ok(results);
        })
        .DisableAntiforgery()
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUploadBytes));

        app.MapGet("/agents/files/{fileId}", (string fileId, IUploadedFileStore store) =>
            store.TryGet(fileId, out var info) && File.Exists(info.StoragePath)
                ? Results.File(File.OpenRead(info.StoragePath), info.ContentType, fileDownloadName: info.OriginalFileName)
                : Results.NotFound());

        return app;
    }
}
