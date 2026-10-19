using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DzhiggaTV.App;

internal static class WindowAppearance
{
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
        Apply(window);
    }

    // Apply the theme to native Windows 11 frame surfaces.
    // https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = Theme.IsLight ? 0 : 1;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        SetColor(34, "BorderBrush");
        SetColor(35, "BackgroundBrush");
        SetColor(36, "TextBrush");

        void SetColor(int attribute, string resource)
        {
            if (window.TryFindResource(resource) is not SolidColorBrush brush) return;
            var color = brush.Color.R | (brush.Color.G << 8) | (brush.Color.B << 16);
            _ = DwmSetWindowAttribute(handle, attribute, ref color, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
