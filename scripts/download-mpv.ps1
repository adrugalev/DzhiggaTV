. (Join-Path $PSScriptRoot 'common.ps1')
$release = '20261018'
$asset = 'mpv-dev-x86_64-20261018-git-01e2533f31.7z'
$archiveHash = 'd7f5621f42b6e161fe032df914a10bdece28a89df3e57c6c3e16a5a250929b5e'
$dllHash = '82bae3b6bf7bb737afc9b52d590b477983c715f3c63c1a361f9ee06523dc5dc2'
$destination = Join-Path $RepositoryRoot 'native\libmpv-2.dll'
if (Test-Path -LiteralPath $destination) {
    Assert-DzhiggaHash $destination $dllHash
    Write-Host "libmpv: закреплённая сборка $release уже проверена."
    return
}
$downloads = Join-Path $RepositoryRoot 'work\downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
$archive = Join-Path $downloads $asset
if (!(Test-Path -LiteralPath $archive)) {
    $temporary = $archive + '.part'
    Invoke-WebRequest -Uri "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$release/$asset" -OutFile $temporary -TimeoutSec 300
    Assert-DzhiggaHash $temporary $archiveHash
    Move-Item -LiteralPath $temporary -Destination $archive
}
Assert-DzhiggaHash $archive $archiveHash
$extraction = Join-Path $RepositoryRoot ('work\mpv-extract-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $extraction -Force | Out-Null
$tar = Get-Command tar.exe -ErrorAction SilentlyContinue
if (!$tar) { throw 'Нужен встроенный в Windows 11 tar.exe для распаковки .7z.' }
& $tar.Source -xf $archive -C $extraction
if ($LASTEXITCODE -ne 0) { throw 'Не удалось распаковать libmpv.' }
$dll = Join-Path $extraction 'libmpv-2.dll'
Assert-DzhiggaHash $dll $dllHash
New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination $destination
Write-Host "libmpv ${release}: архив и DLL проверены по SHA-256."
