using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using Winzilla.Services;

namespace Winzilla;

public sealed partial class MainPage : Page
{
    private const int MaxLogLines = 1000;

    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "Winzilla");

    private readonly Settings _settings = Settings.Load();
    private IRemoteClient? _client;
    private readonly string _downloadPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    private string _remotePath = "/";
    private IReadOnlyList<FileEntry> _remoteItems = [];
    private SortColumn _sortColumn = SortColumn.Name;
    private bool _sortAscending = true;
    private PendingDownload? _drag;
    private int _busy;
    private CancellationTokenSource _cts = new();
    private readonly TransferProgress _transfer = new();
    private readonly DispatcherTimer _transferTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ObservableCollection<LogLine> _log = [];

    public MainPage()
    {
        InitializeComponent();

        try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, true); } catch { }

        LogList.ItemsSource = _log;
        _transferTimer.Tick += (_, _) => UpdateTransferUi();
        ProtocolBox.SelectedIndex = (int)_settings.Protocol;
        HostBox.Text = _settings.Host;
        PortBox.Text = _settings.Port.ToString();
        UserBox.Text = _settings.User;
        UpdateSortHeaders();
        SelectLastUsedConnection();
        Loaded += async (_, _) => await CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        UpdateInfo? update;
        try { update = await UpdateService.CheckAsync(CancellationToken.None); }
        catch { return; }
        if (update is not null) await OfferUpdateAsync(update);
    }

    private async Task OfferUpdateAsync(UpdateInfo update)
    {
        if (XamlRoot is null) return;

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

    private (Protocol Protocol, string Host, int Port, string User) ReadForm()
    {
        var protocol = ProtocolBox.SelectedIndex == 0 ? Protocol.Sftp : Protocol.Ftp;
        if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535)
            port = protocol == Protocol.Sftp ? 22 : 21;
        return (protocol, HostBox.Text.Trim(), port, UserBox.Text.Trim());
    }

    private void SelectLastUsedConnection()
    {
        var (protocol, host, port, user) = ReadForm();
        var match = _settings.Connections.FirstOrDefault(c =>
            c.Protocol == protocol && c.Host == host && c.Port == port && c.User == user);
        RefreshSavedConnections(match?.Id);
        if (match is not null) LoadConnectionIntoForm(match);
    }

    private void RefreshSavedConnections(string? selectedId)
    {
        SavedBox.SelectionChanged -= SavedBox_Changed;
        SavedBox.Items.Clear();
        SavedBox.Items.Add("(New connection)");
        foreach (var c in _settings.Connections) SavedBox.Items.Add(c.Name);
        int index = _settings.Connections.FindIndex(c => c.Id == selectedId);
        SavedBox.SelectedIndex = index + 1;
        DeleteSavedButton.IsEnabled = index >= 0;
        SavedBox.SelectionChanged += SavedBox_Changed;
    }

    private void LoadConnectionIntoForm(SavedConnection c)
    {
        ProtocolBox.SelectedIndex = (int)c.Protocol;
        HostBox.Text = c.Host;
        PortBox.Text = c.Port.ToString();
        UserBox.Text = c.User;
        var password = CredentialStore.Get(c.Id);
        PasswordBox.Password = password ?? "";
        RememberBox.IsChecked = password is not null;
    }

    private void ClearForm()
    {
        ProtocolBox.SelectedIndex = 0;
        HostBox.Text = "";
        PortBox.Text = "22";
        UserBox.Text = "";
        PasswordBox.Password = "";
        RememberBox.IsChecked = false;
    }

    private void SavedBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        int index = SavedBox.SelectedIndex - 1;
        DeleteSavedButton.IsEnabled = index >= 0;
        if (index >= 0) LoadConnectionIntoForm(_settings.Connections[index]);
        else if (SavedBox.SelectedIndex == 0) ClearForm();
    }

    private async void SaveConnection_Click(object sender, RoutedEventArgs e)
    {
        var (protocol, host, port, user) = ReadForm();
        if (host.Length == 0) { SetStatus("Enter a host."); return; }

        int index = SavedBox.SelectedIndex - 1;
        var existing = index >= 0 ? _settings.Connections[index] : null;
        var name = await PromptAsync("Save connection", existing?.Name ?? (user.Length > 0 ? $"{user}@{host}" : host));
        if (string.IsNullOrWhiteSpace(name)) return;

        var saved = new SavedConnection(existing?.Id ?? Guid.NewGuid().ToString("N"), name.Trim(), protocol, host, port, user);
        try
        {
            if (RememberBox.IsChecked == true) CredentialStore.Set(saved.Id, PasswordBox.Password);
            else CredentialStore.Remove(saved.Id);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Couldn't store the password", ex.Message);
            return;
        }

        if (existing is null) _settings.Connections.Add(saved);
        else _settings.Connections[index] = saved;
        _settings.Save();
        RefreshSavedConnections(saved.Id);
        SetStatus($"Saved \"{saved.Name}\"");
    }

    private async void DeleteConnection_Click(object sender, RoutedEventArgs e)
    {
        int index = SavedBox.SelectedIndex - 1;
        if (index < 0) return;

        var connection = _settings.Connections[index];
        if (!await ConfirmAsync("Delete connection", $"Delete \"{connection.Name}\" and its saved password?")) return;

        CredentialStore.Remove(connection.Id);
        _settings.Connections.RemoveAt(index);
        _settings.Save();
        RefreshSavedConnections(null);
        ClearForm();
        SetStatus($"Deleted \"{connection.Name}\"");
    }

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

        var (protocol, host, port, user) = ReadForm();
        if (host.Length == 0) { SetStatus("Enter a host."); return; }
        var conn = new ConnectionSettings(protocol, host, port, user, PasswordBox.Password);

        _settings.Protocol = protocol;
        _settings.Host = host;
        _settings.Port = port;
        _settings.User = user;
        _settings.Save();

        IRemoteClient client = protocol == Protocol.Sftp ? new SftpRemoteClient(_settings) : new FtpRemoteClient();
        client.Log = LogCommand;
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
        RemoteList.Focus(FocusState.Programmatic);
    }

    private async Task DisconnectAsync()
    {
        var client = _client;
        _client = null;
        if (client is not null) await client.DisposeAsync();
        LogCommand("Status: Disconnected");
        ClearOwnClipboard();
        RemoteList.ItemsSource = null;
        _remoteItems = [];
        RemotePathBox.Text = "";
        RemoteTitle.Text = "Remote (not connected)";
        ConnectButton.Content = "Connect";
        SetStatus("Disconnected");
    }

    // ---------- Busy / status / log ----------

    private void SetStatus(string text) =>
        DispatcherQueue.TryEnqueue(() => StatusText.Text = text);

    private void LogCommand(string line) => DispatcherQueue.TryEnqueue(() =>
    {
        var entry = new LogLine(line);
        _log.Add(entry);
        if (_log.Count > MaxLogLines) _log.RemoveAt(0);
        LogList.ScrollIntoView(entry);
    });

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private async void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        if (_log.Count == 0)
        {
            SetStatus("The command log is empty.");
            return;
        }

        var picker = new FileSavePicker { SuggestedFileName = $"winzilla-log-{DateTime.Now:yyyyMMdd-HHmmss}" };
        picker.FileTypeChoices.Add("Text file", [".txt"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            await File.WriteAllLinesAsync(file.Path, _log.Select(l => l.Text));
            SetStatus($"Exported the command log to {file.Path}");
        }
        catch (Exception ex) { SetStatus($"Couldn't export the log: {ex.Message}"); }
    }

    private Task<T> OnUiThreadAsync<T>(Func<Task<T>> work)
    {
        if (DispatcherQueue.HasThreadAccess) return work();

        var tcs = new TaskCompletionSource<T>();
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { tcs.SetResult(await work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })) tcs.SetException(new InvalidOperationException("The window is closing."));
        return tcs.Task;
    }

    /// <summary>Runs <paramref name="work"/> with the busy indicators shown. Returns false if it failed or was cancelled.</summary>
    private async Task<bool> RunOpAsync(Func<CancellationToken, Task> work, bool showErrors = true)
    {
        if (_busy++ == 0)
        {
            _cts = new CancellationTokenSource();
            _transfer.Reset();
            _transferTimer.Start();
        }
        Progress.Visibility = CancelButton.Visibility = Visibility.Visible;
        try
        {
            await work(_cts.Token);
            return true;
        }
        catch (OperationCanceledException) { SetStatus("Cancelled"); }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
            LogCommand($"Error: {ex.Message}");
            if (showErrors) await ShowMessageAsync("Error", ex.Message);
        }
        finally
        {
            if (--_busy == 0)
            {
                _transferTimer.Stop();
                _transfer.Reset();
                Progress.Visibility = CancelButton.Visibility = TransferPanel.Visibility = Visibility.Collapsed;
            }
        }
        return false;
    }

    private void UpdateTransferUi()
    {
        bool active = _transfer.Source is not null && _busy > 0;
        TransferPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        Progress.Visibility = _busy > 0 && !active ? Visibility.Visible : Visibility.Collapsed;
        if (!active) return;

        long total = _transfer.Total, done = _transfer.Transferred;
        double percent = total > 0 ? Math.Min(100, done * 100.0 / total) : 0;
        TransferPath.Text = $"{_transfer.Source}  →  {_transfer.Target}";
        TransferElapsed.Text = TransferProgress.FormatDuration(_transfer.Elapsed) + " elapsed";
        TransferLeft.Text = (_transfer.Remaining is { } left ? TransferProgress.FormatDuration(left) : "--:--:--") + " left";
        TransferBar.Value = percent;
        TransferBytes.Text = $"{FileEntry.FormatSize(done)} of {FileEntry.FormatSize(total)} " +
            $"({percent:0.0}%, {TransferProgress.FormatRate(_transfer.BytesPerSecond)})";
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

        UpdateInfo? found = null;
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var checkButton = new Button { Content = "Check for updates" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "About Winzilla",
            Content = panel,
            CloseButtonText = "OK",
        };
        checkButton.Click += async (_, _) =>
        {
            checkButton.IsEnabled = false;
            result.Text = "Checking…";
            try
            {
                found = await UpdateService.CheckAsync(CancellationToken.None);
                if (found is null) result.Text = "You're up to date.";
                else dialog.Hide();
            }
            catch (Exception ex) { result.Text = $"Couldn't check for updates: {ex.Message}"; }
            finally { checkButton.IsEnabled = true; }
        };
        panel.Children.Add(checkButton);
        panel.Children.Add(result);

        await dialog.ShowAsync();
        if (found is not null) await OfferUpdateAsync(found);
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

    private List<FileEntry> SelectedEntries() => RemoteList.SelectedItems.OfType<FileEntry>().ToList();

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

    private async Task<bool> UploadPathsAsync(List<string> paths)
    {
        var client = _client;
        if (client is null || paths.Count == 0) return false;
        return await RunOpAsync(async ct =>
        {
            string target = _remotePath;
            foreach (var p in paths)
                await TransferService.UploadAsync(client, p, target, SetStatus, _transfer, ct);
            SetStatus($"Uploaded {paths.Count} item(s)");
            await LoadRemoteAsync(_remotePath, ct);
        });
    }

    private async void Download_Click(object sender, RoutedEventArgs e) =>
        await DownloadEntriesAsync(SelectedEntries());

    private async Task DownloadEntriesAsync(List<FileEntry> entries)
    {
        var client = _client;
        if (client is null || entries.Count == 0) return;
        await RunOpAsync(async ct =>
        {
            Directory.CreateDirectory(_downloadPath);
            foreach (var entry in entries)
                await TransferService.DownloadAsync(client, entry, _downloadPath, SetStatus, _transfer, ct);
            SetStatus($"Downloaded {entries.Count} item(s) to {_downloadPath}");
        });
    }

    /// <summary>Downloads <paramref name="items"/> to a temp folder for the shell to pick up (drag out, copy, cut).</summary>
    private PendingDownload CreateDownload(List<FileEntry> items, IRemoteClient client) => new(async ct =>
    {
        var paths = new List<string>();
        bool ok = await OnUiThreadAsync(() => RunOpAsync(async opCt =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(opCt, ct);
            string temp = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            foreach (var item in items)
                await TransferService.DownloadAsync(client, item, temp, SetStatus, _transfer, linked.Token);
            paths.AddRange(items.Select(i => Path.Combine(temp, i.Name)));
            SetStatus($"Downloaded {items.Count} item(s)");
        }, showErrors: false));
        return ok ? paths : throw new InvalidOperationException("The download failed or was cancelled.");
    });

    // ---------- Drag & drop ----------

    private void RemoteList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var client = _client;
        var items = e.Items.OfType<FileEntry>().ToList();
        if (client is null || items.Count == 0) { e.Cancel = true; return; }

        // Start downloading right away so the drop target isn't left waiting on a slow transfer.
        var download = _drag = CreateDownload(items, client);
        _ = download.GetAsync().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        SetStorageProvider(e.Data, download, DataPackageOperation.Copy);
    }

    private void List_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult == DataPackageOperation.None) _drag?.Cancel();
        _drag = null;
    }

    private void SetStorageProvider(DataPackage data, PendingDownload download, DataPackageOperation operation)
    {
        data.RequestedOperation = operation;
        data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var paths = await download.GetAsync();
                var storage = new List<IStorageItem>();
                foreach (var p in paths)
                {
                    if (Directory.Exists(p)) storage.Add(await StorageFolder.GetFolderFromPathAsync(p));
                    else if (File.Exists(p)) storage.Add(await StorageFile.GetFileFromPathAsync(p));
                }
                request.SetData(storage);
            }
            catch (Exception ex)
            {
                SetStatus($"Couldn't hand over the files: {ex.Message}");
                LogCommand($"Error: {ex.Message}");
            }
            finally { deferral.Complete(); }
        });
    }

    private static async Task<List<string>> GetStoragePathsAsync(DataPackageView view)
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
        try { paths = await GetStoragePathsAsync(e.DataView); }
        finally { deferral.Complete(); }

        await UploadPathsAsync(paths);
    }
}
