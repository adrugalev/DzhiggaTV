param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$SkipNative)
. (Join-Path $PSScriptRoot 'common.ps1')
if (!$SkipNative) { & (Join-Path $PSScriptRoot 'download-mpv.ps1') }
Invoke-DzhiggaDotnet -Arguments @('build', (Join-Path $RepositoryRoot 'DzhiggaTV.sln'), '-c', $Configuration, '--nologo')
