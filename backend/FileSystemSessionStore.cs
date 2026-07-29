using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;

namespace AgenticTodos.Backend;

/// <summary>
/// Persists agent sessions as one JSON file per (agent, session) pair, so a conversation survives a
/// backend restart.
/// </summary>
/// <remarks>
/// The parameter name matches the base class: what arrives here is a <i>session store id</i>, not a
/// conversation id the app chose. It reaches this store as the client's <c>RunAgentInput.ThreadId</c>
/// verbatim, and the SDK's contract is to treat it as opaque — no parsing, no character-set or length
/// constraints. Since it becomes half of a file name, <see cref="GetPath"/> escapes and bounds it
/// rather than validating it.
/// </remarks>
public class FileSystemSessionStore : AgentSessionStore
{
    private readonly string pathBase;
    private readonly ILogger<FileSystemSessionStore> logger;

    public FileSystemSessionStore(ILogger<FileSystemSessionStore> logger, string pathBase = "AgentSessions")
    {
        this.logger = logger;
        this.pathBase = pathBase;

    }

    public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
    {
        this.logger.LogInformation("Loading session {SessionStoreId}", sessionStoreId);
        var path = GetPath(sessionStoreId, agent.Id);
        if (!File.Exists(path))
        {
            return await agent.CreateSessionAsync(cancellationToken);
        }

        using var stream = File.OpenRead(path);
        var sessionContent = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: cancellationToken);
        return await agent.DeserializeSessionAsync(sessionContent, cancellationToken: cancellationToken);
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, string sessionStoreId, AgentSession session, CancellationToken cancellationToken = default)
    {
        this.logger.LogInformation("Saving session {SessionStoreId}", sessionStoreId);
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        Directory.CreateDirectory(this.pathBase);
        using var stream = File.Create(GetPath(sessionStoreId, agent.Id));
        await JsonSerializer.SerializeAsync(stream, serialized, cancellationToken: cancellationToken);
    }

    public override ValueTask DeleteSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
    {
        this.logger.LogInformation("Deleting session {SessionStoreId}", sessionStoreId);
        var path = GetPath(sessionStoreId, agent.Id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Both halves are percent-escaped <i>and</i> length-bounded before they become a file name.
    /// Neither is trustworthy: <paramref name="sessionStoreId"/> is client input, and
    /// <paramref name="agentId"/> is derived from the request route.
    /// <para>
    /// <b>Escaping</b> bounds the character set. It leaves the ids a real client sends untouched —
    /// unreserved characters (letters, digits, <c>-</c>, <c>_</c>, <c>.</c>, <c>~</c>) pass through, so
    /// <c>routed-amazonbedrock_thread_abc123.json</c> is still exactly that — while a separator or a
    /// <c>..</c> segment can no longer reach the file system.
    /// </para>
    /// <para>
    /// <b>Bounding</b> covers what escaping does not: the length. It has to be measured on the
    /// <i>escaped</i> form, because <see cref="Uri.EscapeDataString"/> expands every escapable character
    /// threefold (<c>" "</c> → <c>%20</c>) — so 42 spaces already exceed the same
    /// <see cref="MaxEscapedPartLength"/> budget that 124 plain characters fit inside. And the id is
    /// unbounded: <c>RunAgentInput.ThreadId</c> reaches this store verbatim, with MapAGUIServer
    /// substituting a GUID only when it is null or whitespace. A part over budget is therefore replaced
    /// by a digest of itself rather than rejected — no legitimate id becomes unusable, and the same id
    /// keeps mapping to the same file. Its readable form stays in the log line each operation writes.
    /// </para>
    /// <para>
    /// Without either guard <see cref="File.Create"/> throws — <see cref="PathTooLongException"/> for a
    /// long id, <see cref="DirectoryNotFoundException"/> for one containing <c>/</c> — from
    /// <see cref="SaveSessionAsync"/>, which the SDK calls only after the event enumerator is disposed,
    /// i.e. once the SSE response is committed and <see cref="AguiRunErrorMiddleware"/> can no longer
    /// report anything. The pre-stream path does not catch it first either:
    /// <see cref="File.Exists"/> answers false for an over-long path <i>without</i> throwing, so
    /// <see cref="GetSessionAsync"/> just starts a fresh session.
    /// </para>
    /// </summary>
    private string GetPath(string sessionStoreId, string agentId) =>
        Path.Combine(this.pathBase, $"{Bound(agentId)}_{Bound(sessionStoreId)}.json");

    /// <summary>
    /// Longest file-name component APFS, ext4 and NTFS all accept. Escaping emits ASCII only
    /// (non-ASCII is percent-encoded from its UTF-8 bytes), so counting characters counts bytes.
    /// </summary>
    private const int MaxFileNameLength = 255;

    /// <summary>
    /// What one escaped id may occupy: half of the budget left once the <c>_</c> joining the two (1)
    /// and the <c>.json</c> suffix (5) are subtracted.
    /// </summary>
    private const int MaxEscapedPartLength = (MaxFileNameLength - 1 - 5) / 2;

    /// <summary>Hex characters of SHA-256 kept for an over-long id: 32 of them are 128 bits.</summary>
    private const int DigestLength = 32;

    /// <summary>
    /// Escapes one id, substituting a truncated SHA-256 of the escaped form when it does not fit
    /// <see cref="MaxEscapedPartLength"/>. Deterministic, so a session saved under a long id is found
    /// again under it.
    /// </summary>
    /// <remarks>
    /// A client that sent a short id consisting of exactly those 32 hex characters would share a file
    /// with whichever long id digests to them. That is not a new exposure: sessions are keyed by the
    /// bare thread id, so knowing one is already enough to resume it (see
    /// <see cref="AGUIEndpoint.AddAGUISessionStore"/> on the app's threat model).
    /// </remarks>
    private static string Bound(string id)
    {
        var escaped = Uri.EscapeDataString(id);
        return escaped.Length <= MaxEscapedPartLength
            ? escaped
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(escaped)))[..DigestLength];
    }
}
