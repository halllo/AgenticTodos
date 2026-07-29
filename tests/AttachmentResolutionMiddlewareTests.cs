using AgenticTodos.Backend;
using Microsoft.Extensions.AI;

namespace AgenticTodos.Tests;

/// <summary>
/// The middleware that turns the frontend's hidden <c>[[agui-attachments:&lt;fileId&gt;,...]]</c> marker
/// into something the model may see. Every one of its contracts fails <b>silently</b> — the user still
/// gets a fluent answer, only with their attachment ignored, with the raw marker in it, or with a server
/// filesystem path in it — so nothing downstream would ever notice a regression.
/// <para>
/// Three contracts are pinned here. <b>The data-leak guard:</b> the model-visible text carries original
/// filenames and never <see cref="UploadedFileInfo.StoragePath"/>, which is the whole reason the record
/// is split across two channels. <b>The two channels themselves:</b> the rewritten text goes to the model
/// while the storage paths ride on <see cref="ChatMessage.AdditionalProperties"/>, which the provider
/// mappers do not forward but <c>FileSystemChatHistoryProvider</c> does persist. <b>Instance identity:</b>
/// the middleware sits outside the inner <c>ChatClientAgent</c> (which owns the history provider), so one
/// in-place edit serves both consumers only as long as the very same <see cref="ChatMessage"/> objects
/// travel on.
/// </para>
/// </summary>
public class AttachmentResolutionMiddlewareTests
{
    // A storage path shaped like the real one: UploadedFileStore names files by their GUID id under
    // UploadedFiles/, so the path is a server-local location that must never reach a model prompt.
    private const string ReportPath = "UploadedFiles/6f1c2a90d4b6431f9b0e2a7c5d8e3f41";
    private const string ReportId = "6f1c2a90d4b6431f9b0e2a7c5d8e3f41";
    private const string NotesPath = "UploadedFiles/0b7d5e1348af49c2a6f38d2b9c14e705";
    private const string NotesId = "0b7d5e1348af49c2a6f38d2b9c14e705";

    private static readonly UploadedFileInfo Report = new(ReportId, ReportPath, "quarterly-report.pdf", "application/pdf");
    private static readonly UploadedFileInfo Notes = new(NotesId, NotesPath, "notes.txt", "text/plain");

    // ---------------------------------------------------------------------------
    // The data-leak guard — the one failure mode with a blast radius beyond this feature
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TheModelVisibleText_CarriesFilenamesOnly_AndNeverTheStoragePath()
    {
        // AttachmentRecord holds FileId, FileName, StoragePath and ContentType side by side, and the
        // filename line is built from exactly one of them. Selecting the wrong property compiles, reads
        // fine, and ships the backend's filesystem layout into the model context (and from there into
        // whatever the model echoes back to the user) on every single attached file.
        var forwarded = await ResolveAsync(
            new FakeUploadedFileStore(Report, Notes),
            new ChatMessage(ChatRole.User, $"summarize these\n[[agui-attachments:{ReportId},{NotesId}]]"));

        var text = TextOf(Assert.Single(forwarded));

        Assert.Equal("summarize these\n[Attached files: quarterly-report.pdf, notes.txt]", text);

        // Spelled out separately from the equality above so a failure names the leak rather than a diff:
        // not the path, not the directory it lives in, and not the opaque file id either.
        Assert.DoesNotContain(ReportPath, text);
        Assert.DoesNotContain(NotesPath, text);
        Assert.DoesNotContain("UploadedFiles", text);
        Assert.DoesNotContain(ReportId, text);
        Assert.DoesNotContain(NotesId, text);

        // And the path is genuinely available on the message — it is withheld from the text on purpose,
        // not merely absent because resolution failed.
        Assert.Equal(
            [ReportPath, NotesPath],
            RecordsOf(forwarded[0]).Select(r => r.StoragePath));
    }

    // ---------------------------------------------------------------------------
    // Rewriting the text
    // ---------------------------------------------------------------------------

