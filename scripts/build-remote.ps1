param([switch]$SkipTests)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$remoteWebRoot = Join-Path $PSScriptRoot '..\src\DzhiggaTV.Remote\Web'
$remoteNpm = Get-Command npm.cmd -ErrorAction SilentlyContinue
if (!$remoteNpm) { throw 'Для сборки интерфейса Remote нужен Node.js 24.15 или новее. В готовом установщике Node.js не требуется.' }
Push-Location $remoteWebRoot
try {
    & $remoteNpm.Source ci --no-fund --no-audit
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось восстановить frontend-зависимости Remote.' }
    if (!$SkipTests) {
        & $remoteNpm.Source test
        if ($LASTEXITCODE -ne 0) { throw 'Тесты мобильного интерфейса Remote не прошли.' }
    }
    & $remoteNpm.Source run build
    if ($LASTEXITCODE -ne 0) { throw 'Сборка мобильного интерфейса Remote не прошла.' }
} finally { Pop-Location }
