param([string]$OutputDirectory, [switch]$SkipTests, [switch]$NoZip)
. (Join-Path $PSScriptRoot 'common.ps1')
if (!$OutputDirectory) { $OutputDirectory = Join-Path $RepositoryRoot 'outputs\DzhiggaTV-portable-win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Каталог выпуска уже существует. Укажите новый -OutputDirectory, чтобы сохранить предыдущую сборку.' }
& (Join-Path $PSScriptRoot 'download-mpv.ps1')
if (!$SkipTests) { & (Join-Path $PSScriptRoot 'test.ps1') }
$stage = Join-Path $RepositoryRoot ('work\publish-' + [Guid]::NewGuid().ToString('N'))
Invoke-DzhiggaDotnet -Arguments @('publish', (Join-Path $RepositoryRoot 'src\DzhiggaTV.App\DzhiggaTV.App.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-o', $stage, '--nologo')
$runnerStage = Join-Path $RepositoryRoot ('work\update-runner-' + [Guid]::NewGuid().ToString('N'))
# A payload-free installer shares the player's self-contained runtime. Only four tiny host files are added.
Invoke-DzhiggaDotnet -Arguments @('publish', (Join-Path $RepositoryRoot 'src\DzhiggaTV.Installer\DzhiggaTV.Installer.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PortableArchive=', '-p:DebugType=None', '-o', $runnerStage, '--nologo')
foreach ($runnerFile in @('DzhiggaTV.Setup.exe', 'DzhiggaTV.Setup.dll', 'DzhiggaTV.Setup.deps.json', 'DzhiggaTV.Setup.runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $runnerStage $runnerFile) -Destination $stage
}
if ((Get-Item -LiteralPath (Join-Path $stage 'DzhiggaTV.Setup.dll')).Length -gt 2MB) { throw 'В локальный updater попал полный установочный архив.' }
foreach ($required in @('DzhiggaTV.exe', 'DzhiggaTV.dll', 'coreclr.dll', 'native\libmpv-2.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $stage $required))) { throw "Выпуск неполон: отсутствует $required." }
}
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'README.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'docs') -Destination (Join-Path $stage 'docs') -Recurse
$manifest = [ordered]@{
    application = 'DzhiggaTV'; architecture = 'win-x64'; selfContained = $true
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    sdk = (& (Get-DzhiggaDotnet) --version | Out-String).Trim()
    mpvRelease = '20261018'; mpvCommit = '01e2533f31'
    mpvSource = 'https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261018'
    mpvDllSha256 = (Get-FileHash -LiteralPath (Join-Path $stage 'native\libmpv-2.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'build-manifest.json') -Encoding utf8
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputDirectory) -Force | Out-Null
# Validate the resolved staging path before moving a directory tree.
$resolvedWork = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'work')) + [IO.Path]::DirectorySeparatorChar
$resolvedStage = [IO.Path]::GetFullPath($stage)
if (!$resolvedStage.StartsWith($resolvedWork, [StringComparison]::OrdinalIgnoreCase)) { throw 'Staging path is outside this workspace.' }
Move-Item -LiteralPath $stage -Destination $OutputDirectory
if (!$NoZip) {
    $zip = $OutputDirectory + '.zip'
    if (Test-Path -LiteralPath $zip) { throw 'ZIP выпуска уже существует.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($OutputDirectory, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zip) | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
    Write-Host "Portable ZIP: $zip"
}
Write-Host "DzhiggaTV.exe: $(Join-Path $OutputDirectory 'DzhiggaTV.exe')"
