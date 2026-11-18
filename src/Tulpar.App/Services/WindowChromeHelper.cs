using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Tulpar.App.Services;

/// <summary>
/// Configures native Windows DWM dark-mode caption, border, and text colors
/// so standard WPF window title bars match Tulpar's #000000 cockpit surface.
/// </summary>
internal static class WindowChromeHelper
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    // COLORREF uses 0x00BBGGRR layout
    private const int BlackColorRef = 0x00000000;
    private const int WhiteColorRef = 0x00FFFFFF;
    private const int HairlineBorderColorRef = 0x003F3A3A; // #3A3A3F

    public static void EnableDarkTitleBar(Window window)
    {
        window.SourceInitialized += OnWindowSourceInitialized;
    }

    private static void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.SourceInitialized -= OnWindowSourceInitialized;
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            int useDark = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));

            int captionColor = BlackColorRef;
            _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, sizeof(int));

            int textColor = WhiteColorRef;
            _ = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColor, sizeof(int));

            int borderColor = HairlineBorderColorRef;
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));
        }
        catch
        {
            // Ignore on Windows builds where DWM caption customization attributes are unsupported.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attr,
        ref int attrValue,
        int attrSize);
}
