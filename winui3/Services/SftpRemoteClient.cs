using Renci.SshNet;

namespace Winzilla.Services;

public sealed class SftpRemoteClient(Settings settings) : IRemoteClient
{
    private SftpClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SftpClient Client => _client ?? throw new InvalidOperationException("Not connected.");

    private async Task<T> RunAsync<T>(Func<SftpClient, T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await Task.Run(() => work(Client), ct); }
        finally { _gate.Release(); }
    }

    private Task RunAsync(Action<SftpClient> work, CancellationToken ct) =>
        RunAsync<object?>(c => { work(c); return null; }, ct);

    public Task ConnectAsync(ConnectionSettings s, CancellationToken ct) => Task.Run(() =>
    {
        var password = new PasswordAuthenticationMethod(s.User, s.Password);
        var interactive = new KeyboardInteractiveAuthenticationMethod(s.User);
        interactive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var p in e.Prompts) p.Response = s.Password;
        };

        var info = new Renci.SshNet.ConnectionInfo(s.Host, s.Port, s.User, password, interactive);
        var client = new SftpClient(info);
        string? hostKeyError = null;
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = settings.VerifyHostKey(s.Host, s.Port, e.HostKeyName, e.FingerPrintSHA256, out hostKeyError);
        };

        try { client.Connect(); }
        catch (Exception ex) when (hostKeyError is not null)
        {
            client.Dispose();
            throw new InvalidOperationException(hostKeyError, ex);
        }
        _client = client;
    }, ct);

    public Task<string> GetHomeAsync(CancellationToken ct) => RunAsync(c => c.WorkingDirectory, ct);

    public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct) => RunAsync<IReadOnlyList<FileEntry>>(c =>
    {
        var list = new List<FileEntry>();
        foreach (var f in c.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            bool isDir = f.IsDirectory;
            if (f.IsSymbolicLink)
            {
                try { isDir = c.GetAttributes(f.FullName).IsDirectory; } catch { }
            }
            list.Add(new FileEntry
            {
                Name = f.Name,
                FullPath = f.FullName,
                IsDirectory = isDir,
                Size = isDir ? 0 : f.Length,
                Modified = f.LastWriteTime,
            });
        }
        return FileEntry.Sort(list).ToList();
    }, ct);

    public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct) => RunAsync(c =>
    {
        using var fs = File.Create(localPath);
        c.DownloadFile(remotePath, fs);
    }, ct);

    public Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct) => RunAsync(c =>
    {
        using var fs = File.OpenRead(localPath);
        c.UploadFile(fs, remotePath, true);
    }, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => RunAsync(c =>
    {
        if (!c.Exists(path)) c.CreateDirectory(path);
    }, ct);

    public Task DeleteAsync(FileEntry entry, CancellationToken ct) => RunAsync(c =>
    {
        if (entry.IsDirectory) DeleteDir(c, entry.FullPath);
        else c.DeleteFile(entry.FullPath);
    }, ct);

    private static void DeleteDir(SftpClient c, string path)
    {
        foreach (var f in c.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            if (f.IsDirectory) DeleteDir(c, f.FullName);
            else c.DeleteFile(f.FullName);
        }
        c.DeleteDirectory(path);
    }

    public Task RenameAsync(string oldPath, string newPath, CancellationToken ct) =>
        RunAsync(c => c.RenameFile(oldPath, newPath), ct);

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }
}
