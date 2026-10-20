param([Parameter(Mandatory)][string]$InstallerPath, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$installer = [IO.Path]::GetFullPath($InstallerPath)
$scratch = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $scratch) { throw 'Use a new, empty benchmark directory.' }
New-Item -ItemType Directory -Path $scratch | Out-Null
$target = Join-Path $scratch 'DzhiggaTV'
$report = Join-Path $scratch 'extract.json'
$timer = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $installer -ArgumentList @('--extract-to', ('"' + $target + '"'), '--no-shortcuts', '--report', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
$privatePeak = 0L; $residentPeak = 0L
while (!$process.HasExited) {
    try { $process.Refresh(); $privatePeak = [Math]::Max($privatePeak, $process.PrivateMemorySize64); $residentPeak = [Math]::Max($residentPeak, $process.WorkingSet64) } catch { }
    if ($timer.Elapsed.TotalSeconds -gt 180) { $process.Kill(); throw 'Installer benchmark timed out.' }
    Start-Sleep -Milliseconds 50
}
$seconds = $timer.Elapsed.TotalSeconds
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report) -or !(Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).success) { throw 'Installer extraction failed.' }
$result = [ordered]@{ InstallerBytes=(Get-Item -LiteralPath $installer).Length; Seconds=$seconds; PeakPrivateBytes=$privatePeak; PeakWorkingSetBytes=$residentPeak; ExtractedBytes=(Get-ChildItem -LiteralPath $target -File -Recurse | Measure-Object Length -Sum).Sum }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $scratch 'measurements.json') -Encoding utf8
$result | ConvertTo-Json -Compress
