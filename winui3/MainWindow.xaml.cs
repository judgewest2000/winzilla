using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using FtpZilla.Services;
using FtpZilla.Views;

namespace FtpZilla;

/// <summary>The application window. It hosts <see cref="MainPage"/> and owns the title bar.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        RootFrame.Navigate(typeof(MainPage));
    }

    /// <summary>Tints the title bar with the connected server's group colour, or clears it when <paramref name="group"/> is null.</summary>
    public void ShowConnection(ConnectionGroup? group, string? connectionName)
    {
        AppTitleBar.Subtitle = connectionName ?? "";
        Title = connectionName is null ? "FtpZilla" : $"{connectionName} - FtpZilla";
        if (group is null)
        {
            AppTitleBar.Background = null;
            TitleGroupBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var color = GroupColors.Parse(group.Color);
        var text = new SolidColorBrush(GroupColors.ContrastText(color));
        AppTitleBar.Background = new SolidColorBrush(GroupColors.WithAlpha(color, 0x50));
        TitleGroupBadge.Background = new SolidColorBrush(color);
        TitleGroupText.Foreground = TitleGroupLock.Foreground = text;
        TitleGroupText.Text = group.LabelWithReadOnly.ToUpperInvariant();
        TitleGroupLock.Visibility = group.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
        TitleGroupBadge.Visibility = Visibility.Visible;
    }
}
