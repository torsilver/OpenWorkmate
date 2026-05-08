namespace OpenWorkmate.Server.Services.DashScope;

/// <summary>在释放底层流时执行一次回调（用于结束百炼推理 AsyncLocal 帧）。</summary>
internal sealed class PopFrameOnDisposeStream : Stream
{
    private readonly Stream _inner;
    private readonly Action _onDispose;
    private bool _disposed;

    public PopFrameOnDisposeStream(Stream inner, Action onDispose)
    {
        _inner = inner;
        _onDispose = onDispose;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (disposing)
                _inner.Dispose();
        }
        finally
        {
            _onDispose();
        }

        base.Dispose(disposing);
    }
}
