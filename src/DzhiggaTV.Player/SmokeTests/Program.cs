using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DzhiggaTV.Player;

// Native integration harness; no test framework packages and no private playlist are needed.
// Usage: dotnet run --project src/DzhiggaTV.Player/SmokeTests -- <absolute libmpv DLL> <scratch directory> [generated test-media.mkv]
internal static class Program
{
    private static readonly List<string> Passed = [];
    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Passed.Add(description);
    }

    private static async Task WaitUntil(Func<bool> predicate, int milliseconds = 12000)
    {
        var time = Stopwatch.StartNew();
        while (!predicate())
        {
            if (time.ElapsedMilliseconds > milliseconds) throw new TimeoutException("Player state transition timed out.");
            await Task.Delay(50);
        }
    }

    private static string Fixture(string directory, int width, int height)
    {
        var file = Path.Combine(directory, $"test-{width}x{height}.y4m");
        using var output = File.Create(file);
        output.Write(Encoding.ASCII.GetBytes($"YUV4MPEG2 W{width} H{height} F25:1 Ip A1:1 C420jpeg\n"));
        var frame = new byte[width * height * 3 / 2];
        frame.AsSpan(0, width * height).Fill(100);
        frame.AsSpan(width * height).Fill(128);
        for (int i = 0; i < 250; i++) { output.Write("FRAME\n"u8); output.Write(frame); }
        return file;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try { Run(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception exception)
        {
            // Error messages from the wrapper never contain a media URL. Do not print arbitrary exception data.
            Console.WriteLine(JsonSerializer.Serialize(new { passed = Passed, failure = exception.GetType().Name, code = exception is MpvException mpv ? mpv.ErrorCode : (int?)null }));
            return 1;
        }
    }

    private static async Task Run(string[] args)
    {
        if (args.Length is < 2 or > 4) throw new ArgumentException("libmpv DLL and scratch directory required.");
        string native = Path.Combine(AppContext.BaseDirectory, "native");
        Directory.CreateDirectory(native);
        File.Copy(args[0], Path.Combine(native, "libmpv-2.dll"), true);
        Directory.CreateDirectory(args[1]);
        string first = Fixture(args[1], 160, 90), latest = Fixture(args[1], 352, 198);
        IntPtr hwnd = CreateWindowEx(0, "STATIC", "DzhiggaTV native integration test", 0x80000000, -30000, -30000, 640, 360, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Native test window creation failed.");
        try
        {
            using var player = new MpvPlayer();
            player.Volume = 40;
            player.IsMuted = true;
            player.Initialize(hwnd);
            Assert(player.IsInitialized, "native initialization with gpu-next/D3D11 options");
            foreach (var preset in Enum.GetValues<BufferPreset>()) player.SetBufferPreset(preset);
            foreach (var aspect in Enum.GetValues<AspectMode>()) player.SetAspect(aspect);
            foreach (var hdr in Enum.GetValues<HdrMode>()) player.SetHdrMode(hdr);
            player.SetHdrMode(HdrMode.Auto);
            player.SetAspect(AspectMode.Original);
            Assert(Math.Abs(player.Volume - 40) < .01 && player.IsMuted, "volume and mute roundtrip");
            Assert(player.GetAudioDevices().Count > 0, "real audio output device enumeration");
            await player.PlayAsync(first);
            await WaitUntil(() => player.GetStatistics().Width == 160);
            var stats = player.GetStatistics();
            Assert(stats.Height == 90 && stats.FramesPerSecond > 0 && stats.VideoOutput == "gpu-next", "decoded generated video through real D3D11 gpu-next output");
            Assert(player.GetTracks().Count(track => track.Type == "video") == 1 && stats.AudioTrackCount == 0, "real track enumeration without invented audio tracks");
            player.IsPaused = true;
            Assert(player.IsPaused, "pause roundtrip");
            await WaitUntil(() => player.GetTimeline().Seekable);
            Assert(await player.SeekWithinCacheAsync(1), "archive fast path seeks inside a confirmed native cache range");
            await WaitUntil(() => player.GetTimeline().Position is > .9 and < 1.1);
            Assert(!await player.SeekWithinCacheAsync(60) && !await player.SeekWithinCacheAsync(double.NaN) &&
                player.GetTimeline().Position is > .9 and < 1.1,
                "uncached and invalid archive targets are rejected without moving playback");
            player.TogglePause();
            Assert(!player.IsPaused, "resume roundtrip");
            var burst = Enumerable.Range(0, 32).Select(_ => player.PlayAsync(first)).ToList();
            burst.Add(player.PlayAsync(latest));
            foreach (var operation in burst) { try { await operation; } catch (OperationCanceledException) { } }
            await WaitUntil(() => player.GetStatistics().Width == 352);
            await Task.Delay(300);
            Assert(player.GetStatistics().Height == 198, "latest of 33 overlapping load requests wins");
            // A deliberately slow network response must never replace a newer channel after arriving late.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var delayedServer = new CancellationTokenSource();
            var response = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(delayedServer.Token);
                    await Task.Delay(1000, delayedServer.Token);
                    byte[] bytes = await File.ReadAllBytesAsync(first, delayedServer.Token);
                    using var stream = client.GetStream();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), delayedServer.Token);
                    await stream.WriteAsync(bytes, delayedServer.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or IOException or SocketException) { }
            });
            await player.PlayAsync($"http://127.0.0.1:{port}/slow.y4m?private_test_token=never_log_this");
            await Task.Delay(80);
            await player.PlayAsync(latest);
            await WaitUntil(() => player.GetStatistics().Width == 352);
            await Task.Delay(1300);
            Assert(player.GetStatistics().Width == 352, "late stale network response cannot replace current channel");
            delayedServer.Cancel();
            listener.Stop();
            await response;
            if (args.Length >= 3)
            {
                if (!File.Exists(args[2])) throw new ArgumentException("A local generated compressed fixture is required.");
                player.SetPreferredLanguages("rus,ru", "rus,ru");
                await player.PlayAsync(args[2]);
                await WaitUntil(() => player.GetStatistics().VideoCodec == "h264" && player.GetTracks().Count(t => t.Type == "audio") == 2);
                var compressed = player.GetStatistics();
                Assert(compressed.Width == 1280 && compressed.Height == 720 && compressed.AudioCodec == "aac", "H264 720p and AAC decode with accurate codec metadata");
                var tracks = player.GetTracks();
                Assert(tracks.Single(t => t.Type == "audio" && t.Selected).Language is "rus" or "ru", "preferred Russian audio track selected");
                var english = tracks.Single(t => t.Type == "audio" && t.Language is "eng" or "en");
                player.SelectAudio(english.Id);
                await WaitUntil(() => player.GetTracks().Any(t => t.Type == "audio" && t.Id == english.Id && t.Selected));
                Assert(true, "live audio track switch to English");
                var subtitle = tracks.First(t => t.Type == "sub" && !t.External);
                player.SelectSubtitle(subtitle.Id);
                await WaitUntil(() => player.GetTracks().Any(t => t.Type == "sub" && t.Selected));
                player.SelectSubtitle(null);
                await WaitUntil(() => player.GetTracks().All(t => t.Type != "sub" || !t.Selected));
                Assert(true, "embedded subtitle enable and disable");
                Console.WriteLine(JsonSerializer.Serialize(new { compressedVideo = new { compressed.VideoCodec, compressed.AudioCodec, compressed.VideoDecoder, compressed.HardwareDecoder, compressed.SourceTransfer, compressed.TargetTransfer, compressed.VideoOutput } }));
            }
            if (args.Length >= 3)
            {
                await player.PlayArchiveAsync(args[2], 30);
                await WaitUntil(() => player.State == PlaybackState.Playing && player.GetTimeline().Seekable);
                player.IsPaused = true;
                Assert(await player.SeekAsync(15), "bounded archive supports a real native absolute seek");
                await WaitUntil(() => player.GetTimeline().Position is > 14 and < 16);
                Assert(player.IsPaused && Math.Abs(player.Volume - 40) < .01 && player.IsMuted, "archive seek preserves pause, volume and mute");
                await player.PlayArchiveAsync(args[2], 2);
                await WaitUntil(() => player.State == PlaybackState.Idle, 8000);
                await Task.Delay(1200);
                Assert(player.State == PlaybackState.Idle, "finite archive EOF stays completed without reconnecting");
                await player.PlayAsync(args[2]);
                await WaitUntil(() => player.State == PlaybackState.Playing);
                await Task.Delay(2400);
                Assert(player.State == PlaybackState.Playing, "returning to live clears archive start and end options");
            }
            await VerifyStallRecovery(player, first, latest);
            using (var errors = new TcpListener(IPAddress.Loopback, 0))
            using (var errorCancellation = new CancellationTokenSource())
            {
                errors.Start();
                int errorPort = ((IPEndPoint)errors.LocalEndpoint).Port;
                int requests = 0;
                var errorServer = Task.Run(async () =>
                {
                    try
                    {
                        while (!errorCancellation.IsCancellationRequested)
                        {
                            using var client = await errors.AcceptTcpClientAsync(errorCancellation.Token);
                            Interlocked.Increment(ref requests);
                            using var stream = client.GetStream();
                            await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), errorCancellation.Token);
                        }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
                });
                await player.PlayAsync($"http://127.0.0.1:{errorPort}/missing.m3u8?private_test_token=never_log_this");
                await WaitUntil(() => player.State == PlaybackState.Error, 16000);
                Assert(Volatile.Read(ref requests) == 4, "network failure retries exactly three times then stops");
                await player.PlayAsync($"http://127.0.0.1:{errorPort}/missing-again.m3u8?private_test_token=never_log_this");
                await WaitUntil(() => player.State == PlaybackState.Reconnecting);
                player.Stop();
                await Task.Delay(1600);
                Assert(Volatile.Read(ref requests) == 5 && player.State == PlaybackState.Idle, "stop cancels pending reconnect and prevents late retry");
                errorCancellation.Cancel();
                errors.Stop();
                await errorServer;
            }
            if (args.Length == 4)
            {
                await BufferBenchmark.Run(player, args[3]);
                Assert(true, "real HTTP HLS playback in all three buffer presets");
            }
            player.Stop();
            await WaitUntil(() => player.GetStatistics().Width == null);
            Assert(player.State == PlaybackState.Idle, "stop releases loaded video and returns idle");
            var disposal = Stopwatch.StartNew();
            player.Dispose();
            player.Dispose();
            Assert(!player.IsInitialized && disposal.ElapsedMilliseconds < 10000, "idempotent bounded native disposal");
            Console.WriteLine(JsonSerializer.Serialize(new { passed = Passed, count = Passed.Count, video = new { stats.Width, stats.Height, stats.VideoOutput, stats.HardwareDecoder, stats.MpvVersion }, hdrHardwareVerified = false }));
        }
        finally { DestroyWindow(hwnd); }
    }

    private static async Task VerifyStallRecovery(MpvPlayer player, string first, string latest)
    {
        byte[] bytes = await File.ReadAllBytesAsync(first);
        int header = Array.IndexOf(bytes, (byte)'\n') + 1;
        int initial = header + (160 * 90 * 3 / 2 + 6) * 3;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var stop = new CancellationTokenSource();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port, healthy = 0, requests = 0, reconnects = 0;
        bool pauseOnReconnect = false;
        var workers = new System.Collections.Concurrent.ConcurrentBag<Task>();
        async Task Serve(TcpClient client)
        {
            using (client)
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                int rangeStart = 0; bool ranged = false;
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                {
                    if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase) && int.TryParse(line[13..].Split('-')[0], out int start))
                    { rangeStart = Math.Clamp(start, 0, bytes.Length - 1); ranged = true; }
                }
                Interlocked.Increment(ref requests);
                bool ready = Volatile.Read(ref healthy) != 0;
                if (!ready) { ranged = false; rangeStart = 0; }
                string range = ranged ? $"Content-Range: bytes {rangeStart}-{bytes.Length - 1}/{bytes.Length}\r\n" : "";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(ranged ? "206 Partial Content" : "200 OK")}\r\nContent-Type: application/octet-stream\r\nContent-Length: {bytes.Length - rangeStart}\r\n{(ready ? "Accept-Ranges: bytes\r\n" : "")}{range}Connection: close\r\n\r\n"), stop.Token);
                if (ready) { await stream.WriteAsync(bytes.AsMemory(rangeStart), stop.Token); return; }
                await stream.WriteAsync(bytes.AsMemory(0, initial), stop.Token);
                // Keep the TCP connection alive without completing another video frame.
                // This must be detected from playback progress, not just the HTTP timeout.
                int offset = initial;
                while (!stop.IsCancellationRequested)
                { await Task.Delay(1800, stop.Token); await stream.WriteAsync(bytes.AsMemory(offset++, 1), stop.Token); }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        var serving = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) workers.Add(Serve(await listener.AcceptTcpClientAsync(stop.Token))); }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
        });
        void Changed(object? sender, PlaybackStateChangedEventArgs e)
        {
            if (e.State != PlaybackState.Reconnecting) return;
            Interlocked.Increment(ref reconnects); Volatile.Write(ref healthy, 1);
            if (pauseOnReconnect) player.IsPaused = true;
        }
        player.StateChanged += Changed;
        string url = $"http://127.0.0.1:{port}/stall.y4m?private_test_token=never_log_this";
        try
        {
            await player.PlayAsync(url);
            await WaitUntil(() => player.State == PlaybackState.Stalled, 11000);
            Assert(true, "a live connection with no advancing frames is reported stalled before a network error");
            await WaitUntil(() => Volatile.Read(ref reconnects) > 0 && player.State == PlaybackState.Playing && player.GetTimeline().Position > .3, 23000);
            Assert(Volatile.Read(ref requests) >= 2 && player.Volume == 40 && player.IsMuted, "watchdog reopens a trickling frozen stream and restores native playback without changing sound");
            player.IsPaused = true;
            int beforePause = Volatile.Read(ref reconnects);
            await Task.Delay(17000);
            Assert(player.IsPaused && player.State == PlaybackState.Paused && Volatile.Read(ref reconnects) == beforePause,
                "an intentionally paused network stream never triggers the stall watchdog");
            Volatile.Write(ref healthy, 0);
            await player.PlayAsync(url);
            await WaitUntil(() => player.State == PlaybackState.Stalled, 11000);
            await player.PlayAsync(latest); await WaitUntil(() => player.GetStatistics().Width == 352);
            player.IsPaused = true; int beforeSwitch = Volatile.Read(ref requests);
            await Task.Delay(16000);
            Assert(player.GetStatistics().Width == 352 && player.IsPaused && Volatile.Read(ref requests) == beforeSwitch,
                "changing channel cancels stale watchdog probes and prevents a delayed old-stream reconnect");
            Volatile.Write(ref healthy, 1); pauseOnReconnect = true;
            double resumed = 0; int archiveReopens = 0;
            await player.PlayArchiveAsync(url, 60, reconnectSource: position =>
            {
                Volatile.Write(ref resumed, position); Interlocked.Increment(ref archiveReopens);
                return new(url + "&resume=" + position.ToString(System.Globalization.CultureInfo.InvariantCulture), 60 - position);
            });
            await WaitUntil(() => Volatile.Read(ref archiveReopens) > 0 && player.State == PlaybackState.Paused && player.GetTimeline().Position >= Volatile.Read(ref resumed), 22000);
            Assert(resumed > 5 && player.GetTimeline().Position < resumed + 3 && player.IsMuted,
                "premature archive EOF rebuilds the provider URL at the saved position and preserves pause");
            Assert(!await player.SeekWithinCacheAsync(resumed - 2), "a recovered archive never seeks into data before its new native window");
            Assert(await player.SeekAsync(resumed + 1), "recovered archive seeks translate the original timeline to the new native window");
            await WaitUntil(() => player.GetTimeline().Position >= resumed + .9);
            player.Stop();
        }
        finally
        {
            player.StateChanged -= Changed; stop.Cancel(); listener.Stop(); await serving; await Task.WhenAll(workers);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
}
