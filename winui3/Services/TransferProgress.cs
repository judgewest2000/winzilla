using System.Diagnostics;

namespace Winzilla.Services;

/// <summary>
/// Progress of the file currently being transferred. Written from the transfer thread and
/// polled by the UI, so it never marshals per-chunk callbacks onto the UI thread.
/// </summary>
public sealed class TransferProgress
{
    private readonly Stopwatch _clock = new();
    private long _transferred;
    private long _total;
    private volatile string? _source;
    private volatile string? _target;

    public string? Source => _source;
    public string? Target => _target;
    public long Total => Interlocked.Read(ref _total);
    public long Transferred => Interlocked.Read(ref _transferred);
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Average bytes per second since the current file started, or 0 until there is enough data.</summary>
    public double BytesPerSecond => Elapsed.TotalSeconds >= 0.5 ? Transferred / Elapsed.TotalSeconds : 0;

    /// <summary>Estimated time left for the current file, or null when it can't be estimated yet.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            double rate = BytesPerSecond;
            long total = Total;
            return rate > 0 && total > 0 ? TimeSpan.FromSeconds(Math.Max(0, total - Transferred) / rate) : null;
        }
    }

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

    /// <summary>Wraps a local file stream so every byte read or written is counted and cancellation is honoured.</summary>
    public Stream Wrap(Stream inner, CancellationToken ct) => new ProgressStream(inner, this, ct);

    public static string FormatDuration(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    public static string FormatRate(double bytesPerSecond)
    {
        string[] units = ["B/s", "KiB/s", "MiB/s", "GiB/s"];
        int unit = 0;
        while (bytesPerSecond >= 1024 && unit < units.Length - 1)
        {
            bytesPerSecond /= 1024;
            unit++;
        }
        return bytesPerSecond.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
    }
}

/// <summary>Pass-through stream that reports bytes to <see cref="TransferProgress"/> and honours cancellation.</summary>
internal sealed class ProgressStream(Stream inner, TransferProgress progress, CancellationToken ct) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count)
    {
        ct.ThrowIfCancellationRequested();
        int n = inner.Read(buffer, offset, count);
        progress.Add(n);
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        ct.ThrowIfCancellationRequested();
        int n = inner.Read(buffer);
        progress.Add(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ct.ThrowIfCancellationRequested();
        int n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        progress.Add(n);
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ct.ThrowIfCancellationRequested();
        inner.Write(buffer, offset, count);
        progress.Add(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ct.ThrowIfCancellationRequested();
        inner.Write(buffer);
        progress.Add(buffer.Length);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ct.ThrowIfCancellationRequested();
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        progress.Add(buffer.Length);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
