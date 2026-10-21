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
            if (window is MainWindow { IsMini: true } main)
            {
                if (message == 0x0231) main.IsMiniSizing = true; // WM_ENTERSIZEMOVE
                else if (message == 0x0232) { main.IsMiniSizing = false; main.RefreshMiniVideoAspect(); } // WM_EXITSIZEMOVE
                else if (message == 0x0214) // WM_SIZING: constrain the native drag rectangle before Windows applies it.
                {
                    var miniScale = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                    var frame = main.MiniResizeFrame;
                    frame = new(frame.Left * miniScale.M11, frame.Top * miniScale.M22, frame.Right * miniScale.M11, frame.Bottom * miniScale.M22);
                    var miniMonitor = MonitorFromWindow(hwnd, 2);
                    var miniInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    var maximum = miniMonitor != IntPtr.Zero && GetMonitorInfo(miniMonitor, ref miniInfo) ? miniInfo.Work.ToRect().Size :
                        new Size(SystemParameters.WorkArea.Width * miniScale.M11, SystemParameters.WorkArea.Height * miniScale.M22);
                    var rectangle = Marshal.PtrToStructure<NativeRect>(lParam);
                    var fitted = WindowBoundsGeometry.ResizeMini(rectangle.ToRect(), wParam.ToInt32(), main.MiniVideoAspectRatio, frame,
                        new(main.MinWidth * miniScale.M11, main.MinHeight * miniScale.M22), maximum);
                    rectangle.Left = (int)Math.Round(fitted.Left); rectangle.Top = (int)Math.Round(fitted.Top);
                    rectangle.Right = (int)Math.Round(fitted.Right); rectangle.Bottom = (int)Math.Round(fitted.Bottom);
                    Marshal.StructureToPtr(rectangle, lParam, false);
                    handled = true; return new IntPtr(1);
                }
            }
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
