using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Winzilla
{
    public enum Protocol { Sftp, Ftp }

    public enum SortColumn { Name, Size, Modified }

    public sealed class ConnectionSettings
    {
        public ConnectionSettings(Protocol protocol, string host, int port, string user, string password)
        {
            Protocol = protocol;
            Host = host;
            Port = port;
            User = user;
            Password = password;
        }

        public Protocol Protocol { get; }
        public string Host { get; }
        public int Port { get; }
        public string User { get; }
        public string Password { get; }
    }

    public sealed class FileEntry
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime? Modified { get; set; }

        public string SizeText => IsDirectory ? "" : FormatSize(Size);
        public string ModifiedText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";

        public static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return i == 0 ? bytes + " B" : v.ToString("0.#") + " " + units[i];
        }

        public static List<FileEntry> Sort(IEnumerable<FileEntry> items, SortColumn column = SortColumn.Name, bool ascending = true)
        {
            var all = items.ToList();
            var result = new List<FileEntry>(all.Count);
            result.AddRange(SortGroup(all.Where(i => i.IsDirectory), column, ascending));
            result.AddRange(SortGroup(all.Where(i => !i.IsDirectory), column, ascending));
            return result;
        }

        private static IEnumerable<FileEntry> SortGroup(IEnumerable<FileEntry> group, SortColumn column, bool ascending)
        {
            IOrderedEnumerable<FileEntry> ordered;
            switch (column)
            {
                case SortColumn.Size:
                    ordered = ascending ? group.OrderBy(i => i.Size) : group.OrderByDescending(i => i.Size);
                    break;
                case SortColumn.Modified:
                    ordered = ascending
                        ? group.OrderBy(i => i.Modified ?? DateTime.MinValue)
                        : group.OrderByDescending(i => i.Modified ?? DateTime.MinValue);
                    break;
                default:
                    ordered = ascending
                        ? group.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                        : group.OrderByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase);
                    break;
            }
            return ordered.ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);
        }
    }

    public static class RemotePath
    {
        public static string Combine(string dir, string name) =>
            dir.EndsWith("/", StringComparison.Ordinal) ? dir + name : dir + "/" + name;

        public static string Parent(string path)
        {
            path = path.TrimEnd('/');
            int i = path.LastIndexOf('/');
            return i <= 0 ? "/" : path.Substring(0, i);
        }
    }

    public interface IRemoteClient : IDisposable
    {
        Task ConnectAsync(ConnectionSettings settings, CancellationToken ct);
        Task<string> GetHomeAsync(CancellationToken ct);
        Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct);
        Task DownloadFileAsync(string remotePath, string localPath, TransferProgress progress, CancellationToken ct);
        Task UploadFileAsync(string localPath, string remotePath, TransferProgress progress, CancellationToken ct);
        Task CreateDirectoryAsync(string path, CancellationToken ct);
        Task DeleteAsync(FileEntry entry, CancellationToken ct);
        Task RenameAsync(string oldPath, string newPath, CancellationToken ct);
    }
}
