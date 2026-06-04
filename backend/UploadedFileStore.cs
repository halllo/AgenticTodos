using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;

namespace AgenticTodos.Backend;

public sealed record UploadedFileInfo(string FileId, string StoragePath, string OriginalFileName, string ContentType);

public interface IUploadedFileStore
{
    Task<UploadedFileInfo> SaveAsync(Stream content, string originalFileName, string? contentType, CancellationToken ct = default);

    bool TryGet(string fileId, out UploadedFileInfo info);
}

/// <summary>
/// Stores uploaded files on disk under <c>UploadedFiles/</c> (sibling to <c>ChatHistories/</c> and
/// <c>AgentSessions/</c>). Each file is stored under its generated GUID id only — the original filename is
/// never used as a path segment, which prevents path-traversal. A metadata index (<c>_index.json</c>) is
/// persisted so downloads and history path-resolution survive a backend restart.
/// </summary>
public sealed class UploadedFileStore : IUploadedFileStore
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string pathBase;
    private readonly string indexPath;
    private readonly object indexLock = new();
    private readonly Lazy<ConcurrentDictionary<string, UploadedFileInfo>> index;

    public UploadedFileStore(string pathBase = "UploadedFiles")
    {
        this.pathBase = pathBase;
        this.indexPath = Path.Combine(pathBase, "_index.json");
        this.index = new Lazy<ConcurrentDictionary<string, UploadedFileInfo>>(LoadIndex);
    }

    public async Task<UploadedFileInfo> SaveAsync(Stream content, string originalFileName, string? contentType, CancellationToken ct = default)
    {
        Directory.CreateDirectory(this.pathBase);

        var fileId = Guid.NewGuid().ToString("N");
        var storagePath = Path.Combine(this.pathBase, fileId);

        await using (var dest = File.Create(storagePath))
        {
            await content.CopyToAsync(dest, ct);
        }

        var resolvedContentType = ResolveContentType(originalFileName, contentType);
        var safeName = Path.GetFileName(originalFileName); // strip any directory components from the display name
        var info = new UploadedFileInfo(fileId, storagePath, safeName, resolvedContentType);

        this.index.Value[fileId] = info;
        PersistIndex();

        return info;
    }

    public bool TryGet(string fileId, out UploadedFileInfo info) => this.index.Value.TryGetValue(fileId, out info!);

    private static string ResolveContentType(string originalFileName, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType) && contentType != "application/octet-stream")
        {
            return contentType;
        }

        return ContentTypeProvider.TryGetContentType(originalFileName, out var mapped)
            ? mapped
            : "application/octet-stream";
    }

    private ConcurrentDictionary<string, UploadedFileInfo> LoadIndex()
    {
        if (!File.Exists(this.indexPath))
        {
            return new ConcurrentDictionary<string, UploadedFileInfo>(StringComparer.Ordinal);
        }

        try
        {
            using var stream = File.OpenRead(this.indexPath);
            var entries = JsonSerializer.Deserialize<List<UploadedFileInfo>>(stream) ?? [];
            return new ConcurrentDictionary<string, UploadedFileInfo>(
                entries.Select(e => new KeyValuePair<string, UploadedFileInfo>(e.FileId, e)),
                StringComparer.Ordinal);
        }
        catch
        {
            return new ConcurrentDictionary<string, UploadedFileInfo>(StringComparer.Ordinal);
        }
    }

    private void PersistIndex()
    {
        lock (this.indexLock)
        {
            Directory.CreateDirectory(this.pathBase);
            using var stream = File.Create(this.indexPath);
            JsonSerializer.Serialize(stream, this.index.Value.Values.ToList(), JsonOptions);
        }
    }
}
