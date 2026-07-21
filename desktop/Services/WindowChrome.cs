using System.Runtime.InteropServices;

namespace Jodu.Desktop.Services;

internal static class WindowChrome
{
    public const int WmNchittest = 0x0084;
    public const int WmNcLButtonDown = 0x00A1;
    public const int HtCaption = 0x2;
    public const int HtLeft = 10;
    public const int HtRight = 11;
    public const int HtTop = 12;
    public const int HtTopLeft = 13;
    public const int HtTopRight = 14;
    public const int HtBottom = 15;
    public const int HtBottomLeft = 16;
    public const int HtBottomRight = 17;
    public const int HtClient = 1;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWcpRound = 2;

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hWnd, bool invert);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Flash(IntPtr hwnd)
    {
        try { FlashWindow(hwnd, true); }
        catch { /* ignore */ }
    }

    public static void TryEnableRoundedCorners(IntPtr hwnd)
    {
        try
        {
            var preference = DwmWcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // older Windows
        }
    }

    public static void BeginDrag(IntPtr hwnd)
    {
        ReleaseCapture();
        SendMessage(hwnd, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    public static int HitTest(System.Windows.Forms.Form form, Point clientPoint, int grip = 8)
    {
        var w = form.ClientSize.Width;
        var h = form.ClientSize.Height;
        var x = clientPoint.X;
        var y = clientPoint.Y;

        var left = x < grip;
        var right = x > w - grip;
        var top = y < grip;
        var bottom = y > h - grip;

        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return HtClient;
    }
}
