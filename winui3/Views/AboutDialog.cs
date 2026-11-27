using FtpZilla.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FtpZilla.Views;

/// <summary>The About dialog: artwork, version, keyboard shortcuts, update check and release notes.</summary>
public static class AboutDialog
{
    private sealed class KeyboardShortcut
    {
        public required string Keys { get; init; }
        public required string Action { get; init; }
    }

    private static readonly KeyboardShortcut[] Shortcuts =
    [
        new() { Keys = "Enter", Action = "Connect, or open the selected folder or saved connection" },
        new() { Keys = "F5", Action = "Refresh the remote folder" },
        new() { Keys = "F2", Action = "Rename the selected item" },
        new() { Keys = "Delete", Action = "Delete the selected items" },
        new() { Keys = "Ctrl+C", Action = "Copy selected files (paste into Explorer to download)" },
        new() { Keys = "Ctrl+X", Action = "Cut selected files" },
        new() { Keys = "Ctrl+V", Action = "Paste files from Explorer or a cut" },
        new() { Keys = "Alt+Enter", Action = "Show properties" },
        new() { Keys = "Esc", Action = "Clear the folder filter" },
    ];

    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");

    private static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private static Style Res(string key) => (Style)Application.Current.Resources[key];

    /// <summary>Shows About. Returns an update if the user found one via "Check for updates".</summary>
    public static async Task<UpdateInfo?> ShowAsync(XamlRoot root)
    {
        var overview = BuildOverview();
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Keyboard shortcuts", Style = Res("BodyStrongTextBlockStyle") });
        panel.Children.Add(BuildShortcuts());
        panel.Children.Add(new TextBlock
        {
            Text = "Copyright (c) 2026 James West. Released under the MIT License.",
            Foreground = Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        panel.Children.Add(new HyperlinkButton
        {
            Content = $"github.com/{UpdateService.Repo}",
            NavigateUri = new Uri($"https://github.com/{UpdateService.Repo}"),
            Padding = new Thickness(0),
        });

        var layout = new Grid { ColumnSpacing = 24, Width = Math.Min(900, Math.Max(0, root.Size.Width - 96)) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(panel, 1);
        layout.Children.Add(overview);
        layout.Children.Add(panel);
        var about = new ScrollViewer { Content = layout };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Content = about,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1000.0;
        dialog.Resources["ContentDialogMaxHeight"] = Math.Max(0, root.Size.Height - 48);

        // WinUI shows one ContentDialog at a time, so release notes replace the About content rather than stacking.
        var notesButton = new Button { Content = "Release notes" };
        notesButton.Click += (_, _) => dialog.Content = BuildReleaseNotes(() => dialog.Content = about, layout.Width);

        UpdateInfo? found = null;
        var checkResult = new TextBlock { Foreground = Secondary, TextWrapping = TextWrapping.Wrap };
        var checkButton = new Button { Content = "Check for updates" };
        checkButton.Click += async (_, _) =>
        {
            checkButton.IsEnabled = false;
            checkResult.Text = "Checking…";
            try
            {
                found = await UpdateService.CheckAsync(CancellationToken.None);
                if (found is null) checkResult.Text = "You're up to date.";
                else dialog.Hide();
            }
            catch (Exception ex) { checkResult.Text = $"Couldn't check for updates: {ex.Message}"; }
            finally { checkButton.IsEnabled = true; }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(checkButton);
        buttons.Children.Add(notesButton);
        panel.Children.Add(buttons);
        panel.Children.Add(checkResult);

        await dialog.ShowAsync();
        return found;
    }

    private static StackPanel BuildOverview()
    {
        var overview = new StackPanel { Spacing = 10, Width = 220 };
        var art = new Image
        {
            Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AboutArt.png"))),
            Width = 160, Height = 160,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetAccessibilityView(art, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        overview.Children.Add(art);
        overview.Children.Add(new TextBlock { Text = "FtpZilla", HorizontalAlignment = HorizontalAlignment.Center, Style = Res("TitleTextBlockStyle") });
        overview.Children.Add(new TextBlock
        {
            Text = $"Version {UpdateService.CurrentVersion.ToString(3)}", Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center,
        });
        overview.Children.Add(new TextBlock
        {
            Text = "A lightweight FTP and SFTP file-transfer client — colour-coded connection groups, read-only groups and Explorer copy/paste.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 8),
        });
        return overview;
    }

    private static Grid BuildShortcuts()
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < Shortcuts.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = Shortcuts[i].Keys, FontFamily = CodeFont };
            var action = new TextBlock { Text = Shortcuts[i].Action, Foreground = Secondary, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keys);
            grid.Children.Add(action);
        }
        return grid;
    }

    /// <summary>Copies CHANGELOG.md wherever the user picks; returns a status line (shown inline because only one ContentDialog can be open).</summary>
    private static async Task<string> ExportReleaseNotesAsync(string changelogPath)
    {
        var picker = new FileSavePicker { SuggestedFileName = "FtpZilla release notes" };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
        picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });
        var file = await picker.PickSaveFileAsync();
        if (file == null) return "";
        try
        {
            File.Copy(changelogPath, file.Path, overwrite: true);
            return $"Saved to {file.Path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't save: {ex.Message}";
        }
    }

    /// <summary>What changed in each version, read from the CHANGELOG.md shipped next to the exe.</summary>
    private static ScrollViewer BuildReleaseNotes(Action back, double width)
    {
        var panel = new StackPanel { Spacing = 4, Width = width };
        var backButton = new HyperlinkButton { Content = "← Back to About", Padding = new Thickness(0) };
        backButton.Click += (_, _) => back();
        panel.Children.Add(backButton);

        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock { Text = "Release notes", Style = Res("SubtitleTextBlockStyle") });
        var changelogPath = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
        var exportStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Secondary, Visibility = Visibility.Collapsed };
        var exportButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new FontIcon { Glyph = "\uEDE1", FontSize = 14 }, new TextBlock { Text = "Export…" } },
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(exportButton, "Save the full release notes as a Markdown file");
        AutomationProperties.SetName(exportButton, "Export release notes");
        exportButton.Click += async (_, _) =>
        {
            exportStatus.Text = await ExportReleaseNotesAsync(changelogPath);
            exportStatus.Visibility = exportStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        Grid.SetColumn(exportButton, 1);
        titleRow.Children.Add(exportButton);
        panel.Children.Add(titleRow);
        panel.Children.Add(exportStatus);

        IReadOnlyList<ReleaseNote> releases;
        try { releases = ReleaseNotes.Parse(File.ReadAllText(changelogPath)); }
        catch (IOException) { releases = []; }
        if (releases.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "Release notes aren't available.", Foreground = Secondary });
            exportButton.IsEnabled = false;
        }

        var current = UpdateService.CurrentVersion.ToString(3);
        foreach (var release in releases)
        {
            panel.Children.Add(BuildHeading(release, release.Version == current));
            foreach (var item in release.Items) panel.Children.Add(BuildBullet(item));
        }
        return new ScrollViewer { Content = panel };
    }

    private static TextBlock BuildHeading(ReleaseNote release, bool isCurrent)
    {
        var heading = new TextBlock { Margin = new Thickness(0, 12, 0, 2), Style = Res("BodyStrongTextBlockStyle") };
        heading.Inlines.Add(new Run { Text = $"Version {release.Version}" });
        if (release.Date is not null)
            heading.Inlines.Add(new Run { Text = $"   {release.Date}", Foreground = Secondary, FontWeight = FontWeights.Normal });
        if (isCurrent)
            heading.Inlines.Add(new Run { Text = "   (this version)", Foreground = Secondary, FontWeight = FontWeights.Normal });
        return heading;
    }

    private static TextBlock BuildBullet(NoteItem item)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12 + item.Level * 16, 0, 0, 0) };
        line.Inlines.Add(new Run { Text = item.Level == 0 ? "•  " : "◦  " });
        foreach (var span in item.Spans)
        {
            var run = new Run { Text = span.Text };
            switch (span.Style)
            {
                case NoteStyle.Bold: run.FontWeight = FontWeights.SemiBold; break;
                case NoteStyle.Italic: run.FontStyle = Windows.UI.Text.FontStyle.Italic; break;
                case NoteStyle.Code: run.FontFamily = CodeFont; break;
            }
            line.Inlines.Add(run);
        }
        return line;
    }
}
