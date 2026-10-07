using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using WinTabs.Interop;
using static WinTabs.Interop.NativeMethods;

namespace WinTabs.Core;

public static class IconHelper
{
    /// <summary>
    /// The HICON a window reports for its title bar (Explorer reports the current folder's icon,
    /// just like its own tabs show). Falls back to the class icon. The returned handle is owned by
    /// the target window – copy it if you need to keep it.
    /// </summary>
    public static IntPtr GetWindowIconHandle(IntPtr hwnd)
    {
        foreach (var which in new[] { ICON_SMALL2, ICON_SMALL, ICON_BIG })
        {
            SendMessageTimeout(hwnd, WM_GETICON, new IntPtr(which), IntPtr.Zero, SMTO_ABORTIFHUNG | SMTO_BLOCK, 250, out var result);
            if (result != IntPtr.Zero) return result;
        }
        var cls = GetClassLongPtr(hwnd, GCLP_HICONSM);
        if (cls == IntPtr.Zero) cls = GetClassLongPtr(hwnd, GCLP_HICON);
        return cls;
    }

    /// <summary>Icon from an executable (caller owns the returned handle).</summary>
    public static IntPtr ExtractExeIcon(string? path, bool small = true)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return IntPtr.Zero;
        var large = new IntPtr[1];
        var sm = new IntPtr[1];
        ExtractIconEx(path, 0, large, sm, 1);
        var pick = small ? sm[0] : large[0];
        var other = small ? large[0] : sm[0];
        if (pick == IntPtr.Zero) { pick = other; other = IntPtr.Zero; }
        if (other != IntPtr.Zero) DestroyIcon(other);
        return pick;
    }

    /// <summary>Window icon, falling back to the exe icon. Caller owns the result (it's always a copy).</summary>
    public static IntPtr GetIconCopyForWindow(IntPtr hwnd, string? processPath)
    {
        var h = GetWindowIconHandle(hwnd);
        if (h != IntPtr.Zero)
        {
            var copy = CopyIcon(h);
            if (copy != IntPtr.Zero) return copy;
        }
        return ExtractExeIcon(processPath);
    }

    /// <summary>Converts an HICON to a XAML ImageSource (BGRA premultiplied). Does not destroy the icon.</summary>
    public static ImageSource? ToImageSource(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        if (!GetIconInfo(hIcon, out var info)) return null;
        try
        {
            IntPtr bmpHandle = info.hbmColor != IntPtr.Zero ? info.hbmColor : info.hbmMask;
            if (GetObject(bmpHandle, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
            int w = bm.bmWidth;
            int h = info.hbmColor != IntPtr.Zero ? bm.bmHeight : bm.bmHeight / 2;
            if (w <= 0 || h <= 0 || w > 512 || h > 512) return null;

            var hdc = GetDC(IntPtr.Zero);
            try
            {
                var pixels = ReadBgra(hdc, info.hbmColor != IntPtr.Zero ? info.hbmColor : info.hbmMask, w, h);
                if (pixels == null) return null;

                bool hasAlpha = false;
                for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }

                if (!hasAlpha && info.hbmMask != IntPtr.Zero)
                {
                    // Old-style icon: derive alpha from the AND mask (black = opaque).
                    var mask = ReadBgra(hdc, info.hbmMask, w, h);
                    if (mask != null)
                        for (int i = 0; i < pixels.Length; i += 4)
                            pixels[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
                }
                else if (!hasAlpha)
                {
                    for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                }

                // Premultiply.
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int a = pixels[i + 3];
                    if (a == 255) continue;
                    pixels[i] = (byte)(pixels[i] * a / 255);
                    pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                    pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
                }

                var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
                sb.CopyFromBuffer(pixels.AsBuffer());
                var source = new SoftwareBitmapSource();
                _ = source.SetBitmapAsync(sb);
                return source;
            }
            finally { ReleaseDC(IntPtr.Zero, hdc); }
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    private static byte[]? ReadBgra(IntPtr hdc, IntPtr hbm, int w, int h)
    {
        var bi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            }
        };
        var buffer = new byte[w * h * 4];
        int lines = GetDIBits(hdc, hbm, 0, (uint)h, buffer, ref bi, 0);
        return lines == 0 ? null : buffer;
    }
}
