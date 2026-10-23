using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DzhiggaTV.App;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPosition { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPosition point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    private static async Task FullscreenChromeChecks(MainWindow main, OverlayWindow overlay, string[] args)
    {
        GetCursorPos(out var original);
        void Move(FrameworkElement element, Point position)
        {
            var screen = element.PointToScreen(position);
            if (!SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y))) throw new InvalidOperationException("Cannot move the isolated test cursor.");
        }
        var controls = (FrameworkElement)overlay.FindName("ControlsPanel");
        var description = (FrameworkElement)overlay.FindName("FullscreenProgrammeInfo");
        var root = (FrameworkElement)overlay.FindName("OverlayRoot");
        try
        {
            main.Activate(); main.ToggleFullscreen(); await Task.Delay(250);
            Move(root, new(root.ActualWidth * .7, root.ActualHeight * .3));
            await Wait(() => controls.IsVisible && description.IsVisible);
            Move(controls, new(controls.ActualWidth / 2, controls.ActualHeight / 2));
            await Wait(() => controls.IsMouseOver); await Task.Delay(1300);
            Check(controls.IsVisible && description.IsVisible, "Fullscreen controls and programme description stay visible beneath a stationary pointer on the controls");
            Move(description, new(description.ActualWidth - 2, 12));
            await Wait(() => description.IsMouseOver); await Task.Delay(1300);
            Check(controls.IsVisible && description.IsVisible, "Hovering the description heading also holds both panels beyond the hide interval");
            var clock = Stopwatch.StartNew();
            Move(description, new(description.ActualWidth + 2, 12));
            await Wait(() => !description.IsMouseOver);
            await Task.Delay(650);
            Check(controls.IsVisible && description.IsVisible, "Leaving a panel by only four DIPs still starts a fresh one-second grace period");
            await Wait(() => !controls.IsVisible && !description.IsVisible, 1400);
            Check(clock.ElapsedMilliseconds is >= 900 and < 1800, "Both fullscreen panels hide together after one second without waiting for the player refresh tick");
            Move(root, new(root.ActualWidth * .7, root.ActualHeight * .3));
            await Wait(() => controls.IsVisible && description.IsVisible);
            Check(controls.IsVisible && description.IsVisible, "Deliberate pointer movement reveals both fullscreen panels again");
            main.RestoreWindow(); await Task.Delay(250);
            int imageIndex = Array.IndexOf(args, "--layout-image");
            if (imageIndex >= 0)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth), (int)Math.Ceiling(main.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(main); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(args[imageIndex + 1]); png.Save(file);
            }
        }
        finally { if (main.IsFullscreen) main.RestoreWindow(); SetCursorPos(original.X, original.Y); }
    }
}
