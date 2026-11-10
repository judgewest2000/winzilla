namespace Winzilla.Services;

/// <summary>A read-only stream that holds a client's operation gate until it is disposed.</summary>
internal sealed class GatedStream(Stream inner, SemaphoreSlim gate) : Stream
{
    private int _released;

    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { inner.Dispose(); }
            finally { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); }
        }
        base.Dispose(disposing);
    }
}
