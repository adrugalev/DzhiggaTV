param(
    [Parameter(Mandatory = $true)][string]$FfmpegPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$fixtureRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$subtitlePath = Join-Path $fixtureRoot 'test-media.srt'
$videoPath = Join-Path $fixtureRoot 'test-media.mkv'
$hlsRoot = Join-Path $fixtureRoot 'test-hls'
New-Item -ItemType Directory -Path $hlsRoot -Force | Out-Null
@'
1
00:00:01,000 --> 00:00:12,000
DzhiggaTV — тест субтитров

2
00:00:15,000 --> 00:00:30,000
Видео H.264 720p, звук: русский / English

3
00:00:35,000 --> 00:00:58,000
Полный экран и мини-плеер сохраняют воспроизведение
'@ | Set-Content -LiteralPath $subtitlePath -Encoding utf8
& $FfmpegPath -hide_banner -loglevel error -f lavfi -i 'testsrc2=size=1280x720:rate=25' -f lavfi -i 'sine=frequency=440:sample_rate=48000' -f lavfi -i 'sine=frequency=660:sample_rate=48000' -i $subtitlePath -map 0:v -map 1:a -map 2:a -map 3:s -c:v libx264 -preset ultrafast -crf 30 -pix_fmt yuv420p -g 50 -c:a aac -b:a 64k -c:s srt -metadata:s:a:0 language=rus -metadata:s:a:0 'title=Русский · тест 440 Гц' -metadata:s:a:1 language=eng -metadata:s:a:1 'title=English · test 660 Hz' -metadata:s:s:0 language=rus -metadata:s:s:0 'title=Русские субтитры' -t 60 -y $videoPath
if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать MKV fixture.' }
& $FfmpegPath -hide_banner -loglevel error -i $videoPath -map 0:v:0 -map 0:a:0 -c copy -f hls -hls_time 2 -hls_playlist_type vod -hls_segment_filename (Join-Path $hlsRoot 'segment-%03d.ts') -y (Join-Path $hlsRoot 'index.m3u8')
if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать HLS fixture.' }
Write-Output $videoPath
Write-Output (Join-Path $hlsRoot 'index.m3u8')
