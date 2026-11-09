using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Winzilla
{
    /// <summary>
    /// Progress of the file currently being transferred. Written from the transfer thread and
    /// polled by the UI, so it never marshals per-chunk callbacks onto the UI thread.
    /// </summary>
    public sealed class TransferProgress
    {
        private readonly Stopwatch _clock = new Stopwatch();
        private long _transferred;
        private volatile string? _source;
        private volatile string? _target;
        private long _total;

        public string? Source => _source;
        public string? Target => _target;
        public long Total => Interlocked.Read(ref _total);
        public long Transferred => Interlocked.Read(ref _transferred);
        public TimeSpan Elapsed => _clock.Elapsed;

        public void Begin(string source, string target, long total)
        {
            _source = source;
            _target = target;
            Interlocked.Exchange(ref _total, total);
            Interlocked.Exchange(ref _transferred, 0);
            _clock.Restart();
        }

        public void Reset()
        {
            _source = _target = null;
            Interlocked.Exchange(ref _total, 0);
            Interlocked.Exchange(ref _transferred, 0);
            _clock.Reset();
        }

        internal void Add(int bytes) => Interlocked.Add(ref _transferred, bytes);

        /// <summary>Wraps a local file stream so every byte read or written is counted.</summary>
        public Stream Wrap(Stream inner, CancellationToken ct) => new ProgressStream(inner, this, ct);
    }

    /// <summary>Pass-through stream that reports bytes to <see cref="TransferProgress"/> and honours cancellation.</summary>
    internal sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly TransferProgress _progress;
        private readonly CancellationToken _ct;

        public ProgressStream(Stream inner, TransferProgress progress, CancellationToken ct)
        {
            _inner = inner;
            _progress = progress;
            _ct = ct;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count)
        {
            _ct.ThrowIfCancellationRequested();
            int n = _inner.Read(buffer, offset, count);
            _progress.Add(n);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _ct.ThrowIfCancellationRequested();
            _inner.Write(buffer, offset, count);
            _progress.Add(count);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _ct.ThrowIfCancellationRequested();
            int n = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            _progress.Add(n);
            return n;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _ct.ThrowIfCancellationRequested();
            await _inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            _progress.Add(count);
        }

#if NET
        public override int Read(Span<byte> buffer)
        {
            _ct.ThrowIfCancellationRequested();
            int n = _inner.Read(buffer);
            _progress.Add(n);
            return n;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _ct.ThrowIfCancellationRequested();
            _inner.Write(buffer);
            _progress.Add(buffer.Length);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _ct.ThrowIfCancellationRequested();
            int n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _progress.Add(n);
            return n;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _ct.ThrowIfCancellationRequested();
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _progress.Add(buffer.Length);
        }
#endif

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
