using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace Winzilla
{
    public sealed class SftpRemoteClient : IRemoteClient
    {
        private readonly Settings _settings;
        private SftpClient? _client;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public SftpRemoteClient(Settings settings) => _settings = settings;

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

            var info = new ConnectionInfo(s.Host, s.Port, s.User, password, interactive);
            var client = new SftpClient(info);
            string? hostKeyError = null;
            client.HostKeyReceived += (_, e) =>
            {
                e.CanTrust = _settings.VerifyHostKey(s.Host, s.Port, e.HostKeyName, e.FingerPrintSHA256, out hostKeyError);
            };

            try { client.Connect(); }
            catch (Exception ex) when (hostKeyError != null)
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
                if (f.Name == "." || f.Name == "..") continue;
                bool isDir = f.IsDirectory;
                if (f.IsSymbolicLink)
                {
                    try { isDir = c.GetAttributes(f.FullName).IsDirectory; } catch (Exception) { }
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
            return FileEntry.Sort(list);
        }, ct);

        public Task DownloadFileAsync(string remotePath, string localPath, TransferProgress progress, CancellationToken ct) => RunAsync(c =>
        {
            using (var fs = progress.Wrap(File.Create(localPath), ct))
                c.DownloadFile(remotePath, fs);
        }, ct);

        public Task UploadFileAsync(string localPath, string remotePath, TransferProgress progress, CancellationToken ct) => RunAsync(c =>
        {
            using (var fs = progress.Wrap(File.OpenRead(localPath), ct))
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
                if (f.Name == "." || f.Name == "..") continue;
                if (f.IsDirectory) DeleteDir(c, f.FullName);
                else c.DeleteFile(f.FullName);
            }
            c.DeleteDirectory(path);
        }

        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct) =>
            RunAsync(c => c.RenameFile(oldPath, newPath), ct);

        public void Dispose()
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
