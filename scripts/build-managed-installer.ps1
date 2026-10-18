param(
    [string]$PortableArchive,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$installerRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PortableArchive) { $PortableArchive = Join-Path $installerRepoRoot 'outputs\DzhiggaTV-portable-win-x64.zip' }
if (-not $OutputPath) { $OutputPath = Join-Path $installerRepoRoot 'outputs\DzhiggaTV-Setup-x64.exe' }
$installerArchivePath = [System.IO.Path]::GetFullPath($PortableArchive)
if (-not (Test-Path -LiteralPath $installerArchivePath -PathType Leaf)) { throw 'Сначала создайте portable ZIP DzhiggaTV.' }
$installerArchiveCheck = [System.IO.Compression.ZipFile]::OpenRead($installerArchivePath)
try {
    if (-not ($installerArchiveCheck.Entries | Where-Object { $_.FullName -eq 'DzhiggaTV.exe' -and $_.Length -gt 0 })) { throw 'Portable ZIP должен содержать DzhiggaTV.exe в корне.' }
} finally { $installerArchiveCheck.Dispose() }
$installerDotnet = Join-Path $installerRepoRoot 'work\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $installerDotnet -PathType Leaf)) { $installerDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$installerPublishPath = Join-Path $installerRepoRoot 'work\managed-installer-publish'
New-Item -ItemType Directory -Path $installerPublishPath -Force | Out-Null
$installerArguments = @('publish', (Join-Path $installerRepoRoot 'src\DzhiggaTV.Installer\DzhiggaTV.Installer.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '-p:DebugSymbols=false', ('-p:PortableArchive=' + $installerArchivePath), '-o', $installerPublishPath, '--nologo')
& $installerDotnet @installerArguments
if ($LASTEXITCODE -ne 0) { throw 'Сборка DzhiggaTV Setup завершилась с ошибкой.' }
$installerFinalPath = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($installerFinalPath)) -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $installerPublishPath 'DzhiggaTV.Setup.exe') -Destination $installerFinalPath -Force
Get-FileHash -LiteralPath $installerFinalPath -Algorithm SHA256 | Format-List Path, Hash
