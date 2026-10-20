param(
    [string]$PortableArchive,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$installerRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$projectVersionXml = Get-Content -LiteralPath (Join-Path $installerRepoRoot 'Directory.Build.props') -Raw
$projectVersionText = [string]$projectVersionXml.Project.PropertyGroup.Version
if (-not $PortableArchive) {
    $PortableArchive = Join-Path $installerRepoRoot ("outputs\DzhiggaTV-portable-win-x64-" + $projectVersionText + '.zip')
    if (-not (Test-Path -LiteralPath $PortableArchive)) { $PortableArchive = Join-Path $installerRepoRoot 'outputs\DzhiggaTV-portable-win-x64.zip' }
}
if (-not $OutputPath) { $OutputPath = Join-Path $installerRepoRoot 'outputs\DzhiggaTV-Setup-x64.exe' }
$installerArchivePath = [System.IO.Path]::GetFullPath($PortableArchive)
if (-not (Test-Path -LiteralPath $installerArchivePath -PathType Leaf)) { throw 'Сначала создайте portable ZIP DzhiggaTV.' }
$installerArchiveCheck = [System.IO.Compression.ZipFile]::OpenRead($installerArchivePath)
try {
    if (-not ($installerArchiveCheck.Entries | Where-Object { $_.FullName -eq 'DzhiggaTV.exe' -and $_.Length -gt 0 })) { throw 'Portable ZIP должен содержать DzhiggaTV.exe в корне.' }
    $versionCheckDirectory = Join-Path $installerRepoRoot 'work'
    New-Item -ItemType Directory -Path $versionCheckDirectory -Force | Out-Null
    $versionCheckPath = Join-Path $versionCheckDirectory ('installer-payload-' + [Guid]::NewGuid().ToString('N') + '.exe')
    $versionCheckInput = $installerArchiveCheck.GetEntry('DzhiggaTV.exe').Open()
    $versionCheckOutput = [IO.File]::Create($versionCheckPath)
    try { $versionCheckInput.CopyTo($versionCheckOutput) } finally { $versionCheckOutput.Dispose(); $versionCheckInput.Dispose() }
    $parsedProjectVersion = [version]$projectVersionText
    $projectVersion = [version]::new($parsedProjectVersion.Major, $parsedProjectVersion.Minor, [Math]::Max(0, $parsedProjectVersion.Build), [Math]::Max(0, $parsedProjectVersion.Revision))
    $payloadVersion = [version]([Diagnostics.FileVersionInfo]::GetVersionInfo($versionCheckPath).FileVersion)
    if ($payloadVersion -ne $projectVersion) { throw "Версия portable $payloadVersion не совпадает с версией проекта $projectVersion. Выполните scripts\build-release.ps1." }
} finally { $installerArchiveCheck.Dispose() }
$installerDotnet = Join-Path $installerRepoRoot 'work\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $installerDotnet -PathType Leaf)) { $installerDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$installerPublishPath = Join-Path $installerRepoRoot 'work\managed-installer-publish'
New-Item -ItemType Directory -Path $installerPublishPath -Force | Out-Null
$installerArguments = @('publish', (Join-Path $installerRepoRoot 'src\DzhiggaTV.Installer\DzhiggaTV.Installer.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '-p:DebugSymbols=false', ('-p:PortableArchive=' + $installerArchivePath), '-o', $installerPublishPath, '--nologo')
& $installerDotnet @installerArguments
if ($LASTEXITCODE -ne 0) { throw 'Сборка DzhiggaTV Setup завершилась с ошибкой.' }
$installerFinalPath = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($installerFinalPath)) -Force | Out-Null
function Copy-InstallerAtomically([string]$Source, [string]$Destination) {
    $installerTemporaryCopy = Join-Path ([IO.Path]::GetDirectoryName($Destination)) ('.DzhiggaTV-installer-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    Copy-Item -LiteralPath $Source -Destination $installerTemporaryCopy
    Move-Item -LiteralPath $installerTemporaryCopy -Destination $Destination -Force
}
Copy-InstallerAtomically (Join-Path $installerPublishPath 'DzhiggaTV.Setup.exe') $installerFinalPath
$installerLatestPath = Join-Path $installerRepoRoot 'DzhiggaTV-Setup-x64.exe'
if (-not [string]::Equals($installerFinalPath, $installerLatestPath, [StringComparison]::OrdinalIgnoreCase)) {
    Copy-InstallerAtomically $installerFinalPath $installerLatestPath
}
if ((Get-FileHash -LiteralPath $installerLatestPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $installerFinalPath -Algorithm SHA256).Hash) {
    throw 'Контрольная сумма установщика в корне проекта не совпадает с результатом сборки.'
}
Write-Host "Последний установщик: $installerLatestPath"
Get-FileHash -LiteralPath $installerFinalPath -Algorithm SHA256 | Format-List Path, Hash
$installerHash = (Get-FileHash -LiteralPath $installerFinalPath -Algorithm SHA256).Hash.ToLowerInvariant()
($installerHash + '  ' + [IO.Path]::GetFileName($installerFinalPath)) | Set-Content -LiteralPath ($installerFinalPath + '.sha256') -Encoding ascii
($installerHash + '  DzhiggaTV-Setup-x64.exe') | Set-Content -LiteralPath ($installerLatestPath + '.sha256') -Encoding ascii
