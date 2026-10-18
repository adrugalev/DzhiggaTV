using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DzhiggaTV.Player;

internal static class BufferBenchmark
{
    internal static async Task Run(MpvPlayer player, string directory)
    {
        if (!File.Exists(Path.Combine(directory, "index.m3u8"))) throw new ArgumentException("Generated HLS fixture missing.");
        using var cancellation = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var clients = new List<Task>();
        var server = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    lock (clients) clients.Add(Serve(client, directory, cancellation.Token));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
        });
        var results = new List<object>();
        try
        {
            foreach (var preset in Enum.GetValues<BufferPreset>())
            {
                player.Stop();
                await Task.Delay(100);
                player.SetBufferPreset(preset);
                var watch = Stopwatch.StartNew();
                await player.PlayAsync($"http://127.0.0.1:{port}/index.m3u8");
                while (player.GetStatistics().PlaybackPositionSeconds is not > .05)
                {
                    if (watch.ElapsedMilliseconds > 12000) throw new TimeoutException("HLS benchmark startup timed out.");
                    await Task.Delay(50);
                }
                double startup = watch.Elapsed.TotalMilliseconds;
                await Task.Delay(1500);
                player.IsPaused = true;
                await Task.Delay(6500);
                var measured = player.GetStatistics();
                if (measured.VideoCodec != "h264" || measured.AudioCodec != "aac") throw new InvalidOperationException("Expected compressed HLS codecs unavailable.");
                results.Add(new { preset = preset.ToString(), startupMs = Math.Round(startup), pausedFillSeconds = measured.BufferedSeconds, pausedFillBytes = measured.BufferedBytes, hardwareDecoder = measured.HardwareDecoder });
                player.IsPaused = false;
            }
            Console.WriteLine(JsonSerializer.Serialize(new { hlsBufferBenchmark = results, segmentSeconds = 2, approximateServerBytesPerSecond = 1048576, note = "Synthetic localhost fixture; these are initial limits, not provider latency guarantees." }));
        }
        finally
        {
            player.Stop();
            cancellation.Cancel();
            listener.Stop();
            await server;
            Task[] pending;
            lock (clients) pending = clients.ToArray();
            await Task.WhenAll(pending);
        }
    }

    private static async Task Serve(TcpClient client, string directory, CancellationToken token)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var header = new byte[8192];
                int length = 0;
                while (length < header.Length)
                {
                    int read = await stream.ReadAsync(header.AsMemory(length, 1), token);
                    if (read == 0) return;
                    length += read;
                    if (length >= 4 && header.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
                }
                string[] request = Encoding.ASCII.GetString(header, 0, length).Split(' ', 3);
                if (request.Length < 2) return;
                string filename = Path.GetFileName(request[1].Split('?')[0]);
                if (filename != "index.m3u8" && (!filename.StartsWith("segment-", StringComparison.Ordinal) || !filename.EndsWith(".ts", StringComparison.Ordinal))) return;
                string path = Path.Combine(directory, filename);
                if (!File.Exists(path)) return;
                byte[] content = await File.ReadAllBytesAsync(path, token);
                string mime = filename.EndsWith(".m3u8", StringComparison.Ordinal) ? "application/vnd.apple.mpegurl" : "video/mp2t";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {mime}\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"), token);
                for (int offset = 0; offset < content.Length; offset += 8192)
                {
                    await stream.WriteAsync(content.AsMemory(offset, Math.Min(8192, content.Length - offset)), token);
                    if (filename.EndsWith(".ts", StringComparison.Ordinal)) await Task.Delay(8, token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }
    }
}
