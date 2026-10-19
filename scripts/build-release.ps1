param([switch]$SkipTests)
. (Join-Path $PSScriptRoot 'common.ps1')
[xml]$releaseVersionXml = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw
$releaseVersion = [string]$releaseVersionXml.Project.PropertyGroup.Version
$portable = Join-Path $RepositoryRoot ("outputs\DzhiggaTV-portable-win-x64-" + $releaseVersion)
& (Join-Path $PSScriptRoot 'publish.ps1') -OutputDirectory $portable -SkipTests:$SkipTests
& (Join-Path $PSScriptRoot 'build-managed-installer.ps1') -PortableArchive ($portable + '.zip')
Write-Host "Для GitHub Release v${releaseVersion} приложите файлы из корня проекта:"
Write-Host 'DzhiggaTV-Setup-x64.exe'
Write-Host 'DzhiggaTV-Setup-x64.exe.sha256'