    [Theory]
    // How the frontend actually assembles it: the marker is appended to the user's text after a newline.
    [InlineData("look at this\n[[agui-attachments:ID]]", "look at this\n[Attached files: quarterly-report.pdf]")]
    // The leading newline is optional in the regex, and trailing whitespace after the marker is tolerated
    // — both so a change in how the wire content is assembled cannot leave the marker in the prompt.
    [InlineData("look at this [[agui-attachments:ID]]", "look at this\n[Attached files: quarterly-report.pdf]")]
    [InlineData("look at this\n[[agui-attachments:ID]]\n  ", "look at this\n[Attached files: quarterly-report.pdf]")]
    // An attachment sent with no prose at all: the text collapses to the filename line.
    [InlineData("[[agui-attachments:ID]]", "\n[Attached files: quarterly-report.pdf]")]
    public async Task TheMarkerIsStripped_AndTheFilenameLineTakesItsPlace(string sent, string expected)
    {
        var forwarded = await ResolveAsync(
            new FakeUploadedFileStore(Report),
            new ChatMessage(ChatRole.User, sent.Replace("ID", ReportId)));

        var text = TextOf(Assert.Single(forwarded));

        Assert.Equal(expected, text);
        Assert.DoesNotContain("agui-attachments", text);
    }

    [Fact]
    public async Task AnUnknownOrExpiredFileId_IsSkipped_ButTheMarkerIsStrippedAnyway()
    {
        // The store is a restart-surviving index, not a guarantee: an id from a stale browser tab
        // resolves to nothing. Leaving the marker in place would put "[[agui-attachments:...]]" in front
        // of the model, so stripping is unconditional and only the filename line is skipped.
        var forwarded = await ResolveAsync(
            new FakeUploadedFileStore(),
            new ChatMessage(ChatRole.User, "summarize these\n[[agui-attachments:long-gone]]"));

        var message = Assert.Single(forwarded);

        Assert.Equal("summarize these", TextOf(message));
        Assert.DoesNotContain("agui-attachments", TextOf(message));

        // Nothing resolved, so nothing is stashed — the key's presence means "there are paths here".
        Assert.False(message.AdditionalProperties?.ContainsKey(AttachmentResolutionMiddleware.AdditionalPropertiesKey) ?? false);
    }

    [Fact]
    public async Task AKnownAndAnUnknownId_YieldTheKnownOneOnly()
    {
        var forwarded = await ResolveAsync(
            new FakeUploadedFileStore(Notes),
            new ChatMessage(ChatRole.User, $"summarize these\n[[agui-attachments:long-gone,{NotesId}]]"));

        var message = Assert.Single(forwarded);

        Assert.Equal("summarize these\n[Attached files: notes.txt]", TextOf(message));
        Assert.Equal(["notes.txt"], RecordsOf(message).Select(r => r.FileName));
    }

    // ---------------------------------------------------------------------------
    // The second channel: storage paths for persistence, off the model's path
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TheResolvedRecords_LandInAdditionalProperties_UnderTheDocumentedKey()
    {
        // This is the half the model never sees and the persisted history keeps: drop the write and the
        // conversation still looks right in the browser, while the stored history has no way back to the
        // uploaded bytes — a data loss that only surfaces after a reload, on old sessions.
        var forwarded = await ResolveAsync(
            new FakeUploadedFileStore(Report, Notes),
            new ChatMessage(ChatRole.User, $"summarize these\n[[agui-attachments:{ReportId},{NotesId}]]"));

        var message = Assert.Single(forwarded);

        Assert.Equal("attachments", AttachmentResolutionMiddleware.AdditionalPropertiesKey);
        Assert.NotNull(message.AdditionalProperties);
        Assert.True(
            message.AdditionalProperties.ContainsKey(AttachmentResolutionMiddleware.AdditionalPropertiesKey),
            $"AdditionalProperties has no \"{AttachmentResolutionMiddleware.AdditionalPropertiesKey}\" entry, so the "
                + $"persisted history has no way back to the uploaded bytes. Keys present: "
                + $"[{string.Join(", ", message.AdditionalProperties.Keys)}]");

        // Every field of the store's record is carried over, in marker order — the download endpoint
        // needs the id and the content type, not just the path.
        Assert.Equal(
            [
                new AttachmentResolutionMiddleware.AttachmentRecord(ReportId, "quarterly-report.pdf", ReportPath, "application/pdf"),
                new AttachmentResolutionMiddleware.AttachmentRecord(NotesId, "notes.txt", NotesPath, "text/plain"),
            ],
            RecordsOf(message));
    }

