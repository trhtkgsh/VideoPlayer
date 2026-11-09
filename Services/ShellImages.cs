using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoPlayer.Services;

/// <summary>Windows shell icons and thumbnails for media files.</summary>
internal static class ShellImages
{
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileAttributeDirectory = 0x10;
    private const uint SiigbfThumbnailOnly = 0x8;

    private static readonly string[] CoverBaseNames = ["cover", "folder", "front", "album"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp"];
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>Returns the large shell icon for the file type (cached per extension).</summary>
    public static ImageSource? GetFileIcon(string path)
    {
        var extension = Path.GetExtension(path);
        return GetCachedIcon(extension, "file" + extension, FileAttributeNormal, ShgfiIcon | ShgfiUseFileAttributes);
    }

    /// <summary>Returns the shell icon for a folder.</summary>
    public static ImageSource? GetFolderIcon()
    {
        return GetCachedIcon("<folder>", "folder", FileAttributeDirectory, ShgfiIcon | ShgfiUseFileAttributes);
    }

    /// <summary>Returns the shell icon for a drive root such as "C:\".</summary>
    public static ImageSource? GetDriveIcon(string root)
    {
        return GetCachedIcon("<drive>" + root, root, 0, ShgfiIcon);
    }

    private static ImageSource? GetCachedIcon(string key, string path, uint attributes, uint flags)
    {
        if (IconCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var info = new ShFileInfo { szDisplayName = string.Empty, szTypeName = string.Empty };
        var result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
        ImageSource? icon = null;
        if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero)
        {
            icon = ToImageSource(info.hIcon);
        }

        IconCache[key] = icon;
        return icon;
    }

    /// <summary>Returns the Windows thumbnail for a file, or null when the shell has no thumbnail provider for it.</summary>
    public static ImageSource? GetShellThumbnail(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        if (hr != 0 || factory is null)
        {
            return null;
        }

        try
        {
            if (factory.GetImage(new NativeSize { cx = size, cy = size }, SiigbfThumbnailOnly, out var hBitmap) != 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Finds a cover image next to an audio file (same name, or cover/folder/front/album).</summary>
    public static ImageSource? GetAudioCover(string audioPath, int decodeWidth)
    {
        var folder = Path.GetDirectoryName(audioPath);
        if (folder is null)
        {
            return null;
        }

        var baseNames = new[] { Path.GetFileNameWithoutExtension(audioPath) }.Concat(CoverBaseNames);
        foreach (var baseName in baseNames)
        {
            foreach (var ext in ImageExtensions)
            {
                var candidate = Path.Combine(folder, baseName + ext);
                if (File.Exists(candidate))
                {
                    return LoadImage(candidate, decodeWidth);
                }
            }
        }

        return null;
    }

    private static ImageSource? LoadImage(string path, int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ImageSource ToImageSource(IntPtr hIcon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
