using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace AgenticTodos.Backend;

/// <summary>
/// Write-only stream that intercepts an SSE response body and calls an injector for every
/// complete <c>data:</c> event. The injector controls what reaches the underlying stream:
/// <list type="bullet">
///   <item><c>null</c> — suppress the original event (write nothing).</item>
///   <item>empty sequence — forward the original event unchanged.</item>
///   <item>non-empty sequence — suppress the original event and emit these events instead.</item>
/// </list>
/// Non-<c>data:</c> SSE lines (comments, keep-alives) are always forwarded unchanged.
/// </summary>
[SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed",
    Justification = "_inner is the original response body stream — we do not own it")]
internal sealed class SseInterceptorStream : Stream
{
    private readonly Stream _inner;
    private readonly Func<string, IEnumerable<string>?> _injector;
    private readonly MemoryStream _buffer = new();

    public SseInterceptorStream(Stream inner, Func<string, IEnumerable<string>?> injector)
    {
        _inner = inner;
        _injector = injector;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    // SseFormatter only uses the async overload; synchronous Write is not expected.
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use WriteAsync instead.");

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _buffer.Write(buffer.Span);
        await ForwardCompleteEventsAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    /// <summary>
    /// Writes any bytes still in the buffer to the inner stream without attempting to parse them
    /// as SSE events. Call this after the downstream handler has finished so that non-SSE error
    /// responses (which may contain no <c>\n\n</c> boundary) are forwarded to the client rather
    /// than silently dropped.
    /// </summary>
    public async Task FlushRemainingAsync(CancellationToken cancellationToken = default)
    {
        if (_buffer.Length > 0)
        {
            await _inner.WriteAsync(_buffer.ToArray(), cancellationToken).ConfigureAwait(false);
            _buffer.SetLength(0);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _buffer.Dispose();
        base.Dispose(disposing);
    }

    private async Task ForwardCompleteEventsAsync(CancellationToken cancellationToken)
    {
        // SSE events are delimited by \n\n. Buffer until at least one complete event is available.
        string text = Encoding.UTF8.GetString(_buffer.ToArray());

        int lastBoundary = -1;
        int pos = 0;
        while ((pos = text.IndexOf("\n\n", pos, StringComparison.Ordinal)) >= 0)
        {
            lastBoundary = pos + 2;
            pos += 2;
        }

        if (lastBoundary < 0) return;

        string complete = text[..lastBoundary];
        string remaining = text[lastBoundary..];

        _buffer.SetLength(0);
        if (remaining.Length > 0)
            _buffer.Write(Encoding.UTF8.GetBytes(remaining));

        foreach (string evt in complete.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!evt.StartsWith("data: ", StringComparison.Ordinal))
            {
                // Non-data SSE lines are always forwarded unchanged.
                await _inner.WriteAsync(Encoding.UTF8.GetBytes(evt + "\n\n"), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            string json = evt["data: ".Length..];
            IEnumerable<string>? result = _injector(json);

            if (result is null)
            {
                // Null → suppress completely.
                continue;
            }

            // Materialise once to check whether the sequence is empty.
            IReadOnlyList<string> replacements = result as IReadOnlyList<string> ?? result.ToList();

            if (replacements.Count == 0)
            {
                // Empty → forward original unchanged.
                await _inner.WriteAsync(Encoding.UTF8.GetBytes(evt + "\n\n"), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // Non-empty → suppress original, emit replacements.
                foreach (string replacement in replacements)
                {
                    await _inner.WriteAsync(
                        Encoding.UTF8.GetBytes($"data: {replacement}\n\n"),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
