using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DzhiggaTV.Player;

/// <summary>A persistent native child surface. Change the containing Window's styles/size for fullscreen and mini mode.
/// Put controls in a synchronized owned WPF Window to cross HWND airspace without an SDR bitmap copy.</summary>
public sealed class NativeVideoHost : HwndHost
{
    public IntPtr VideoHandle { get; private set; }
    public event EventHandler? HandleReady;
    public event EventHandler? DoubleClicked;
    public event EventHandler<NativePointerEventArgs>? PointerActivity;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        VideoHandle = CreateWindowEx(0, "STATIC", "DzhiggaTV video", 0x40000000 | 0x10000000 | 0x02000000 | 0x04000000 | 0x00000100,
            0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight), hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (VideoHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        Dispatcher.BeginInvoke(() => { if (VideoHandle != IntPtr.Zero) HandleReady?.Invoke(this, EventArgs.Empty); });
        return new HandleRef(this, VideoHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Dispose MpvPlayer before disposing this host so the render thread no longer uses the HWND.
        DestroyWindow(hwnd.Handle);
        VideoHandle = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x0200: PointerActivity?.Invoke(this, new(NativePointerKind.Move)); break;
            case 0x0201:
                var point = new NativePoint { X = (short)(lParam.ToInt64() & 0xffff), Y = (short)((lParam.ToInt64() >> 16) & 0xffff) };
                ClientToScreen(hwnd, ref point);
                PointerActivity?.Invoke(this, new(NativePointerKind.LeftDown, screenX: point.X, screenY: point.Y));
                break;
            case 0x0204: PointerActivity?.Invoke(this, new(NativePointerKind.RightDown)); break;
            case 0x020A: PointerActivity?.Invoke(this, new(NativePointerKind.Wheel, (short)((wParam.ToInt64() >> 16) & 0xffff))); handled = true; break;
            case 0x0203: DoubleClicked?.Invoke(this, EventArgs.Empty); handled = true; break;
            case 0x0014: handled = true; return new IntPtr(1); // mpv owns painting.
            case 0x0084: handled = true; return new IntPtr(1); // HTCLIENT, no WPF hit-test redirection.
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
}