    // ---------------------------------------------------------------------------
    // What must be left alone
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AMessageWithNoMarker_IsPassedThroughUntouched()
    {
        var store = new FakeUploadedFileStore(Report);
        var message = new ChatMessage(ChatRole.User, "add milk to the list");

        var forwarded = await ResolveAsync(store, message);

        Assert.Same(message, Assert.Single(forwarded));
        Assert.Equal("add milk to the list", TextOf(message));
        Assert.Null(message.AdditionalProperties);
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("system")]
    [InlineData("tool")]
    public async Task ANonUserMessage_IsLeftAlone_EvenIfItLooksLikeItCarriesAMarker(string role)
    {
        // Only the user turn can carry an upload, and the role check is the only thing that says so.
        // Point it at any other role and real attachments stop resolving altogether while replayed
        // history — which is full of assistant and tool messages — starts getting rewritten.
        var sent = $"echoing back\n[[agui-attachments:{ReportId}]]";

        var forwarded = await ResolveAsync(
            new FakeUploadedFileStore(Report),
            new ChatMessage(new ChatRole(role), sent));

        var message = Assert.Single(forwarded);

        Assert.Equal(sent, TextOf(message));
        Assert.Null(message.AdditionalProperties);
    }

    [Fact]
    public async Task NonUserMessagesKeepTheirPlace_AndOnlyTheUserTurnIsRewritten()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "you are helpful"),
            new ChatMessage(ChatRole.Assistant, "sure, send it over"),
            new ChatMessage(ChatRole.User, $"here it is\n[[agui-attachments:{NotesId}]]"),
        };

        var forwarded = await ResolveAsync(new FakeUploadedFileStore(Notes), messages);

        Assert.Equal(
            ["you are helpful", "sure, send it over", "here it is\n[Attached files: notes.txt]"],
            forwarded.Select(TextOf));
    }

    // ---------------------------------------------------------------------------
    // Instance identity — what makes one edit serve both the model call and persistence
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TheSameMessageInstancesContinueDownThePipeline()
    {
        // The middleware mutates in place and forwards; it never rebuilds. The inner ChatClientAgent
        // hands these very objects to both the provider and the ChatHistoryProvider, so a copy made
        // anywhere in between would split the two channels apart — the model would see one message and
        // the store would persist another.
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "you are helpful"),
            new ChatMessage(ChatRole.User, $"here it is\n[[agui-attachments:{ReportId}]]"),
        };

        var forwarded = await ResolveAsync(new FakeUploadedFileStore(Report), messages);

        Assert.Equal(messages.Length, forwarded.Count);
        Assert.Same(messages[0], forwarded[0]);
        Assert.Same(messages[1], forwarded[1]);

        // Both halves of the edit are visible on the caller's own object, not only on what was forwarded.
        Assert.Equal("here it is\n[Attached files: quarterly-report.pdf]", TextOf(messages[1]));
        Assert.Equal([ReportPath], RecordsOf(messages[1]).Select(r => r.StoragePath));
    }

    [Fact]
    public async Task ALazySourceIsMaterializedOnce_SoTheEditsSurviveToTheNextStage()
    {
        // The AG-UI server SDK happens to hand in a List today (ToChatRequestContext calls
        // AsChatMessages(...).ToList()), which is why dropping the materialization changes nothing in
        // production *yet*. A lazy source is what makes the class doc's argument observable: enumerate
        // it twice and the second pass yields freshly built messages, so every in-place edit is thrown
        // away and the raw marker reaches both the model and the persisted history.
        var enumerations = 0;

        IEnumerable<ChatMessage> Lazy()
        {
            enumerations++;
            yield return new ChatMessage(ChatRole.User, $"here it is\n[[agui-attachments:{ReportId}]]");
        }

        var forwarded = await ResolveAsync(new FakeUploadedFileStore(Report), Lazy());

        Assert.Equal(1, enumerations);
        Assert.Equal("here it is\n[Attached files: quarterly-report.pdf]", TextOf(Assert.Single(forwarded)));
    }

    // ---------------------------------------------------------------------------
    // Multi-block messages
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AMarkerOnALaterTextBlock_IsStillFound()
    {
        // The marker rides on whichever TextContent the conversion produced it on, and a message can
        // carry several content blocks (text plus attachments, or a text block per protocol delta).
        // Blocks without a marker are skipped rather than ending the search.
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent("first block"),
            new TextContent($"second block\n[[agui-attachments:{NotesId}]]"),
        ]);

        var forwarded = await ResolveAsync(new FakeUploadedFileStore(Notes), message);

        Assert.Equal(
            ["first block", "second block\n[Attached files: notes.txt]"],
            Assert.Single(forwarded).Contents.OfType<TextContent>().Select(c => c.Text));
        Assert.Equal([NotesPath], RecordsOf(message).Select(r => r.StoragePath));
    }

    // ---------------------------------------------------------------------------

    /// <summary>
    /// Drives the middleware exactly as <c>AIAgentBuilder.Use(sharedFunc:)</c> does and returns what
    /// reached the next stage, so the instance-identity assertions can compare against the input.
    /// </summary>
    private static Task<List<ChatMessage>> ResolveAsync(IUploadedFileStore store, params ChatMessage[] messages) =>
        ResolveAsync(store, (IEnumerable<ChatMessage>)messages);

    private static async Task<List<ChatMessage>> ResolveAsync(IUploadedFileStore store, IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage>? forwarded = null;

        await AttachmentResolutionMiddleware.Invoke(
            messages,
            session: null,
            options: null,
            next: (received, _, _, _) =>
            {
                forwarded = [.. received];
                return Task.CompletedTask;
            },
            cancellationToken: default,
            store);

        Assert.NotNull(forwarded);
        return forwarded;
    }

    /// <summary>The model-visible text of a message: every text block, in order.</summary>
    private static string TextOf(ChatMessage message) =>
        string.Concat(message.Contents.OfType<TextContent>().Select(c => c.Text));

    private static List<AttachmentResolutionMiddleware.AttachmentRecord> RecordsOf(ChatMessage message)
    {
        Assert.NotNull(message.AdditionalProperties);
        Assert.True(
            message.AdditionalProperties.ContainsKey(AttachmentResolutionMiddleware.AdditionalPropertiesKey),
            $"no resolved attachments under AdditionalProperties[\"{AttachmentResolutionMiddleware.AdditionalPropertiesKey}\"]");

        return Assert.IsType<List<AttachmentResolutionMiddleware.AttachmentRecord>>(
            message.AdditionalProperties[AttachmentResolutionMiddleware.AdditionalPropertiesKey]);
    }

    /// <summary>
    /// An <see cref="IUploadedFileStore"/> that only answers <c>TryGet</c> from a fixed set — enough to
    /// make the middleware a pure function of (messages, store) with no disk involved.
    /// </summary>
    private sealed class FakeUploadedFileStore(params UploadedFileInfo[] files) : IUploadedFileStore
    {
        private readonly Dictionary<string, UploadedFileInfo> byId =
            files.ToDictionary(f => f.FileId, StringComparer.Ordinal);

        public Task<UploadedFileInfo> SaveAsync(Stream content, string originalFileName, string? contentType, CancellationToken ct = default)
            => throw new NotSupportedException("The middleware only ever reads.");

        public bool TryGet(string fileId, out UploadedFileInfo info) => this.byId.TryGetValue(fileId, out info!);
    }
}
