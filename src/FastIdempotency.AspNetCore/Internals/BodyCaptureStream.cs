using Microsoft.IO;

namespace FastIdempotency.AspNetCore.Internals;

/// <summary>
/// A delegating stream that wraps HttpResponse.Body to intercept and buffer
/// the response payload before it is flushed to the wire.
///
/// WHY THIS IS NEEDED:
///   In ASP.NET Core, once response.Body is written and flushed, it cannot be read back.
///   We need to capture the full response body BEFORE it reaches the client
///   so we can persist it in the idempotency store for future cache hits.
///
/// ZERO-ALLOCATION APPROACH:
///   Uses RecyclableMemoryStream from Microsoft.IO — a pooled stream that avoids
///   Large Object Heap (LOH) allocations for large response bodies (>85 KB).
///   The pool is shared across all requests to minimize GC pressure.
/// </summary>
internal sealed class BodyCaptureStream : Stream
{
    internal static readonly RecyclableMemoryStreamManager PoolManager
        = new RecyclableMemoryStreamManager();

    private readonly Stream _innerStream;
    private readonly RecyclableMemoryStream _captureBuffer;

    public BodyCaptureStream(Stream innerStream)
    {
        _innerStream = innerStream;
        _captureBuffer = PoolManager.GetStream("FastIdempotency.BodyCapture");
    }

    /// <summary>
    /// Returns the captured response bytes. Call only after the response has fully written.
    /// </summary>
    public byte[] GetCapturedBytes()
    {
        _captureBuffer.Position = 0;
        return _captureBuffer.ToArray();
    }

    // ── Stream overrides ─────────────────────────────────────────────────────

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _captureBuffer.Length;
    public override long Position
    {
        get => _innerStream.Position;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _captureBuffer.Write(buffer, offset, count);
        _innerStream.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _captureBuffer.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        await _innerStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _captureBuffer.WriteAsync(buffer, cancellationToken);
        await _innerStream.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush() => _innerStream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _innerStream.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _captureBuffer.Dispose();
        base.Dispose(disposing);
    }
}
