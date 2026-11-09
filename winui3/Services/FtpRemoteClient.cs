using FluentFTP;

namespace Winzilla.Services;

public sealed class FtpRemoteClient : IRemoteClient
{
    private AsyncFtpClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AsyncFtpClient Client => _client ?? throw new InvalidOperationException("Not connected.");

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
        var client = new AsyncFtpClient(s.Host, s.User, s.Password, s.Port);
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

    public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct) => RunAsync(async c =>
    {
        await using var fs = File.Create(localPath);
        await c.DownloadStream(fs, remotePath, token: ct);
    }, ct);

    public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct) => RunAsync(async c =>
    {
        await using var fs = File.OpenRead(localPath);
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
