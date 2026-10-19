. (Join-Path $PSScriptRoot 'common.ps1')
$verifyInfo = & (Join-Path $PSScriptRoot 'get-release-info.ps1')
$verifyInstaller = Join-Path $RepositoryRoot 'DzhiggaTV-Setup-x64.exe'
$verifyHash = (Get-FileHash -LiteralPath $verifyInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-Content -LiteralPath ($verifyInstaller + '.sha256') -Raw).Trim() -cne ($verifyHash + '  DzhiggaTV-Setup-x64.exe')) { throw 'Неверная контрольная сумма установщика.' }
$verifyRoot = Join-Path $RepositoryRoot ('work\ci-extract-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $verifyRoot -Force | Out-Null
$verifyTarget = Join-Path $verifyRoot 'DzhiggaTV'
$verifyReport = Join-Path $verifyRoot 'report.json'
$verifyProcess = Start-Process -FilePath $verifyInstaller -ArgumentList @('--extract-to', ('"' + $verifyTarget + '"'), '--no-shortcuts', '--report', ('"' + $verifyReport + '"')) -WindowStyle Hidden -PassThru
if (!$verifyProcess.WaitForExit(180000)) { $verifyProcess.Kill(); throw 'Проверка распаковки превысила лимит времени.' }
if ($verifyProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $verifyReport)) { throw 'Установщик не прошёл проверку распаковки.' }
if (!(Get-Content -LiteralPath $verifyReport -Raw | ConvertFrom-Json).success) { throw 'Установщик сообщил об ошибке распаковки.' }
$verifyExpected = [version]$verifyInfo.Version
$verifyExpected = [version]::new($verifyExpected.Major, $verifyExpected.Minor, [Math]::Max(0, $verifyExpected.Build), [Math]::Max(0, $verifyExpected.Revision))
foreach ($verifyFile in @($verifyInstaller, (Join-Path $verifyTarget 'DzhiggaTV.exe'))) {
    if ([version]([Diagnostics.FileVersionInfo]::GetVersionInfo($verifyFile).FileVersion) -ne $verifyExpected) { throw 'Версия установщика или вложенного приложения не совпадает с выпуском.' }
}
Write-Host "Проверены установщик, SHA-256 и вложенное приложение версии $($verifyInfo.Version)."
$verifyUpdates = Join-Path $RepositoryRoot ('outputs\updates-' + $verifyInfo.Version)
Invoke-DzhiggaDotnet -Arguments @('run', '--project', (Join-Path $RepositoryRoot 'tools\DzhiggaTV.Packaging\DzhiggaTV.Packaging.csproj'), '-c', 'Release', '--', '--verify', (Join-Path $verifyUpdates 'DzhiggaTV-win-x64.manifest.json'), $verifyTarget, (Join-Path $verifyUpdates 'DzhiggaTV-Update-x64.zip'))
Invoke-DzhiggaDotnet -Arguments @('run', '--project', (Join-Path $RepositoryRoot 'tests\DzhiggaTV.Delta.Tests\DzhiggaTV.Delta.Tests.csproj'), '-c', 'Release', '--', '--published', $verifyTarget)
