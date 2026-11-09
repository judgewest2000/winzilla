using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Winzilla.Services;

/// <summary>Explorer-style icons from the shell, looked up by extension (no file access needed). Must be used on the UI thread.</summary>
public static class ShellIcons
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const uint UseFileAttributes = 0x10;
    private const uint GetIcon = 0x100;
    private const uint SmallIcon = 0x1;

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string name, bool isDirectory)
    {
        string ext = isDirectory ? "<dir>" : Path.GetExtension(name);
        return Cache.GetOrAdd(ext, _ => Load(ext, isDirectory));
    }

    private static ImageSource? Load(string ext, bool isDirectory)
    {
        var info = new ShFileInfo();
        string probe = isDirectory ? "folder" : "file" + ext;
        uint attrs = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
        if (SHGetFileInfo(probe, attrs, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), UseFileAttributes | GetIcon | SmallIcon) == 0
            || info.HIcon == 0)
            return null;

        try
        {
            using var icon = System.Drawing.Icon.FromHandle(info.HIcon);
            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);

            var image = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(ms.ToArray());
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            stream.Seek(0);
            image.SetSource(stream);
            return image;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.HIcon);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint HIcon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
}
