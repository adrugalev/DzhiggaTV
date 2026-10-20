# Opt-in performance measurements

Run on Windows with a visible desktop and no other DzhiggaTV test windows. The harness creates a **new isolated profile**, 3227 local channels, 96 distinct 512px PNG logos and a cached guide. It does not read or change the user's profile or contact IPTV providers. The app's normal update check remains enabled. Never run builds, media encoding or another benchmark at the same time as the sampling phase.

Generate a repeatable H.264/AAC fixture using ffmpeg:

```powershell
ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=30 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 240 -c:v libx264 -preset ultrafast -crf 23 -pix_fmt yuv420p -c:a aac work/performance-1080p.mkv
work/dotnet/dotnet.exe build tests/DzhiggaTV.Performance -c Release -o work/performance-build
work/dotnet/dotnet.exe work/performance-build/DzhiggaTV.Performance.dll --fixture work/performance-1080p.mkv --data-dir work/new-performance-profile --json work/performance.json --seconds 25
```

Fixture generation runs in a separate child process before the measured application starts, so its temporary PNG/database allocations do not inflate the player's heap. Each phase warms for five seconds, then samples process private bytes and working set once per second. CPU is total process CPU time divided by wall time and logical processor count, matching the system-wide 0–100% convention. Managed allocations are a separate diagnostic, **not** the process's RAM usage. No forced GC or working-set trimming changes the measurements.

Phases: idle catalogue; one playing stream; four visible streams plus the continuously playing muted main stream; the main player after closing multiscreen and scrolling through 96 channels. Each active stream records resolution, actual hardware decoder, video output, frame-drop delta and playback-clock advancement. The harness never changes decode quality, cache presets, volume beyond the test profile, output resolution or frame rate.

Repeat at least twice for each binary, alternating before/after builds. Keep copies of the baseline binaries, not just the benchmark DLL: dependencies must match their assembly versions. Keep the source video, OS display/DPI, GPU driver and window layout unchanged. Results do not predict arbitrary IPTV bitrates, HEVC, 4K/HDR or network buffering.

A separate parser benchmark streams 100,000 XMLTV records, checks their count and content checksum, and reports three iterations of elapsed time and total managed allocations. Its file is generated before the measurement; no SQLite writes or video encoding occur during it:

```powershell
work/dotnet/dotnet.exe work/performance-build/DzhiggaTV.Performance.dll --xml-benchmark work/performance-xmltv.xml
```

These measurements intentionally stay outside the ordinary test script: they require native video hardware and take several minutes.
