param([string]$PlaylistPath, [ValidateSet('Debug','Release')][string]$Configuration = 'Release')
. (Join-Path $PSScriptRoot 'common.ps1')
$previousFixture = $env:DZHIGGATV_TEST_PLAYLIST
try {
    if ($PlaylistPath) {
        if (!(Test-Path -LiteralPath $PlaylistPath -PathType Leaf)) { throw 'Не найден локальный тестовый плейлист.' }
        $env:DZHIGGATV_TEST_PLAYLIST = [IO.Path]::GetFullPath($PlaylistPath)
    }
    Invoke-DzhiggaDotnet -Arguments @('run', '--project', (Join-Path $RepositoryRoot 'tests\DzhiggaTV.Tests\DzhiggaTV.Tests.csproj'), '-c', $Configuration, '--no-launch-profile')
    Invoke-DzhiggaDotnet -Arguments @('run', '--project', (Join-Path $RepositoryRoot 'tests\DzhiggaTV.Transport.Tests\DzhiggaTV.Transport.Tests.csproj'), '-c', $Configuration, '--no-launch-profile')
    Invoke-DzhiggaDotnet -Arguments @('run', '--project', (Join-Path $RepositoryRoot 'tests\DzhiggaTV.App.Tests\DzhiggaTV.App.Tests.csproj'), '-c', $Configuration, '--no-launch-profile')
    Invoke-DzhiggaDotnet -Arguments @('run', '--project', (Join-Path $RepositoryRoot 'tests\DzhiggaTV.Update.Tests\DzhiggaTV.Update.Tests.csproj'), '-c', $Configuration, '--no-launch-profile')
} finally {
    $env:DZHIGGATV_TEST_PLAYLIST = $previousFixture
}
