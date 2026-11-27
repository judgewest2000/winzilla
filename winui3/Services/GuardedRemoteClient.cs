namespace Winzilla.Services;

/// <summary>
/// Wraps the live client so a connection in a read-only group can't change the server,
/// even if the UI lets a write through (a paste, a drop, a keyboard shortcut).
/// </summary>
public sealed class GuardedRemoteClient(IRemoteClient inner) : IRemoteClient
{
    /// <summary>The read-only group's name, or null when writes are allowed. Can change while connected (the group was edited).</summary>
    public string? ReadOnlyGroup { get; set; }

    public bool IsReadOnly => ReadOnlyGroup is not null;

    public Action<string>? Log
    {
        get => inner.Log;
        set => inner.Log = value;
    }

    public Task ConnectAsync(ConnectionSettings settings, CancellationToken ct) => inner.ConnectAsync(settings, ct);
    public Task<string> GetHomeAsync(CancellationToken ct) => inner.GetHomeAsync(ct);
    public Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);
    public Task<Stream> OpenReadAsync(string remotePath, CancellationToken ct) => inner.OpenReadAsync(remotePath, ct);

    public Task DownloadFileAsync(string remotePath, string localPath, TransferProgress progress, CancellationToken ct) =>
        inner.DownloadFileAsync(remotePath, localPath, progress, ct);

    public Task UploadFileAsync(string localPath, string remotePath, TransferProgress progress, CancellationToken ct)
    {
        EnsureWritable();
        return inner.UploadFileAsync(localPath, remotePath, progress, ct);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        EnsureWritable();
        return inner.CreateDirectoryAsync(path, ct);
    }

    public Task DeleteAsync(FileEntry entry, CancellationToken ct)
    {
        EnsureWritable();
        return inner.DeleteAsync(entry, ct);
    }

    public Task RenameAsync(string oldPath, string newPath, CancellationToken ct)
    {
        EnsureWritable();
        return inner.RenameAsync(oldPath, newPath, ct);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private void EnsureWritable()
    {
        if (ReadOnlyGroup is { } group)
            throw new InvalidOperationException($"\u201C{group}\u201D is a read-only group: uploading, deleting, renaming and creating folders are disabled.");
    }
}
