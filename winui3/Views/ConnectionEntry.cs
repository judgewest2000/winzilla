using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using FtpZilla.Services;

namespace FtpZilla.Views;

/// <summary>View-model for one row in the connections tree: a group, or a saved connection inside it.</summary>
public sealed class ConnectionEntry
{
    private ConnectionEntry(ConnectionGroup group, SavedConnection? connection)
    {
        Group = group;
        Connection = connection;
    }

    public static ConnectionEntry ForGroup(ConnectionGroup group, bool isExpanded)
    {
        var entry = new ConnectionEntry(group, null) { IsExpanded = isExpanded };
        foreach (var c in group.Connections) entry.Children.Add(new ConnectionEntry(group, c));
        return entry;
    }

    public ConnectionGroup Group { get; }
    public SavedConnection? Connection { get; }
    public bool IsGroup => Connection is null;
    public bool IsExpanded { get; set; }
    public ObservableCollection<ConnectionEntry> Children { get; } = [];

    public string Label => Connection?.DisplayName ?? Group.Name;
    public string Detail => Connection is { } c ? c.Summary : $"({Group.Connections.Count:N0})";

    public string AutomationName => Connection is { } c
        ? $"{c.DisplayName}, {c.Summary}"
        : $"{Group.Name} group{(Group.IsReadOnly ? ", read-only" : "")}, {Group.Connections.Count} connections";

    public string ToolTip => Connection is { } c
        ? $"{c.DisplayName}\n{c.Summary}\nDouble-click to connect"
        : Group.IsReadOnly ? $"{Group.Name} (read-only: browse and download only)" : Group.Name;

    public Visibility SwatchVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => IsGroup ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ReadOnlyVisibility => IsGroup && Group.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
    public Windows.UI.Text.FontWeight LabelWeight => IsGroup ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    /// <summary>Swatches and icons take the group colour so every row shows which environment it belongs to.</summary>
    public Brush GroupBrush => GroupColors.Brush(Group.Color);

    public override string ToString() => Label;
}
