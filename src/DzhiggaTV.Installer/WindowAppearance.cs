using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DzhiggaTV.Installer;

internal static class WindowAppearance
{
    public static void Attach(Window window) => window.SourceInitialized += (_, _) =>
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(window).Handle;
        Set(20, 1); // Immersive dark mode for caption buttons.
        Set(34, 0x003A3229); // Border: #29323A.
        Set(35, 0x00120F0C); // Caption: #0C0F12.
        Set(36, 0x00F5F3EE); // Text: #EEF3F5.
        void Set(int attribute, int value) => _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
    };

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
