using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DzhiggaTV.App;

/// <summary>Decode logos at their actual display resolution, including the monitor DPI.</summary>
public sealed class ChannelLogo : Image
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(nameof(Path), typeof(string), typeof(ChannelLogo),
        new FrameworkPropertyMetadata(null, (d, _) => ((ChannelLogo)d).Refresh()));
    public string? Path { get => (string?)GetValue(PathProperty); set => SetValue(PathProperty, value); }
    private readonly record struct Key(string Path, long Modified, int Width, int Height);
    private static readonly Dictionary<Key, LinkedListNode<(Key Key, BitmapSource Image, long Bytes)>> Cache = [];
    private static readonly LinkedList<(Key Key, BitmapSource Image, long Bytes)> Recent = [];
    private const long MaximumBytes = 16 * 1024 * 1024;
    private static long _bytes;
    private Key? _loaded;

    public ChannelLogo()
    {
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => Refresh(); SizeChanged += (_, _) => Refresh();
        Unloaded += (_, _) => { Source = null; _loaded = null; };
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); Refresh(); }

    protected override Size MeasureOverride(Size constraint) => Source != null ? base.MeasureOverride(constraint) :
        new(double.IsFinite(constraint.Width) ? constraint.Width : 0, double.IsFinite(constraint.Height) ? constraint.Height : 0);
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (IsLoaded) Refresh(finalSize);
        return Source != null ? base.ArrangeOverride(finalSize) : finalSize;
    }

    private void Refresh() => Refresh(new(ActualWidth, ActualHeight));
    private void Refresh(Size size)
    {
        if (string.IsNullOrEmpty(Path)) { Source = null; _loaded = null; return; }
        if (!IsLoaded || size.Width <= 0 || size.Height <= 0) return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            // Round up slightly to reuse decodes across small layout changes, never below display pixels.
            int width = Math.Max(16, (int)Math.Ceiling(size.Width * dpi.DpiScaleX / 16) * 16);
            int height = Math.Max(16, (int)Math.Ceiling(size.Height * dpi.DpiScaleY / 16) * 16);
            string fullPath = System.IO.Path.GetFullPath(Path);
            if (_loaded is { } current && current.Path == fullPath && current.Width == width && current.Height == height && Source != null) return;
            var key = new Key(fullPath, File.GetLastWriteTimeUtc(fullPath).Ticks, width, height);
            if (Cache.TryGetValue(key, out var cached))
            { Recent.Remove(cached); Recent.AddFirst(cached); Source = cached.Value.Image; _loaded = key; return; }
            using var stream = File.OpenRead(key.Path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            double scale = Math.Min(1, Math.Min((double)width / frame.PixelWidth, (double)height / frame.PixelHeight));
            stream.Position = 0;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            // Specify one dimension so WIC preserves the original aspect ratio exactly.
            if (scale < 1) image.DecodePixelWidth = Math.Max(1, (int)Math.Ceiling(frame.PixelWidth * scale));
            image.EndInit(); image.Freeze();
            long bytes = ((long)image.PixelWidth * Math.Max(1, image.Format.BitsPerPixel) + 7) / 8 * image.PixelHeight;
            Source = image; _loaded = key;
            if (bytes > MaximumBytes) return;
            while (_bytes + bytes > MaximumBytes && Recent.Last is { } last)
            { _bytes -= last.Value.Bytes; Cache.Remove(last.Value.Key); Recent.RemoveLast(); }
            Cache[key] = Recent.AddFirst((key, image, bytes)); _bytes += bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.IO.FileFormatException or System.Runtime.InteropServices.COMException)
        { Source = null; _loaded = null; }
    }
}
