using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Winzilla.Services;

namespace Winzilla.Views;

/// <summary>The edited copy of a saved connection and the group it should live in.</summary>
public sealed class ConnectionEditResult(SavedConnection connection, ConnectionGroup group)
{
    public SavedConnection Connection { get; } = connection;
    public ConnectionGroup Group { get; } = group;
}

/// <summary>A password typed at connect time, and whether to save it with the connection.</summary>
public sealed class PasswordPromptResult(string password, bool remember)
{
    public string Password { get; } = password;
    public bool Remember { get; } = remember;
}

/// <summary>Code-built ContentDialogs for editing groups and saved connections.</summary>
public static class ConnectionDialogs
{
    private static ContentDialog Create(XamlRoot root, string title, object content, string primary) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary,
    };

    /// <summary>Edits a group's name, colour and read-only flag in place. Returns false if cancelled.</summary>
    public static async Task<bool> EditGroupAsync(XamlRoot root, ConnectionGroup group, bool isNew)
    {
        var nameBox = new TextBox { Header = "Name", Text = group.Name, PlaceholderText = "e.g. Production" };
        var color = GroupColors.Parse(group.Color);

        var preview = new Border { Height = 36, CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 0, 10, 0) };
        var previewText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        preview.Child = previewText;

        var picker = new ColorPicker
        {
            Color = color,
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            ColorSpectrumShape = ColorSpectrumShape.Ring,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(picker, "Custom colour");

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void UpdatePreview()
        {
            preview.Background = new SolidColorBrush(color);
            previewText.Foreground = new SolidColorBrush(GroupColors.ContrastText(color));
            previewText.Text = $"{(nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : "Group")} · the window uses this colour";
            foreach (var b in swatches.Children.OfType<Button>())
                b.BorderBrush = new SolidColorBrush(GroupColors.ToHex(color) == (string)b.Tag ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Transparent);
        }
        foreach (var hex in GroupColors.Presets)
        {
            var swatch = new Button
            {
                Width = 28, Height = 28, Padding = new Thickness(0), Tag = hex,
                Background = GroupColors.Brush(hex), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(14),
            };
            ToolTipService.SetToolTip(swatch, hex);
            AutomationProperties.SetName(swatch, $"Colour {hex}");
            swatch.Click += (_, _) => { color = GroupColors.Parse(hex); picker.Color = color; UpdatePreview(); };
            swatches.Children.Add(swatch);
        }
        var customButton = new HyperlinkButton { Content = "Custom colour…", Padding = new Thickness(4, 0, 4, 0) };
        customButton.Click += (_, _) => picker.Visibility = picker.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        swatches.Children.Add(customButton);
        picker.ColorChanged += (_, args) => { color = Windows.UI.Color.FromArgb(255, args.NewColor.R, args.NewColor.G, args.NewColor.B); UpdatePreview(); };
        nameBox.TextChanged += (_, _) => UpdatePreview();

        var readOnly = new ToggleSwitch
        {
            Header = "Read-only",
            IsOn = group.IsReadOnly,
            OnContent = "Browse and download only — no uploading, deleting, renaming or new folders",
            OffContent = "Off",
        };

        var panel = new StackPanel { Spacing = 12, Width = 440 };
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "Colour" });
        panel.Children.Add(swatches);
        panel.Children.Add(picker);
        panel.Children.Add(preview);
        panel.Children.Add(readOnly);
        UpdatePreview();
        nameBox.Loaded += (_, _) => { nameBox.Focus(FocusState.Programmatic); nameBox.SelectAll(); };

        var dialog = Create(root, isNew ? "New group" : "Edit group", new ScrollViewer { Content = panel }, isNew ? "Create" : "Save");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        group.Name = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : "New Group";
        group.Color = GroupColors.ToHex(color);
        group.IsReadOnly = readOnly.IsOn;
        return true;
    }

    /// <summary>Edits a copy of <paramref name="connection"/>. Returns the edited copy and chosen group, or null if cancelled.</summary>
    public static async Task<ConnectionEditResult?> EditConnectionAsync(
        XamlRoot root, SavedConnection connection, IReadOnlyList<ConnectionGroup> groups, ConnectionGroup group, bool isNew)
    {
        var groupBox = new ComboBox { Header = "Group", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var g in groups)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            item.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = GroupColors.Brush(g.Color), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = g.LabelWithReadOnly });
            AutomationProperties.SetName(item, g.LabelWithReadOnly);
            groupBox.Items.Add(item);
        }
        groupBox.SelectedIndex = Math.Max(0, groups.ToList().IndexOf(group));

        var nameBox = new TextBox { Header = "Display name (optional)", Text = connection.Name, PlaceholderText = "Defaults to user@host" };
        var protocolBox = new ComboBox { Header = "Protocol", Width = 110, Items = { "SFTP", "FTP" }, SelectedIndex = (int)connection.Protocol };
        var hostBox = new TextBox { Header = "Host", Text = connection.Host, PlaceholderText = "example.com", IsSpellCheckEnabled = false };
        var portBox = new TextBox { Header = "Port", Text = connection.Port.ToString(), Width = 80 };
        var userBox = new TextBox { Header = "Username", Text = connection.User, IsSpellCheckEnabled = false };
        var passwordBox = new PasswordBox { Header = "Password", Password = connection.Password, PlaceholderText = "Leave empty to be asked when connecting" };
        var hint = new TextBlock
        {
            Text = "The password is encrypted for your Windows account (DPAPI) and stored in %LOCALAPPDATA%\\Winzilla\\connections.json.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        protocolBox.SelectionChanged += (_, _) =>
        {
            if (portBox.Text is "22" or "21" or "")
                portBox.Text = SavedConnection.DefaultPort(protocolBox.SelectedIndex == 0 ? Protocol.Sftp : Protocol.Ftp).ToString();
        };

        var serverRow = new Grid { ColumnSpacing = 8 };
        serverRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        serverRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        serverRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(hostBox, 1);
        Grid.SetColumn(portBox, 2);
        serverRow.Children.Add(protocolBox);
        serverRow.Children.Add(hostBox);
        serverRow.Children.Add(portBox);

        var panel = new StackPanel { Spacing = 12, Width = 440 };
        panel.Children.Add(groupBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(serverRow);
        panel.Children.Add(userBox);
        panel.Children.Add(passwordBox);
        panel.Children.Add(hint);
        (isNew && connection.Host.Length == 0 ? hostBox : nameBox).Loaded += (s, _) => ((Control)s).Focus(FocusState.Programmatic);

        var dialog = Create(root, isNew ? "New connection" : "Edit connection", new ScrollViewer { Content = panel }, isNew ? "Add" : "Save");
        dialog.IsPrimaryButtonEnabled = hostBox.Text.Trim().Length > 0;
        hostBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = hostBox.Text.Trim().Length > 0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        var result = connection.Clone();
        result.Name = nameBox.Text.Trim();
        result.Protocol = protocolBox.SelectedIndex == 1 ? Protocol.Ftp : Protocol.Sftp;
        result.Host = hostBox.Text.Trim();
        result.Port = int.TryParse(portBox.Text, out var port) && port is >= 1 and <= 65535 ? port : SavedConnection.DefaultPort(result.Protocol);
        result.User = userBox.Text.Trim();
        result.Password = passwordBox.Password;
        return new ConnectionEditResult(result, groups[Math.Max(0, groupBox.SelectedIndex)]);
    }

    /// <summary>Asks for the password of a saved connection that doesn't store one. Returns null if cancelled.</summary>
    public static async Task<PasswordPromptResult?> PromptPasswordAsync(XamlRoot root, SavedConnection connection)
    {
        var box = new PasswordBox { Header = $"Password for {connection.Address}", Width = 360 };
        var remember = new CheckBox { Content = "Save password (encrypted for your Windows account)" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(box);
        panel.Children.Add(remember);
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var dialog = Create(root, $"Connect to {connection.DisplayName}", panel, "Connect");
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? new PasswordPromptResult(box.Password, remember.IsChecked == true) : null;
    }
}
