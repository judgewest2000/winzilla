using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Winzilla
{
    /// <summary>Explorer-style icons from the shell, looked up by extension (no file access needed).</summary>
    public static class ShellIcons
    {
        private const uint FileAttributeDirectory = 0x10;
        private const uint FileAttributeNormal = 0x80;
        private const uint UseFileAttributes = 0x10;
        private const uint GetIcon = 0x100;
        private const uint SmallIcon = 0x1;

        /// <summary>Ensures the icon for this entry is in the list and returns its key ("" if unavailable).</summary>
        public static string EnsureIcon(ImageList list, string name, bool isDirectory)
        {
            string ext = isDirectory ? "" : Path.GetExtension(name).ToLowerInvariant();
            string key = isDirectory ? "<dir>" : (ext.Length == 0 ? "<none>" : ext);
            if (list.Images.ContainsKey(key)) return key;

            var bitmap = Load(ext, isDirectory);
            if (bitmap == null) return "";
            list.Images.Add(key, bitmap);
            return key;
        }

        private static Bitmap? Load(string ext, bool isDirectory)
        {
            var info = new ShFileInfo();
            string probe = isDirectory ? "folder" : "file" + ext;
            uint attrs = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
            if (SHGetFileInfo(probe, attrs, ref info, (uint)Marshal.SizeOf(typeof(ShFileInfo)), UseFileAttributes | GetIcon | SmallIcon) == IntPtr.Zero
                || info.HIcon == IntPtr.Zero)
                return null;

            try
            {
                using (var icon = Icon.FromHandle(info.HIcon))
                    return icon.ToBitmap();
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
        private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShFileInfo
        {
            public IntPtr HIcon;
            public int IconIndex;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
        }
    }
}
