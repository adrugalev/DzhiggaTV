namespace DzhiggaTV.Remote;

public static class RemoteImage
{
    public static async Task<string?> ContentTypeAsync(string path, CancellationToken token)
    {
        var header = new byte[16]; await using var input = File.OpenRead(path); var n = await input.ReadAsync(header, token);
        if (n >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (n >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255) return "image/jpeg";
        if (n >= 6 && (header.AsSpan(0, 6).SequenceEqual("GIF89a"u8) || header.AsSpan(0, 6).SequenceEqual("GIF87a"u8))) return "image/gif";
        if (n >= 2 && header[0] == 66 && header[1] == 77) return "image/bmp";
        if (n >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}
