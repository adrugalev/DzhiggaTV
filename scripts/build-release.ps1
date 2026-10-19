param([switch]$SkipTests, [string]$BaselineManifest)
. (Join-Path $PSScriptRoot 'common.ps1')
[xml]$releaseVersionXml = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw
$releaseVersion = [string]$releaseVersionXml.Project.PropertyGroup.Version
$portable = Join-Path $RepositoryRoot ("outputs\DzhiggaTV-portable-win-x64-" + $releaseVersion)
& (Join-Path $PSScriptRoot 'publish.ps1') -OutputDirectory $portable -SkipTests:$SkipTests
$updatePackages = Join-Path $RepositoryRoot ("outputs\updates-" + $releaseVersion)
if (!$BaselineManifest) { $BaselineManifest = & (Join-Path $PSScriptRoot 'get-update-baseline.ps1') -Version $releaseVersion }
$packageArguments = @('run', '--project', (Join-Path $RepositoryRoot 'tools\DzhiggaTV.Packaging\DzhiggaTV.Packaging.csproj'), '-c', 'Release', '--', $portable, $releaseVersion, $updatePackages)
if ($BaselineManifest) { $packageArguments += [IO.Path]::GetFullPath($BaselineManifest) }
Invoke-DzhiggaDotnet -Arguments $packageArguments
& (Join-Path $PSScriptRoot 'build-managed-installer.ps1') -PortableArchive ($portable + '.zip')
Write-Host "Манифест и пакеты обновления: $updatePackages"
Write-Host "Для GitHub Release v${releaseVersion} приложите файлы из корня проекта:"
Write-Host 'DzhiggaTV-Setup-x64.exe'
Write-Host 'DzhiggaTV-Setup-x64.exe.sha256'
