namespace Winzilla.Services;

public enum Protocol { Sftp, Ftp }

public sealed record ConnectionSettings(Protocol Protocol, string Host, int Port, string User, string Password);

/// <summary>A named connection. The password lives in <see cref="CredentialStore"/>, never in settings.json.</summary>
public sealed record SavedConnection(string Id, string Name, Protocol Protocol, string Host, int Port, string User);

public sealed class FileEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }

    public Microsoft.UI.Xaml.Media.ImageSource? Icon => ShellIcons.Get(Name, IsDirectory);
    public string SizeText => IsDirectory ? "" : FormatSize(Size);
    public string ModifiedText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }

    public static List<FileEntry> Sort(IEnumerable<FileEntry> items, SortColumn column = SortColumn.Name, bool ascending = true)
    {
        var folders = items.Where(i => i.IsDirectory);
        var files = items.Where(i => !i.IsDirectory);
        return [.. SortGroup(folders, column, ascending), .. SortGroup(files, column, ascending)];
    }

    private static IEnumerable<FileEntry> SortGroup(IEnumerable<FileEntry> group, SortColumn column, bool ascending)
    {
        IOrderedEnumerable<FileEntry> ordered = column switch
        {
            SortColumn.Size => Order(group, i => i.Size, ascending),
            SortColumn.Modified => Order(group, i => i.Modified ?? DateTime.MinValue, ascending),
            _ => ascending
                ? group.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                : group.OrderByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        return ordered.ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static IOrderedEnumerable<FileEntry> Order<TKey>(IEnumerable<FileEntry> items, Func<FileEntry, TKey> key, bool ascending) =>
        ascending ? items.OrderBy(key) : items.OrderByDescending(key);
}

public enum SortColumn { Name, Size, Modified }

public static class RemotePath
{
    public static string Combine(string dir, string name) =>
        dir.EndsWith('/') ? dir + name : dir + "/" + name;

    public static string Parent(string path)
    {
        path = path.TrimEnd('/');
        int i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }
}

public interface IRemoteClient : IAsyncDisposable
{
    Task ConnectAsync(ConnectionSettings settings, CancellationToken ct);
    Task<string> GetHomeAsync(CancellationToken ct);
    Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct);
    Task DownloadFileAsync(string remotePath, string localPath, CancellationToken ct);
    Task UploadFileAsync(string localPath, string remotePath, CancellationToken ct);
    Task CreateDirectoryAsync(string path, CancellationToken ct);
    Task DeleteAsync(FileEntry entry, CancellationToken ct);
    Task RenameAsync(string oldPath, string newPath, CancellationToken ct);
}
