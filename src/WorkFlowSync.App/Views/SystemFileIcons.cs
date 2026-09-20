using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace WorkFlowSync.App.Views;

/// <summary>
/// The icon Windows itself shows for a file type — the same one Explorer and OneDrive use, so a list of
/// documents looks familiar instead of looking like our invention.
///
/// Asked by EXTENSION, never by opening the file: <c>SHGFI_USEFILEATTRIBUTES</c> makes the shell answer
/// from the registry for a path that need not exist. That matters here, because the list is built from the
/// state database and the file may already be gone — and because touching a cloud placeholder would
/// download it.
///
/// Results are cached per extension: the answer is the same for every .docx on the machine, and the call
/// is not free.
/// </summary>
public static class SystemFileIcons
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetBitmapBits(IntPtr hbmp, int cbBuffer, IntPtr lpvBits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>The shell icon for this file name's type, or null when the shell has nothing to offer.</summary>
    public static Bitmap? For(string? fileName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(fileName)) return null;

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) ext = ".";     // one shared entry for «no extension»

        return Cache.GetOrAdd(ext, key =>
        {
            try
            {
                return Load("file" + (key == "." ? "" : key));
            }
            catch
            {
                // A missing icon is a cosmetic loss; the badge behind it still tells the user the type.
                return null;
            }
        });
    }

    private static Bitmap? Load(string pathPattern)
    {
        var info = new SHFILEINFO();
        var result = SHGetFileInfoW(pathPattern, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            return FromIcon(info.hIcon);
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>
    /// Copies the icon's colour bitmap into an Avalonia one. Done by hand through GDI rather than through
    /// System.Drawing so the portable build does not have to carry that dependency for one small feature.
    /// </summary>
    private static Bitmap? FromIcon(IntPtr hIcon)
    {
        if (!GetIconInfo(hIcon, out var icon)) return null;
        var colour = icon.hbmColor;
        var mask = icon.hbmMask;
        try
        {
            if (colour == IntPtr.Zero) return null;

            var bmp = new BITMAP();
            if (GetObject(colour, Marshal.SizeOf<BITMAP>(), ref bmp) == 0) return null;
            if (bmp.bmWidth <= 0 || bmp.bmHeight <= 0 || bmp.bmBitsPixel != 32) return null;

            var bytes = bmp.bmWidthBytes * bmp.bmHeight;
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                if (GetBitmapBits(colour, bytes, buffer) == 0) return null;
                // The shell hands back pre-multiplied BGRA, which is exactly what Avalonia wants.
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, buffer,
                    new Avalonia.PixelSize(bmp.bmWidth, bmp.bmHeight),
                    new Avalonia.Vector(96, 96), bmp.bmWidthBytes);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            if (colour != IntPtr.Zero) DeleteObject(colour);
            if (mask != IntPtr.Zero) DeleteObject(mask);
        }
    }
}
