namespace Winzilla.Services;

/// <summary>Recursive upload/download built on top of <see cref="IRemoteClient"/>.</summary>
public static class TransferService
{
    public static async Task UploadAsync(IRemoteClient client, string localPath, string remoteDir,
        Action<string> status, CancellationToken ct)
    {
        string name = Path.GetFileName(localPath.TrimEnd('\\', '/'));
        string target = RemotePath.Combine(remoteDir, name);

        if (Directory.Exists(localPath))
        {
            await client.CreateDirectoryAsync(target, ct);
            foreach (var child in Directory.EnumerateFileSystemEntries(localPath))
                await UploadAsync(client, child, target, status, ct);
        }
        else if (File.Exists(localPath))
        {
            status($"Uploading {name}");
            await client.UploadFileAsync(localPath, target, ct);
        }
    }

    public static async Task DownloadAsync(IRemoteClient client, FileEntry entry, string localDir,
        Action<string> status, CancellationToken ct)
    {
        string target = Path.Combine(localDir, entry.Name);

        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(target);
            foreach (var child in await client.ListAsync(entry.FullPath, ct))
                await DownloadAsync(client, child, target, status, ct);
        }
        else
        {
            status($"Downloading {entry.Name}");
            await client.DownloadFileAsync(entry.FullPath, target, ct);
        }
    }

    public static void CopyLocal(string source, string targetDir)
    {
        string target = Path.Combine(targetDir, Path.GetFileName(source.TrimEnd('\\', '/')));
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
                CopyLocal(child, target);
        }
        else if (File.Exists(source))
        {
            File.Copy(source, target, true);
        }
    }
}
