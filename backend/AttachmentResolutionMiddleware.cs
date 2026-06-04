using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Backend;

/// <summary>
/// Agent-level middleware that resolves file attachments carried as a hidden text marker on incoming user
/// messages. The frontend appends <c>[[agui-attachments:&lt;fileId&gt;,...]]</c> to the message text; this
/// middleware strips the marker, appends a model-visible <c>[Attached files: ...]</c> line built from the
/// original filenames, and stashes the resolved storage paths in <see cref="ChatMessage.AdditionalProperties"/>.
///
/// Because this runs outside the inner <c>ChatClientAgent</c> (which owns the ChatHistoryProvider), the same
/// mutated message flows to BOTH the model call and persistence: the model sees only the filenames, while the
/// persisted history keeps the storage paths (AdditionalProperties is not forwarded to the model).
///
/// Mirrors the marker-in-text idiom used by <see cref="DetectMcpAppsActivityMiddleware"/>.
/// </summary>
public static class AttachmentResolutionMiddleware
{
    public const string AdditionalPropertiesKey = "attachments";

    // End-anchored marker, e.g. "\n[[agui-attachments:abc,def]]". The leading newline is optional so the
    // regex is tolerant of how the frontend assembles the wire content.
    private static readonly Regex MarkerRegex = new(
        @"\n?\[\[agui-attachments:([^\]]*)\]\]\s*$",
        RegexOptions.Compiled);

    public sealed record AttachmentRecord(string FileId, string FileName, string StoragePath, string ContentType);

    public static Task Invoke(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, CancellationToken, Task> next,
        CancellationToken cancellationToken,
        IUploadedFileStore store)
    {
        // Materialize once: the incoming sequence is a lazy iterator (AG-UI's AsChatMessages yields).
        // We must mutate and forward the SAME instances — re-enumerating the lazy source would rebuild
        // fresh ChatMessage objects and discard our mutations.
        var materialized = messages.ToList();

        foreach (var message in materialized)
        {
            if (message.Role == ChatRole.User)
            {
                ResolveAttachments(message, store);
            }
        }

        return next(materialized, session, options, cancellationToken);
    }

    private static void ResolveAttachments(ChatMessage message, IUploadedFileStore store)
    {
        foreach (var textContent in message.Contents.OfType<TextContent>())
        {
            var text = textContent.Text ?? string.Empty;
            var match = MarkerRegex.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var fileIds = match.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var resolved = new List<AttachmentRecord>();
            foreach (var fileId in fileIds)
            {
                if (store.TryGet(fileId, out var info))
                {
                    resolved.Add(new AttachmentRecord(info.FileId, info.OriginalFileName, info.StoragePath, info.ContentType));
                }
                // Unknown/expired ids are skipped silently; the marker is stripped regardless so the model never sees the id.
            }

            // Strip the marker; append a model-visible filename line (filenames only — never the storage path).
            var stripped = text[..match.Index].TrimEnd();
            var suffix = resolved.Count > 0
                ? $"\n[Attached files: {string.Join(", ", resolved.Select(r => r.FileName))}]"
                : string.Empty;
            textContent.Text = stripped + suffix;

            if (resolved.Count > 0)
            {
                message.AdditionalProperties ??= [];
                message.AdditionalProperties[AdditionalPropertiesKey] = resolved;
            }

            // Only one marker per message (end-anchored); stop after the first match.
            break;
        }
    }
}
