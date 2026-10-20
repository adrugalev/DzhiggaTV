using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DzhiggaTV.App;

internal static class WindowBounds
{
    public static void Attach(MainWindow window)
        => Attach(window, () => window.IsFullscreen, () => window.IsMini);

    public static void Attach(Window window, Func<bool> fullscreen, Func<bool>? mini = null)
    {
        HwndSource? source = null;
        window.SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook(Hook);
        };
        window.Closed += (_, _) => source?.RemoveHook(Hook);
        IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x0024 || mini?.Invoke() == true) return IntPtr.Zero; // WM_GETMINMAXINFO
            var monitor = MonitorFromWindow(hwnd, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
            var bounds = WindowBoundsGeometry.Maximized(info.Monitor.ToRect(), info.Work.ToRect(), fullscreen());
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            limits.MaxPosition = new() { X = (int)bounds.X, Y = (int)bounds.Y };
            limits.MaxSize = new() { X = (int)bounds.Width, Y = (int)bounds.Height };
            var scale = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            limits.MinTrackSize.X = Math.Max(limits.MinTrackSize.X, (int)Math.Ceiling(window.MinWidth * scale.M11));
            limits.MinTrackSize.Y = Math.Max(limits.MinTrackSize.Y, (int)Math.Ceiling(window.MinHeight * scale.M22));
            Marshal.StructureToPtr(limits, lParam, false);
            handled = true; return IntPtr.Zero;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo
    { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
