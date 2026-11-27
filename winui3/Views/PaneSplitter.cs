using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FtpZilla.Views;

/// <summary>
/// Vertical drag handle that resizes column <see cref="Target"/> of its parent Grid;
/// double-click restores the original width.
/// </summary>
public sealed class PaneSplitter : Grid
{
    private readonly Border _line;
    private readonly Border _grip;
    private bool _dragging;
    private bool _hover;
    private double _start;
    private double _startWidth;
    private GridLength? _original;

    /// <summary>Index of the column to resize; -1 means the one before the splitter.</summary>
    public int Target { get; set; } = -1;

    public bool ShowLine
    {
        get => _line.Visibility == Visibility.Visible;
        set => _line.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public PaneSplitter()
    {
        Width = 12;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        _line = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch };
        _grip = new Border
        {
            Width = 4,
            Height = 40,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(_line);
        Children.Add(_grip);
        UpdateVisual();
        ActualThemeChanged += (_, _) => UpdateVisual();

        PointerEntered += (_, _) => { _hover = true; UpdateVisual(); };
        PointerExited += (_, _) => { _hover = false; UpdateVisual(); };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => EndDrag(e);
        PointerCaptureLost += (_, _) => { _dragging = false; UpdateVisual(); };
        DoubleTapped += OnDoubleTapped;
    }

    private Grid? Host => Parent as Grid;

    private int Index => Target >= 0 ? Target : Grid.GetColumn(this) - 1;

    private ColumnDefinition? TargetColumn(Grid host) =>
        Index >= 0 && Index < host.ColumnDefinitions.Count ? host.ColumnDefinitions[Index] : null;

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void UpdateVisual()
    {
        var active = _dragging || _hover;
        _line.Background = active ? Res("AccentFillColorDefaultBrush") : Res("DividerStrokeColorDefaultBrush");
        _line.Width = active ? 2 : 1;
        _grip.Background = active ? Res("AccentFillColorDefaultBrush") : Res("ControlStrongFillColorDefaultBrush");
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Host is not { } host || TargetColumn(host) is not { } column) return;
        _original ??= column.Width;
        _dragging = CapturePointer(e.Pointer);
        _start = e.GetCurrentPoint(host).Position.X;
        _startWidth = column.ActualWidth;
        UpdateVisual();
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || Host is not { } host || TargetColumn(host) is not { } column) return;
        var delta = e.GetCurrentPoint(host).Position.X - _start;
        if (Index > Grid.GetColumn(this)) delta = -delta; // target is after the splitter
        var width = Math.Clamp(_startWidth + delta, column.MinWidth, Math.Max(column.MinWidth, MaxWidthFor(host)));
        column.Width = new GridLength(width);
        e.Handled = true;
    }

    /// <summary>Space left once every other column keeps at least its minimum (or fixed) width.</summary>
    private double MaxWidthFor(Grid host)
    {
        var others = 0.0;
        for (var i = 0; i < host.ColumnDefinitions.Count; i++)
        {
            if (i == Index) continue;
            var c = host.ColumnDefinitions[i];
            others += c.Width.IsStar ? c.MinWidth : c.ActualWidth;
        }
        var spacing = host.ColumnSpacing * Math.Max(0, host.ColumnDefinitions.Count - 1);
        var total = host.ActualWidth - host.Padding.Left - host.Padding.Right;
        return Math.Min(host.ColumnDefinitions[Index].MaxWidth, total - others - spacing);
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        ReleasePointerCapture(e.Pointer);
        _dragging = false;
        UpdateVisual();
        e.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Host is { } host && TargetColumn(host) is { } column && _original is { } original) column.Width = original;
        e.Handled = true;
    }
}
