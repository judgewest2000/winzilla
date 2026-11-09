using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using Winzilla.Services;

namespace Winzilla;

public sealed partial class MainPage : Page
{
    private sealed record DragState(List<FileEntry> Items);

    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "Winzilla");

    private readonly Settings _settings = Settings.Load();
    private IRemoteClient? _client;
    private readonly string _downloadPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    private string _remotePath = "/";
    private IReadOnlyList<FileEntry> _remoteItems = [];
    private SortColumn _sortColumn = SortColumn.Name;
    private bool _sortAscending = true;
    private DragState? _drag;
    private int _busy;
    private CancellationTokenSource _cts = new();

    public MainPage()
    {
        InitializeComponent();

        try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, true); } catch { }

        ProtocolBox.SelectedIndex = (int)_settings.Protocol;
        HostBox.Text = _settings.Host;
        PortBox.Text = _settings.Port.ToString();
        UserBox.Text = _settings.User;
        UpdateSortHeaders();
        Loaded += async (_, _) => await CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        UpdateInfo? update;
        try { update = await UpdateService.CheckAsync(CancellationToken.None); }
        catch { return; }
        if (update is null || XamlRoot is null) return;

        var prompt = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Update available",
            Content = new TextBlock
            {
                Text = $"Winzilla {update.Version} is available (you have {UpdateService.CurrentVersion}). Update now? Winzilla will restart.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Update now",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await prompt.ShowAsync() != ContentDialogResult.Primary) return;

        SetStatus($"Downloading Winzilla {update.Version}...");
        Progress.Visibility = Visibility.Visible;
        try
        {
            var installer = await UpdateService.DownloadAsync(update, CancellationToken.None);
            UpdateService.StartInstaller(installer);
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Progress.Visibility = Visibility.Collapsed;
            SetStatus("Update failed");
            await ShowMessageAsync("Update failed", ex.Message);
        }
    }

    // ---------- Connection form ----------

    private void Protocol_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PortBox is null) return;
        bool sftp = ProtocolBox.SelectedIndex == 0;
        if (PortBox.Text is "22" or "21" or "")
            PortBox.Text = sftp ? "22" : "21";
    }

    private void Connect_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _client is null) Connect_Click(sender, e);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not null)
        {
            await DisconnectAsync();
            return;
        }

        string host = HostBox.Text.Trim();
        if (host.Length == 0) { SetStatus("Enter a host."); return; }
        var protocol = ProtocolBox.SelectedIndex == 0 ? Protocol.Sftp : Protocol.Ftp;
        if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535)
            port = protocol == Protocol.Sftp ? 22 : 21;
        string user = UserBox.Text.Trim();
        var conn = new ConnectionSettings(protocol, host, port, user, PasswordBox.Password);

        _settings.Protocol = protocol;
        _settings.Host = host;
        _settings.Port = port;
        _settings.User = user;
        _settings.Save();

        IRemoteClient client = protocol == Protocol.Sftp ? new SftpRemoteClient(_settings) : new FtpRemoteClient();
        ConnectButton.IsEnabled = false;
        bool connected = false;
        try
        {
            await RunOpAsync(async ct =>
            {
                SetStatus($"Connecting to {host}:{port}…");
                await client.ConnectAsync(conn, ct);
                _client = client;
                _remotePath = await client.GetHomeAsync(ct);
                await LoadRemoteAsync(_remotePath, ct);
                connected = true;
            });
        }
        finally { ConnectButton.IsEnabled = true; }

        if (!connected)
        {
            _client = null;
            await client.DisposeAsync();
            return;
        }
        ConnectButton.Content = "Disconnect";
        RemoteTitle.Text = $"Remote ({user}@{host})";
        SetStatus($"Connected to {host}");
    }

    private async Task DisconnectAsync()
    {
        var client = _client;
        _client = null;
        if (client is not null) await client.DisposeAsync();
        RemoteList.ItemsSource = null;
        _remoteItems = [];
        RemotePathBox.Text = "";
        RemoteTitle.Text = "Remote (not connected)";
        ConnectButton.Content = "Connect";
        SetStatus("Disconnected");
    }

    // ---------- Busy / status ----------

    private void SetStatus(string text) =>
        DispatcherQueue.TryEnqueue(() => StatusText.Text = text);

    private async Task RunOpAsync(Func<CancellationToken, Task> work)
    {
        if (_busy++ == 0) _cts = new CancellationTokenSource();
        Progress.Visibility = CancelButton.Visibility = Visibility.Visible;
        try { await work(_cts.Token); }
        catch (OperationCanceledException) { SetStatus("Cancelled"); }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
            await ShowMessageAsync("Error", ex.Message);
        }
        finally
        {
            if (--_busy == 0) Progress.Visibility = CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts.Cancel();

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null) return;
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Version {version}\nA lightweight FTP and SFTP file-transfer client.\n\nCopyright (c) 2026 James West\nReleased under the MIT License.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new HyperlinkButton
        {
            Content = "github.com/judgewest2000/winzilla",
            NavigateUri = new Uri("https://github.com/judgewest2000/winzilla"),
            Padding = new Thickness(0),
        });
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "About Winzilla",
            Content = panel,
            CloseButtonText = "OK",
        }.ShowAsync();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (XamlRoot is null) return;
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        }.ShowAsync();
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var d = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Yes",
            CloseButtonText = "No",
            DefaultButton = ContentDialogButton.Close,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<string?> PromptAsync(string title, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 280 };
        var d = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        d.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var name = box.Text;
        if (await d.ShowAsync() != ContentDialogResult.Primary) return null;
        name = box.Text.Trim();
        return name.Length == 0 || name.IndexOfAny(['/', '\\']) >= 0 ? null : name;
    }

    // ---------- Remote pane ----------

    private async Task LoadRemoteAsync(string path, CancellationToken ct)
    {
        if (_client is null) return;
        _remoteItems = await _client.ListAsync(path, ct);
        _remotePath = path;
        RemotePathBox.Text = path;
        ApplySort();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var column = Enum.Parse<SortColumn>((string)((Button)sender).Tag);
        _sortAscending = column != _sortColumn || !_sortAscending;
        _sortColumn = column;
        ApplySort();
    }

    private void ApplySort()
    {
        RemoteList.ItemsSource = FileEntry.Sort(_remoteItems, _sortColumn, _sortAscending);
        UpdateSortHeaders();
    }

    private void UpdateSortHeaders()
    {
        string arrow = _sortAscending ? " ▲" : " ▼";
        SortNameButton.Content = "Name" + (_sortColumn == SortColumn.Name ? arrow : "");
        SortSizeButton.Content = "Size" + (_sortColumn == SortColumn.Size ? arrow : "");
        SortModifiedButton.Content = "Date modified" + (_sortColumn == SortColumn.Modified ? arrow : "");
    }

    private Task ReloadRemoteAsync() => _client is null
        ? Task.CompletedTask
        : RunOpAsync(ct => LoadRemoteAsync(_remotePath, ct));

    private async void RemoteUp_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) return;
        await RunOpAsync(ct => LoadRemoteAsync(RemotePath.Parent(_remotePath), ct));
    }

    private async void RemoteRefresh_Click(object sender, RoutedEventArgs e) => await ReloadRemoteAsync();

    private async void RemotePath_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _client is null) return;
        var path = RemotePathBox.Text.Trim();
        await RunOpAsync(async ct =>
        {
            try { await LoadRemoteAsync(path, ct); }
            catch { RemotePathBox.Text = _remotePath; throw; }
        });
    }

    private async void RemoteList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RemoteList.SelectedItem is not FileEntry entry) return;
        if (entry.IsDirectory) await RunOpAsync(ct => LoadRemoteAsync(entry.FullPath, ct));
        else Download_Click(sender, e);
    }

    private async void RemoteList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete) RemoteDelete_Click(sender, e);
        else if (e.Key == VirtualKey.Enter && RemoteList.SelectedItem is FileEntry { IsDirectory: true } d)
            await RunOpAsync(ct => LoadRemoteAsync(d.FullPath, ct));
    }

    private async void RemoteNewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) return;
        var name = await PromptAsync("New remote folder");
        if (name is null) return;
        var client = _client;
        await RunOpAsync(async ct =>
        {
            await client.CreateDirectoryAsync(RemotePath.Combine(_remotePath, name), ct);
            await LoadRemoteAsync(_remotePath, ct);
        });
    }

    private async void RemoteRename_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || RemoteList.SelectedItem is not FileEntry entry) return;
        var name = await PromptAsync("Rename", entry.Name);
        if (name is null || name == entry.Name) return;
        var client = _client;
        await RunOpAsync(async ct =>
        {
            await client.RenameAsync(entry.FullPath, RemotePath.Combine(_remotePath, name), ct);
            await LoadRemoteAsync(_remotePath, ct);
        });
    }

    private async void RemoteDelete_Click(object sender, RoutedEventArgs e)
    {
        var entries = RemoteList.SelectedItems.OfType<FileEntry>().ToList();
        if (_client is null || entries.Count == 0) return;
        if (!await ConfirmAsync("Delete", $"Delete {entries.Count} selected remote item(s)? This cannot be undone.")) return;
        var client = _client;
        await RunOpAsync(async ct =>
        {
            foreach (var entry in entries) await client.DeleteAsync(entry, ct);
            await LoadRemoteAsync(_remotePath, ct);
        });
    }

    // ---------- Transfers ----------

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) return;
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        await UploadPathsAsync(files.Select(f => f.Path).ToList());
    }

    private async Task UploadPathsAsync(List<string> paths)
    {
        var client = _client;
        if (client is null || paths.Count == 0) return;
        await RunOpAsync(async ct =>
        {
            string target = _remotePath;
            foreach (var p in paths)
                await TransferService.UploadAsync(client, p, target, SetStatus, ct);
            SetStatus($"Uploaded {paths.Count} item(s)");
            await LoadRemoteAsync(_remotePath, ct);
        });
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var entries = RemoteList.SelectedItems.OfType<FileEntry>().ToList();
        await DownloadEntriesAsync(entries);
    }

    private async Task DownloadEntriesAsync(List<FileEntry> entries)
    {
        var client = _client;
        if (client is null || entries.Count == 0) return;
        await RunOpAsync(async ct =>
        {
            Directory.CreateDirectory(_downloadPath);
            foreach (var entry in entries)
                await TransferService.DownloadAsync(client, entry, _downloadPath, SetStatus, ct);
            SetStatus($"Downloaded {entries.Count} item(s) to {_downloadPath}");
        });
    }

    // ---------- Drag & drop ----------

    private void RemoteList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var client = _client;
        var items = e.Items.OfType<FileEntry>().ToList();
        _drag = new DragState(items);
        if (client is null) { e.Cancel = true; return; }

        // Files are only downloaded if the drop target (e.g. Explorer) actually asks for them.
        SetStorageProvider(e.Data, async ct =>
        {
            string temp = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            if (_busy++ == 0) _cts = new CancellationTokenSource();
            DispatcherQueue.TryEnqueue(() => Progress.Visibility = CancelButton.Visibility = Visibility.Visible);
            try
            {
                foreach (var item in items)
                    await TransferService.DownloadAsync(client, item, temp, SetStatus, _cts.Token);
                SetStatus($"Downloaded {items.Count} item(s)");
                return items.Select(i => Path.Combine(temp, i.Name)).ToList();
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (--_busy == 0) Progress.Visibility = CancelButton.Visibility = Visibility.Collapsed;
                });
            }
        });
    }

    private void List_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => _drag = null;

    private void SetStorageProvider(DataPackage data, Func<CancellationToken, Task<List<string>>> getPaths)
    {
        data.RequestedOperation = DataPackageOperation.Copy;
        data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var paths = await getPaths(CancellationToken.None);
                var storage = new List<IStorageItem>();
                foreach (var p in paths)
                {
                    if (Directory.Exists(p)) storage.Add(await StorageFolder.GetFolderFromPathAsync(p));
                    else if (File.Exists(p)) storage.Add(await StorageFile.GetFileFromPathAsync(p));
                }
                request.SetData(storage);
            }
            catch (Exception ex) { SetStatus($"Drag failed: {ex.Message}"); }
            finally { deferral.Complete(); }
        });
    }

    private static async Task<List<string>> GetDroppedPathsAsync(DataPackageView view)
    {
        var items = await view.GetStorageItemsAsync();
        return items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
    }

    private void RemotePane_DragOver(object sender, DragEventArgs e)
    {
        bool accept = _client is not null && _drag is null && e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = accept ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (accept) e.DragUIOverride.Caption = "Upload";
    }

    private async void RemotePane_Drop(object sender, DragEventArgs e)
    {
        if (_drag is not null) return;

        List<string> paths;
        var deferral = e.GetDeferral();
        try { paths = await GetDroppedPathsAsync(e.DataView); }
        finally { deferral.Complete(); }

        await UploadPathsAsync(paths);
    }
}
