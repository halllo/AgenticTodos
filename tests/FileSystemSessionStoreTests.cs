using AgenticTodos.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticTodos.Tests;

/// <summary>
/// The session store turns two untrusted strings into a file name: the agent id (derived from the
/// request route) and the session store id, which is the client's <c>RunAgentInput.ThreadId</c>
/// verbatim. The SDK's contract is to treat the latter as opaque, so the store has to make it safe
/// rather than reject it.
/// </summary>
public class FileSystemSessionStoreTests : IDisposable
{
    private readonly string pathBase = Path.Combine(
        Path.GetTempPath(), "agentic-todos-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveThenGet_RoundTripsTheSession()
    {
        var store = NewStore();
        var agent = NewAgent();
        var session = await agent.CreateSessionAsync();

        await store.SaveSessionAsync(agent, "thread_abc123", session);
        var loaded = await store.GetSessionAsync(agent, "thread_abc123");

        Assert.NotNull(loaded);
        Assert.Single(Directory.GetFiles(this.pathBase));
    }

    [Fact]
    public async Task UnknownSession_StartsAFreshOne()
    {
        var store = NewStore();
        var agent = NewAgent();

        var session = await store.GetSessionAsync(agent, "thread_never_seen");

        Assert.NotNull(session);
        Assert.False(Directory.Exists(this.pathBase));
    }

    [Fact]
    public async Task Delete_RemovesTheFile()
    {
        var store = NewStore();
        var agent = NewAgent();
        await store.SaveSessionAsync(agent, "thread_abc123", await agent.CreateSessionAsync());

        await store.DeleteSessionAsync(agent, "thread_abc123");

        Assert.Empty(Directory.GetFiles(this.pathBase));
    }

    [Theory]
    [InlineData("../../../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("with:colon")]
    [InlineData("with space")]
    public async Task HostileSessionStoreId_StaysInsideThePathBase(string sessionStoreId)
    {
        // Without escaping, a '/' makes File.Create throw from inside SaveSessionAsync — which the AG-UI
        // endpoint calls *after* the SSE response has started, so the stream dies with no RUN_FINISHED
        // and AguiRunErrorMiddleware can no longer report it.
        var store = NewStore();
        var agent = NewAgent();

        await store.SaveSessionAsync(agent, sessionStoreId, await agent.CreateSessionAsync());

        var written = Assert.Single(Directory.GetFiles(this.pathBase));
        Assert.Equal(
            Path.GetFullPath(this.pathBase),
            Path.GetFullPath(Path.GetDirectoryName(written)!));

        // And it is still the same session on the way back out.
        Assert.NotNull(await store.GetSessionAsync(agent, sessionStoreId));
    }

    [Fact]
    public async Task OrdinaryIds_KeepTheirReadableFileName()
    {
        // Escaping must not churn the names of sessions already on disk: unreserved characters pass
        // through untouched.
        var store = NewStore();
        var agent = NewAgent();

        await store.SaveSessionAsync(agent, "thread_abc-123", await agent.CreateSessionAsync());

        var written = Path.GetFileName(Assert.Single(Directory.GetFiles(this.pathBase)));
        Assert.Equal($"{agent.Id}_thread_abc-123.json", written);
    }

    [Fact]
    public async Task DistinctThreads_GetDistinctFiles()
    {
        var store = NewStore();
        var agent = NewAgent();

        await store.SaveSessionAsync(agent, "thread_one", await agent.CreateSessionAsync());
        await store.SaveSessionAsync(agent, "thread_two", await agent.CreateSessionAsync());

        Assert.Equal(2, Directory.GetFiles(this.pathBase).Length);
    }

    [Theory]
    // Long enough that no per-part budget could accommodate it.
    [InlineData(5000, 'x')]
    // The subtler one, and the reason the bound is on the escaped length rather than the raw length:
    // 80 characters is well inside any raw-length budget, but Uri.EscapeDataString expands each space
    // to %20, so the escaped form is 240 characters and the composed name is over the limit.
    [InlineData(80, ' ')]
    public async Task AnOverlongSessionStoreId_StillSaves_UnderAFileNameTheFileSystemAccepts(int length, char fill)
    {
        // Unbounded on the wire: RunAgentInput.ThreadId reaches this store verbatim, and MapAGUIServer
        // substitutes a GUID only when it is null or whitespace. Without the bound File.Create throws
        // PathTooLongException from inside SaveSessionAsync — which the AG-UI endpoint calls only after
        // the SSE response is committed, so the stream dies with no RUN_FINISHED and
        // AguiRunErrorMiddleware can no longer report anything.
        var store = NewStore();
        var agent = NewAgent();
        var sessionStoreId = new string(fill, length);

        await store.SaveSessionAsync(agent, sessionStoreId, await agent.CreateSessionAsync());

        var written = Path.GetFileName(Assert.Single(Directory.GetFiles(this.pathBase)));

        // 255 bytes is the longest component APFS, ext4 and NTFS all accept. Bytes, not characters:
        // escaping emits ASCII only, so the two coincide today — asserting the byte count keeps the
        // test honest if that ever stops being true.
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(written) <= 255,
            $"File name is {System.Text.Encoding.UTF8.GetByteCount(written)} bytes, over the 255-byte " +
            $"component limit: {written}");

        // Deterministic, or a session saved under a long id could never be found again.
        await store.SaveSessionAsync(agent, sessionStoreId, await agent.CreateSessionAsync());
        Assert.Equal(written, Path.GetFileName(Assert.Single(Directory.GetFiles(this.pathBase))));

        // And it is a session on the way back out, not a fresh one — the same id resolves to the same
        // file for reads as for writes.
        Assert.NotNull(await store.GetSessionAsync(agent, sessionStoreId));
    }

    [Fact]
    public async Task AnIdThatFitsTheBudget_IsNotDigested()
    {
        // The bound must not over-trigger. A hundred characters is long for a thread id and still fits,
        // so the file name stays the readable thing an operator can match against the log line — which
        // is the only reason to escape-and-bound rather than hash everything.
        var store = NewStore();
        var agent = NewAgent();
        var sessionStoreId = new string('x', 100);

        await store.SaveSessionAsync(agent, sessionStoreId, await agent.CreateSessionAsync());

        Assert.Equal(
            $"{agent.Id}_{sessionStoreId}.json",
            Path.GetFileName(Assert.Single(Directory.GetFiles(this.pathBase))));
    }

    [Fact]
    public async Task TwoDifferentOverlongIds_StillGetDifferentFiles()
    {
        // The digest has to be of the id, not a constant: collapsing every over-long id onto one file
        // would let two conversations overwrite each other, which is worse than the exception.
        var store = NewStore();
        var agent = NewAgent();

        await store.SaveSessionAsync(agent, new string('x', 5000), await agent.CreateSessionAsync());
        await store.SaveSessionAsync(agent, new string('y', 5000), await agent.CreateSessionAsync());

        Assert.Equal(2, Directory.GetFiles(this.pathBase).Length);
    }

    [Fact]
    public async Task AnOverlongAgentId_IsBoundedToo()
    {
        // The other half of the file name. It is derived from the request route rather than sent as a
        // field, so it is shorter in practice — but it is composed from request input all the same, and
        // 255 bytes is the budget for the two halves together.
        var store = NewStore();
        var agent = new RenamedAgent(NewAgent(), new string('a', 5000));

        await store.SaveSessionAsync(agent, "thread_abc123", await agent.CreateSessionAsync());

        var written = Path.GetFileName(Assert.Single(Directory.GetFiles(this.pathBase)));
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(written) <= 255,
            $"File name is {System.Text.Encoding.UTF8.GetByteCount(written)} bytes: {written}");

        // The readable half survives the other half being digested — the two are bounded independently.
        Assert.EndsWith("_thread_abc123.json", written);
    }

    private FileSystemSessionStore NewStore() =>
        new(NullLogger<FileSystemSessionStore>.Instance, this.pathBase);

    private static AIAgent NewAgent() => new NoopChatClient().AsAIAgent();

    /// <summary>An agent with an id of the test's choosing; everything else forwards to a real one.</summary>
    private sealed class RenamedAgent(AIAgent inner, string id) : DelegatingAIAgent(inner)
    {
        protected override string? IdCore => id;
    }

    public void Dispose()
    {
        if (Directory.Exists(this.pathBase))
        {
            Directory.Delete(this.pathBase, recursive: true);
        }
    }
}
