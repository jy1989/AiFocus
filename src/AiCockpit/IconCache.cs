using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AiCockpit;

/// <summary>App icons (from the window, else the executable) and browser favicons (data URLs from the extension).</summary>
public static class IconCache
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
    private const uint WmGetIcon = 0x7F, SmtoAbortIfHung = 0x2;
    private const int IconBig = 1, IconSmall2 = 2, GclpHIcon = -14;

    private static readonly Dictionary<string, ImageSource?> AppIcons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource?> DataUrlIcons = new(StringComparer.Ordinal);

    /// <summary>Cached per process name: one lookup per app, not per refresh.</summary>
    public static ImageSource? ForWindow(IntPtr hwnd, string process, string executablePath)
    {
        // Keyed by executable when known: Codex and ChatGPT desktop share the process name "ChatGPT".
        var key = string.IsNullOrEmpty(executablePath) ? process : executablePath;
        if (AppIcons.TryGetValue(key, out var cached)) return cached;
        var icon = FromWindow(hwnd) ?? FromExecutable(executablePath);
        AppIcons[key] = icon;
        return icon;
    }

    private static ImageSource? FromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        // Timeout guards against a hung target window.
        IntPtr handle = IntPtr.Zero;
        foreach (var kind in new[] { IconBig, IconSmall2 })
        {
            SendMessageTimeout(hwnd, WmGetIcon, kind, IntPtr.Zero, SmtoAbortIfHung, 100, out handle);
            if (handle != IntPtr.Zero) break;
        }
        if (handle == IntPtr.Zero) handle = GetClassLongPtr(hwnd, GclpHIcon);
        if (handle == IntPtr.Zero) return null;
        try
        {
            // The icon belongs to the other window; we only copy its pixels and never destroy it.
            var source = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static ImageSource? FromExecutable(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException) { return null; }
    }

    /// <summary>Decodes a "data:image/...;base64," favicon sent by the browser extension.</summary>
    public static ImageSource? FromDataUrl(string? dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl)) return null;
        if (DataUrlIcons.TryGetValue(dataUrl, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            int comma = dataUrl.IndexOf(',');
            if (dataUrl.StartsWith("data:image/", StringComparison.Ordinal) && comma > 0 && dataUrl[..comma].EndsWith(";base64", StringComparison.Ordinal))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(Convert.FromBase64String(dataUrl[(comma + 1)..]));
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException or InvalidOperationException) { }
        if (DataUrlIcons.Count > 300) DataUrlIcons.Clear();
        DataUrlIcons[dataUrl] = image;
        return image;
    }
}
