using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Winzilla.Views;

/// <summary>Converts group colours (#RRGGBB) into the brushes used for tree swatches, the title bar and the remote pane.</summary>
public static class GroupColors
{
    public static readonly string[] Presets =
    [
        "#C42B1C", "#E3008C", "#D97B06", "#E8C547", "#2EA043", "#00A3A3", "#2575D6", "#8E4EC6", "#7A7574",
    ];

    public static Color Parse(string? hex)
    {
        var s = (hex ?? "").Trim().TrimStart('#');
        if (s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return Color.FromArgb(255, 0x7A, 0x75, 0x74);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>Black or white, whichever reads better on <paramref name="c"/>.</summary>
    public static Color ContrastText(Color c)
    {
        static double Lin(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        var luminance = 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        return luminance > 0.4 ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255);
    }

    public static SolidColorBrush Brush(string? hex, byte alpha = 255) => new(WithAlpha(Parse(hex), alpha));
}
