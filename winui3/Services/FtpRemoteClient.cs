using FluentFTP;

namespace Winzilla.Services;

public sealed class FtpRemoteClient : IRemoteClient
{
    private AsyncFtpClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Action<string>? Log { get; set; }

    private AsyncFtpClient Client => _client ?? throw new InvalidOperationException("Not connected.");

    private sealed class CommandLogger(FtpRemoteClient owner) : IFtpLogger
    {
        public void Log(FtpLogEntry entry)
        {
            var message = entry.Message?.Trim();
            if (string.IsNullOrEmpty(message) || message.StartsWith("Command:", StringComparison.Ordinal) && message.Contains("PASS ")) return;
            owner.Log?.Invoke(message);
        }
    }

    private async Task<T> RunAsync<T>(Func<AsyncFtpClient, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await work(Client); }
        finally { _gate.Release(); }
    }

    private Task RunAsync(Func<AsyncFtpClient, Task> work, CancellationToken ct) =>
        RunAsync<object?>(async c => { await work(c); return null; }, ct);

    public async Task ConnectAsync(ConnectionSettings s, CancellationToken ct)
    {
        var client = new AsyncFtpClient(s.Host, s.User, s.Password, s.Port) { Logger = new CommandLogger(this) };
        client.Config.EncryptionMode = FtpEncryptionMode.None;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        await client.Connect(ct);
        _client = client;
    }

    public Task<string> GetHomeAsync(CancellationToken ct) => RunAsync(c => c.GetWorkingDirectory(ct), ct);

    public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct) => RunAsync<IReadOnlyList<FileEntry>>(async c =>
    {
        var items = await c.GetListing(path, ct);
        var list = items
            .Where(i => i.Type is FtpObjectType.File or FtpObjectType.Directory or FtpObjectType.Link)
            .Where(i => i.Name is not ("." or ".."))
            .Select(i =>
            {
                bool isDir = i.Type == FtpObjectType.Directory;
                return new FileEntry
                {
                    Name = i.Name,
                    FullPath = i.FullName,
                    IsDirectory = isDir,
                    Size = isDir || i.Size < 0 ? 0 : i.Size,
                    Modified = i.Modified == DateTime.MinValue ? null : i.Modified,
                };
            });
        return FileEntry.Sort(list).ToList();
    }, ct);

    public Task DownloadFileAsync(string remotePath, string localPath, TransferProgress progress, CancellationToken ct) => RunAsync(async c =>
    {
        await using var fs = progress.Wrap(File.Create(localPath), ct);
        await c.DownloadStream(fs, remotePath, token: ct);
    }, ct);

    public Task UploadFileAsync(string localPath, string remotePath, TransferProgress progress, CancellationToken ct) => RunAsync(async c =>
    {
        await using var fs = progress.Wrap(File.OpenRead(localPath), ct);
        await c.UploadStream(fs, remotePath, FtpRemoteExists.Overwrite, false, null, ct);
    }, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) =>
        RunAsync(c => c.CreateDirectory(path, ct), ct);

    public Task DeleteAsync(FileEntry entry, CancellationToken ct) => RunAsync(async c =>
    {
        if (entry.IsDirectory) await c.DeleteDirectory(entry.FullPath, ct);
        else await c.DeleteFile(entry.FullPath, ct);
    }, ct);

    public Task RenameAsync(string oldPath, string newPath, CancellationToken ct) =>
        RunAsync(c => c.Rename(oldPath, newPath, ct), ct);

    public async ValueTask DisposeAsync()
    {
        if (_client is { } c)
        {
            try { await c.Disconnect(); } catch { }
            c.Dispose();
        }
        _client = null;
    }
}
