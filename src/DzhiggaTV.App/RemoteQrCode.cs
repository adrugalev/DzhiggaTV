using System.Windows.Media.Imaging;
using QRCoder;

namespace DzhiggaTV.App;

public static class RemoteQrCode
{
    public static BitmapSource Create(string url)
    {
        using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data); using var stream = new MemoryStream(png.GetGraphic(10));
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
}
