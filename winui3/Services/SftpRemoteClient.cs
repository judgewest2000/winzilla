using Renci.SshNet;

namespace Winzilla.Services;

public sealed class SftpRemoteClient(Settings settings) : IRemoteClient
{
    private SftpClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Action<string>? Log { get; set; }

    private SftpClient Client => _client ?? throw new InvalidOperationException("Not connected.");

    private void Say(string kind, string text) => Log?.Invoke($"{kind}: {text}");

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
        Say("Status", $"Connecting to {s.Host}:{s.Port} (SFTP) as {s.User}");
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
        Say("Status", "Connected");
    }, ct);

    public Task<string> GetHomeAsync(CancellationToken ct) => RunAsync(c =>
    {
        Say("Command", "pwd");
        return c.WorkingDirectory;
    }, ct);

    public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct) => RunAsync<IReadOnlyList<FileEntry>>(c =>
    {
        Say("Command", $"ls {path}");
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
        Say("Response", $"{list.Count} entries");
        return FileEntry.Sort(list).ToList();
    }, ct);

    public Task DownloadFileAsync(string remotePath, string localPath, TransferProgress progress, CancellationToken ct) => RunAsync(c =>
    {
        Say("Command", $"get {remotePath}");
        using var fs = progress.Wrap(File.Create(localPath), ct);
        c.DownloadFile(remotePath, fs);
        Say("Response", "Transfer complete");
    }, ct);

    public async Task<Stream> OpenReadAsync(string remotePath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Say("Command", $"get {remotePath}");
            var stream = await Task.Run(() => Client.OpenRead(remotePath), ct);
            return new GatedStream(stream, _gate);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public Task UploadFileAsync(string localPath, string remotePath, TransferProgress progress, CancellationToken ct) => RunAsync(c =>
    {
        Say("Command", $"put {remotePath}");
        using var fs = progress.Wrap(File.OpenRead(localPath), ct);
        c.UploadFile(fs, remotePath, true);
        Say("Response", "Transfer complete");
    }, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => RunAsync(c =>
    {
        Say("Command", $"mkdir {path}");
        if (!c.Exists(path)) c.CreateDirectory(path);
    }, ct);

    public Task DeleteAsync(FileEntry entry, CancellationToken ct) => RunAsync(c =>
    {
        if (entry.IsDirectory) DeleteDir(c, entry.FullPath);
        else
        {
            Say("Command", $"rm {entry.FullPath}");
            c.DeleteFile(entry.FullPath);
        }
    }, ct);

    private void DeleteDir(SftpClient c, string path)
    {
        foreach (var f in c.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            if (f.IsDirectory) DeleteDir(c, f.FullName);
            else
            {
                Say("Command", $"rm {f.FullName}");
                c.DeleteFile(f.FullName);
            }
        }
        Say("Command", $"rmdir {path}");
        c.DeleteDirectory(path);
    }

    public Task RenameAsync(string oldPath, string newPath, CancellationToken ct) => RunAsync(c =>
    {
        Say("Command", $"rename {oldPath} {newPath}");
        c.RenameFile(oldPath, newPath);
    }, ct);

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }
}
