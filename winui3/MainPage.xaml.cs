using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using Winzilla.Services;
using Winzilla.Views;

namespace Winzilla;

public sealed partial class MainPage : Page
{
    private const int MaxLogLines = 1000;

    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "Winzilla");

    private readonly Settings _settings = Settings.Load();
    private GuardedRemoteClient? _client;
    private readonly ConnectionStore _store = new();
    private readonly List<ConnectionGroup> _groups;
    private readonly HashSet<string> _collapsedGroups = [];
    private string? _activeConnectionId;
    private string? _activeLabel;
    private ConnectionGroup? _activeGroup;
    private readonly Brush _remoteListBorder;
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
        _remoteListBorder = RemoteList.BorderBrush;
        _groups = _store.Load();
        Commit();
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

    // ---------- Quick connect ----------

    /// <summary>The quick-connect row as an unsaved connection.</summary>
    private SavedConnection ReadForm()
    {
        var protocol = ProtocolBox.SelectedIndex == 1 ? Protocol.Ftp : Protocol.Sftp;
        if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535)
            port = SavedConnection.DefaultPort(protocol);
        return new SavedConnection
        {
            Protocol = protocol,
            Host = HostBox.Text.Trim(),
            Port = port,
            User = UserBox.Text.Trim(),
            Password = PasswordBox.Password,
        };
    }

    private void FillForm(SavedConnection c, string password)
    {
        ProtocolBox.SelectedIndex = (int)c.Protocol;
        HostBox.Text = c.Host;
        PortBox.Text = c.Port.ToString();
        UserBox.Text = c.User;
        PasswordBox.Password = password;
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
        await ConnectAsync(null);
    }

    /// <summary>Saves the quick-connect details: edits the matching saved connection, or adds a new one to a group.</summary>
    private async void SaveConnection_Click(object sender, RoutedEventArgs e)
    {
        var form = ReadForm();
        if (FindSaved(form) is { } existing && FindGroup(existing.Id) is { } group)
        {
            await EditConnectionAsync(group, existing, form.Password.Length > 0 ? form.Password : null);
            return;
        }
        if (await TargetGroupAsync() is { } target) await AddConnectionAsync(target, form);
    }

    /// <summary>Connects with the quick-connect details. <paramref name="saved"/> (or a saved connection to the same server) sets the group colour and read-only mode.</summary>
    private async Task ConnectAsync(SavedConnection? saved)
    {
        var form = ReadForm();
        if (form.Host.Length == 0) { SetStatus("Enter a host."); return; }
        saved ??= FindSaved(form);
        var conn = new ConnectionSettings(form.Protocol, form.Host, form.Port, form.User, form.Password);

        _settings.Protocol = form.Protocol;
        _settings.Host = form.Host;
        _settings.Port = form.Port;
        _settings.User = form.User;
        _settings.Save();

        var client = new GuardedRemoteClient(form.Protocol == Protocol.Sftp ? new SftpRemoteClient(_settings) : new FtpRemoteClient());
        client.Log = LogCommand;
        ConnectButton.IsEnabled = false;
        bool connected = false;
        try
        {
            await RunOpAsync(async ct =>
            {
                SetStatus($"Connecting to {form.Host}:{form.Port}…");
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
        _activeConnectionId = saved?.Id;
        _activeLabel = saved?.DisplayName ?? form.Address;
        _activeGroup = saved is null ? null : FindGroup(saved.Id);
        ConnectButton.Content = "Disconnect";
        RemoteTitle.Text = $"Remote ({form.Address})";
        ApplyGroupChrome();
        SetStatus($"Connected to {form.Host}");
        RemoteList.Focus(FocusState.Programmatic);
    }

    // ---------- Saved connections & groups ----------

    private SavedConnection? FindSaved(SavedConnection form) =>
        _groups.SelectMany(g => g.Connections).FirstOrDefault(c => c.Matches(form.Protocol, form.Host, form.Port, form.User));

    private ConnectionGroup? FindGroup(string connectionId) =>
        _groups.FirstOrDefault(g => g.Connections.Any(c => c.Id == connectionId));

    private ConnectionEntry? SelectedEntry => ConnectionTree.SelectedItem as ConnectionEntry;

    private void SaveGroups()
    {
        try { _store.Save(_groups); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Couldn't save connections: {ex.Message}");
        }
    }

    /// <summary>Saves, redraws the tree and re-applies the connected server's (possibly edited) group.</summary>
    private void Commit()
    {
        SaveGroups();
        ConnectionTree.ItemsSource = _groups.Select(g => ConnectionEntry.ForGroup(g, !_collapsedGroups.Contains(g.Id))).ToList();
        EmptyHint.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_activeConnectionId is { } id && FindGroup(id) is { } group)
        {
            _activeGroup = group;
            _activeLabel = group.Connections.First(c => c.Id == id).DisplayName;
        }
        ApplyGroupChrome();
    }

    /// <summary>Colours the title bar and remote pane with the connected server's group, and locks writes for read-only groups.</summary>
    private void ApplyGroupChrome()
    {
        var group = _client is null ? null : _activeGroup;
        if (_client is not null) _client.ReadOnlyGroup = group is { IsReadOnly: true } ? group.Name : null;
        (App.MainWindow as MainWindow)?.ShowConnection(group, _client is null ? null : _activeLabel);

        if (group is null)
        {
            GroupBadge.Visibility = Visibility.Collapsed;
            RemoteList.BorderBrush = _remoteListBorder;
            RemoteList.BorderThickness = new Thickness(1);
        }
        else
        {
            var color = GroupColors.Parse(group.Color);
            GroupBadge.Background = new SolidColorBrush(color);
            GroupBadgeText.Foreground = GroupBadgeLock.Foreground = new SolidColorBrush(GroupColors.ContrastText(color));
            GroupBadgeText.Text = group.LabelWithReadOnly.ToUpperInvariant();
            GroupBadgeLock.Visibility = group.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
            GroupBadge.Visibility = Visibility.Visible;
            RemoteList.BorderBrush = new SolidColorBrush(color);
            RemoteList.BorderThickness = new Thickness(2);
        }

        bool writable = _client is not { IsReadOnly: true };
        UploadButton.IsEnabled = NewFolderButton.IsEnabled = RenameButton.IsEnabled = DeleteButton.IsEnabled = writable;
    }

    /// <summary>True (and says why) when the connected server is in a read-only group.</summary>
    private bool WriteBlocked()
    {
        if (_client is not { ReadOnlyGroup: { } group }) return false;
        SetStatus($"\u201C{group}\u201D is read-only: browse and download only.");
        return true;
    }

    private async Task ConnectSavedAsync(SavedConnection saved)
    {
        var password = saved.Password;
        if (password.Length == 0 && saved.User.Length > 0)
        {
            var prompt = await ConnectionDialogs.PromptPasswordAsync(XamlRoot, saved);
            if (prompt is null) return;
            password = prompt.Password;
            if (prompt.Remember)
            {
                saved.Password = password;
                SaveGroups();
            }
        }
        if (_client is not null) await DisconnectAsync();
        FillForm(saved, password);
        await ConnectAsync(saved);
    }

    /// <summary>The group a new connection goes into: the selected one, the connected one, or the first; creates one if there are none.</summary>
    private async Task<ConnectionGroup?> TargetGroupAsync()
    {
        var existing = SelectedEntry?.Group ?? _activeGroup ?? _groups.FirstOrDefault();
        if (existing is not null) return existing;
        var group = new ConnectionGroup { Name = "", Color = GroupColors.Presets[4] };
        if (!await ConnectionDialogs.EditGroupAsync(XamlRoot, group, isNew: true)) return null;
        _groups.Add(group);
        Commit();
        return group;
    }

    private async void NewConnection_Click(object sender, RoutedEventArgs e)
    {
        if (await TargetGroupAsync() is { } group) await AddConnectionAsync(group, new SavedConnection());
    }

    private async void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = new ConnectionGroup { Name = "", Color = GroupColors.Presets[_groups.Count % GroupColors.Presets.Length] };
        if (!await ConnectionDialogs.EditGroupAsync(XamlRoot, group, isNew: true)) return;
        _groups.Add(group);
        Commit();
    }

    private async Task AddConnectionAsync(ConnectionGroup group, SavedConnection template)
    {
        var result = await ConnectionDialogs.EditConnectionAsync(XamlRoot, template, _groups, group, isNew: true);
        if (result is null) return;
        result.Group.Connections.Add(result.Connection);
        _collapsedGroups.Remove(result.Group.Id);
        Commit();
        SetStatus($"Saved \u201C{result.Connection.DisplayName}\u201D in {result.Group.Name}");
    }

    /// <param name="password">Pre-fills the dialog's password (e.g. one typed into the quick-connect row).</param>
    private async Task EditConnectionAsync(ConnectionGroup group, SavedConnection connection, string? password = null)
    {
        var template = connection.Clone();
        if (password is not null) template.Password = password;
        var result = await ConnectionDialogs.EditConnectionAsync(XamlRoot, template, _groups, group, isNew: false);
        if (result is null) return;
        var index = group.Connections.IndexOf(connection);
        if (index < 0) return;
        if (result.Group == group) group.Connections[index] = result.Connection;
        else
        {
            group.Connections.RemoveAt(index);
            result.Group.Connections.Add(result.Connection);
        }
        Commit();
        SetStatus($"Saved \u201C{result.Connection.DisplayName}\u201D");
    }

    private void DuplicateConnection(ConnectionGroup group, SavedConnection connection)
    {
        var copy = connection.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = connection.DisplayName + " (copy)";
        group.Connections.Insert(group.Connections.IndexOf(connection) + 1, copy);
        Commit();
    }

    private async Task DeleteConnectionAsync(ConnectionGroup group, SavedConnection connection)
    {
        if (!await ConfirmAsync("Delete connection", $"Delete \u201C{connection.DisplayName}\u201D and its saved password from \u201C{group.Name}\u201D?")) return;
        group.Connections.Remove(connection);
        Commit();
        SetStatus($"Deleted \u201C{connection.DisplayName}\u201D");
    }

    private async Task EditGroupAsync(ConnectionGroup group)
    {
        if (!await ConnectionDialogs.EditGroupAsync(XamlRoot, group, isNew: false)) return;
        Commit();
    }

    private void MoveGroup(ConnectionGroup group, int delta)
    {
        var index = _groups.IndexOf(group);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _groups.Count) return;
        _groups.RemoveAt(index);
        _groups.Insert(target, group);
        Commit();
    }

    private async Task DeleteGroupAsync(ConnectionGroup group)
    {
        var detail = group.Connections.Count == 0 ? "" : $" and its {group.Connections.Count} connection{(group.Connections.Count == 1 ? "" : "s")}";
        if (!await ConfirmAsync("Delete group", $"Delete \u201C{group.Name}\u201D{detail}? Nothing is changed on the servers.")) return;
        _groups.Remove(group);
        Commit();
    }

    // ---------- Connections tree ----------

    private ConnectionEntry? EntryFromSource(object source) =>
        source is DependencyObject d && FindParent<TreeViewItem>(d) is { } container
            ? ConnectionTree.ItemFromContainer(container) as ConnectionEntry
            : null;

    private async void ConnectionTree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (EntryFromSource(e.OriginalSource) is { Connection: { } saved })
        {
            e.Handled = true;
            await ConnectSavedAsync(saved);
        }
    }

    private async void ConnectionTree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || SelectedEntry is not { Connection: { } saved }) return;
        e.Handled = true;
        await ConnectSavedAsync(saved);
    }

    private void ConnectionTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is not ConnectionEntry { IsGroup: true } entry) return;
        entry.IsExpanded = true;
        _collapsedGroups.Remove(entry.Group.Id);
    }

    private void ConnectionTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (args.Item is not ConnectionEntry { IsGroup: true } entry) return;
        entry.IsExpanded = false;
        _collapsedGroups.Add(entry.Group.Id);
    }

    private void ConnectionTree_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var container = e.OriginalSource is DependencyObject d ? FindParent<TreeViewItem>(d) : null;
        if (container is null || ConnectionTree.ItemFromContainer(container) is not ConnectionEntry entry) return;
        e.Handled = true;
        ConnectionTree.SelectedItem = entry;

        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        void Separator() => menu.Items.Add(new MenuFlyoutSeparator());

        var group = entry.Group;
        if (entry.Connection is { } c)
        {
            Add("Connect", "\uE703", () => _ = ConnectSavedAsync(c));
            Separator();
            Add("Edit connection…", "\uE70F", () => _ = EditConnectionAsync(group, c));
            Add("Duplicate", "\uE8C8", () => DuplicateConnection(group, c));
            Separator();
            Add("Delete connection", "\uE74D", () => _ = DeleteConnectionAsync(group, c));
        }
        else
        {
            var index = _groups.IndexOf(group);
            Add("Add connection…", "\uE710", () => _ = AddConnectionAsync(group, new SavedConnection()));
            Add("Edit group…", "\uE70F", () => _ = EditGroupAsync(group));
            Separator();
            Add("Move up", "\uE70E", () => MoveGroup(group, -1), index > 0);
            Add("Move down", "\uE70D", () => MoveGroup(group, 1), index < _groups.Count - 1);
            Separator();
            Add("Delete group", "\uE74D", () => _ = DeleteGroupAsync(group));
        }

        if (e.TryGetPosition(container, out var point)) menu.ShowAt(container, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(container);
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
        RemoteFilterBox.Text = "";
        RemotePathBox.Text = "";
        RemoteTitle.Text = "Remote (not connected)";
        ConnectButton.Content = "Connect";
        _activeConnectionId = null;
        _activeGroup = null;
        ApplyGroupChrome();
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
        var items = await _client.ListAsync(path, ct);
        // A filter only applies to the folder it was typed in.
        if (path != _remotePath) RemoteFilterBox.Text = "";
        _remoteItems = items;
        _remotePath = path;
        RemotePathBox.Text = path;
        ApplySort();
    }

    private void RemoteFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_client is not null) ApplySort();
    }

    private void RemoteFilter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        RemoteFilterBox.Text = "";
        e.Handled = true;
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
        string filter = RemoteFilterBox.Text.Trim();
        var shown = filter.Length == 0
            ? _remoteItems
            : _remoteItems.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        RemoteList.ItemsSource = FileEntry.Sort(shown, _sortColumn, _sortAscending);
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
        if (_client is null || WriteBlocked()) return;
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
        if (_client is null || WriteBlocked() || RemoteList.SelectedItem is not FileEntry entry) return;
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
        if (_client is null || entries.Count == 0 || WriteBlocked()) return;
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
        if (_client is null || WriteBlocked()) return;
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        await UploadPathsAsync(files.Select(f => f.Path).ToList());
    }

    private async Task<bool> UploadPathsAsync(List<string> paths)
    {
        var client = _client;
        if (client is null || paths.Count == 0 || WriteBlocked()) return false;
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
        bool accept = _client is { IsReadOnly: false } && _drag is null && e.DataView.Contains(StandardDataFormats.StorageItems);
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
