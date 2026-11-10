using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Winzilla.Services;

namespace Winzilla;

public sealed partial class MainPage
{
    private const string ClipFormat = "Winzilla.RemoteItems";

    /// <summary>What this app last put on the clipboard, so pasting back into the app can skip the round trip through temp files.</summary>
    private sealed record ClipState(string Id, List<FileEntry> Items, string SourceDir, bool IsCut, PendingDownload Download);

    private ClipState? _clip;

    // ---------- Cut / copy ----------

    private void Copy_Click(object sender, RoutedEventArgs e) => SetClipboard(cut: false);

    private void Cut_Click(object sender, RoutedEventArgs e) => SetClipboard(cut: true);

    private void Copy_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetClipboard(cut: false);
    }

    private void Cut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetClipboard(cut: true);
    }

    private void SetClipboard(bool cut)
    {
        var client = _client;
        var items = SelectedEntries();
        if (client is null || items.Count == 0) return;

        // The files are only downloaded once something (Explorer, or a paste back into this app) asks for them.
        var download = CreateDownload(items, client);
        var clip = new ClipState(Guid.NewGuid().ToString("N"), items, _remotePath, cut, download);
        var data = new DataPackage();
        data.SetData(ClipFormat, clip.Id);
        SetStorageProvider(data, download, cut ? DataPackageOperation.Move : DataPackageOperation.Copy);
        if (cut)
        {
            data.OperationCompleted += (s, args) =>
            {
                // Explorer reports a completed move once it has pasted the files; only then is the remote copy removed.
                if (args.Operation == DataPackageOperation.Move && download.Succeeded)
                    _ = OnUiThreadAsync(async () => { await DeleteMovedAsync(client, clip); return true; });
            };
        }

        try { Clipboard.SetContent(data); }
        catch (Exception ex)
        {
            SetStatus($"Couldn't use the clipboard: {ex.Message}");
            return;
        }
        _clip = clip;
        SetStatus($"{(cut ? "Cut" : "Copied")} {items.Count} item(s)");
    }

    private async Task DeleteMovedAsync(IRemoteClient client, ClipState clip)
    {
        if (!ReferenceEquals(_client, client)) return;
        if (_clip == clip) _clip = null;
        await RunOpAsync(async ct =>
        {
            foreach (var entry in clip.Items) await client.DeleteAsync(entry, ct);
            await LoadRemoteAsync(_remotePath, ct);
        });
    }

    private void ClearOwnClipboard()
    {
        if (_clip is null) return;
        _clip = null;
        try { Clipboard.Clear(); } catch { }
    }

    // ---------- Paste ----------

    private async void Paste_Click(object sender, RoutedEventArgs e) => await PasteAsync();

    private async void Paste_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PasteAsync();
    }

    private static DataPackageView? GetClipboardView()
    {
        try { return Clipboard.GetContent(); }
        catch { return null; }
    }

    private async Task PasteAsync()
    {
        var client = _client;
        var view = GetClipboardView();
        if (client is null || view is null) return;

        if (_clip is { } clip && view.Contains(ClipFormat) && await view.GetDataAsync(ClipFormat) is string id && id == clip.Id)
        {
            await PasteOwnAsync(client, clip);
            return;
        }

        if (!view.Contains(StandardDataFormats.StorageItems))
        {
            SetStatus("Nothing to paste.");
            return;
        }

        // Explorer's "Cut" only offers Move; report it back after the upload so Explorer removes the originals.
        bool isMove = await IsExplorerCutAsync(view);
        var paths = await GetStoragePathsAsync(view);
        if (await UploadPathsAsync(paths) && isMove) view.ReportOperationCompleted(DataPackageOperation.Move);
    }

    // Explorer marks a cut with the "Preferred DropEffect" format holding DROPEFFECT_MOVE (2).
    private static async Task<bool> IsExplorerCutAsync(DataPackageView view)
    {
        try
        {
            if (!view.Contains("Preferred DropEffect")) return false;
            if (await view.GetDataAsync("Preferred DropEffect") is not IRandomAccessStream stream) return false;
            using var reader = new DataReader(stream);
            await reader.LoadAsync(4);
            return (reader.ReadUInt32() & 2) != 0;
        }
        catch { return false; }
    }

    private async Task PasteOwnAsync(IRemoteClient client, ClipState clip)
    {
        string target = _remotePath;
        if (clip.SourceDir == target)
        {
            SetStatus("The items are already in this folder.");
            return;
        }

        if (clip.IsCut)
        {
            bool moved = await RunOpAsync(async ct =>
            {
                foreach (var item in clip.Items)
                    await client.RenameAsync(item.FullPath, RemotePath.Combine(target, item.Name), ct);
                await LoadRemoteAsync(target, ct);
            });
            if (moved) ClearOwnClipboard();
            return;
        }

        try { await UploadPathsAsync(await clip.Download.GetAsync()); }
        catch (Exception ex) { SetStatus($"Couldn't paste: {ex.Message}"); }
    }

    // ---------- Other shortcuts ----------

    private void Rename_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        RemoteRename_Click(sender, null!);
    }

    private void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        RemoteRefresh_Click(sender, null!);
    }

    private void Properties_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Properties_Click(sender, null!);
    }

    // ---------- Context menu ----------

    private void RemoteList_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var item = e.OriginalSource is DependencyObject source ? FindParent<ListViewItem>(source) : null;
        if (item is null) RemoteList.SelectedItems.Clear();
        else if (!item.IsSelected)
        {
            RemoteList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T) node = VisualTreeHelper.GetParent(node);
        return node as T;
    }

    private void RemoteMenu_Opening(object sender, object e)
    {
        var selected = SelectedEntries();
        bool connected = _client is not null;
        bool any = connected && selected.Count > 0;
        var view = GetClipboardView();

        MenuOpen.Visibility = selected is [{ IsDirectory: true }] ? Visibility.Visible : Visibility.Collapsed;
        MenuDownload.IsEnabled = any;
        MenuUpload.IsEnabled = connected;
        MenuCut.IsEnabled = MenuCopy.IsEnabled = MenuDelete.IsEnabled = MenuProperties.IsEnabled = any;
        MenuPaste.IsEnabled = connected && view is not null
            && (view.Contains(ClipFormat) || view.Contains(StandardDataFormats.StorageItems));
        MenuRename.IsEnabled = connected && selected.Count == 1;
        MenuNewFolder.IsEnabled = MenuRefresh.IsEnabled = connected;
    }

    private async void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntries() is [{ IsDirectory: true } folder])
            await RunOpAsync(ct => LoadRemoteAsync(folder.FullPath, ct));
    }

    // ---------- Properties ----------

    private async void Properties_Click(object sender, RoutedEventArgs e)
    {
        var entries = SelectedEntries();
        if (_client is null || entries.Count == 0 || XamlRoot is null) return;

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6, MinWidth = 320 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        void AddRow(string label, string value)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Opacity = 0.7 };
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            grid.Children.Add(name);
            grid.Children.Add(text);
        }

        if (entries is [var entry])
        {
            AddRow("Name", entry.Name);
            AddRow("Type", entry.IsDirectory ? "Folder" : Path.GetExtension(entry.Name) is { Length: > 1 } ext
                ? $"{ext[1..].ToUpperInvariant()} file" : "File");
            AddRow("Location", RemotePath.Parent(entry.FullPath));
            if (!entry.IsDirectory) AddRow("Size", $"{FileEntry.FormatSize(entry.Size)} ({entry.Size:N0} bytes)");
            if (entry.Modified is not null) AddRow("Modified", entry.ModifiedText);
        }
        else
        {
            int folders = entries.Count(i => i.IsDirectory);
            long size = entries.Where(i => !i.IsDirectory).Sum(i => i.Size);
            AddRow("Selection", $"{entries.Count} items ({entries.Count - folders} files, {folders} folders)");
            AddRow("Location", _remotePath);
            AddRow("Size", FileEntry.FormatSize(size) + (folders > 0 ? " (folder contents not counted)" : ""));
        }

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Properties",
            Content = grid,
            CloseButtonText = "Close",
        }.ShowAsync();
    }
}
