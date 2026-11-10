namespace Winzilla.Services;

/// <summary>
/// A download that runs at most once, however many times its result is requested. The shell asks for the
/// storage items once per clipboard/drag format, so without this each ask would re-download the files.
/// </summary>
public sealed class PendingDownload(Func<CancellationToken, Task<List<string>>> run)
{
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task<List<string>>? _task;

    public bool Succeeded => _task is { IsCompletedSuccessfully: true };

    public Task<List<string>> GetAsync()
    {
        lock (_lock) return _task ??= run(_cts.Token);
    }

    public void Cancel() => _cts.Cancel();
}
