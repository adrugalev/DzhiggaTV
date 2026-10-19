Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ciTestRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ciTestWork = Join-Path $ciTestRoot ('work\ci-release-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $ciTestWork -Force | Out-Null
$ciTestInstaller = Join-Path $ciTestWork 'DzhiggaTV-Setup-x64.exe'
[IO.File]::WriteAllText($ciTestInstaller, 'MZ mocked CI release payload; not an executable')
$ciTestHash = (Get-FileHash -LiteralPath $ciTestInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
$ciTestChecksum = $ciTestInstaller + '.sha256'
($ciTestHash + '  DzhiggaTV-Setup-x64.exe') | Set-Content -LiteralPath $ciTestChecksum -Encoding ascii
$ciTestEnvNames = @('GH_TOKEN', 'GITHUB_REPOSITORY', 'GITHUB_SHA', 'GITHUB_OUTPUT', 'GITHUB_STEP_SUMMARY')
$ciTestPrevious = @{}
foreach ($ciTestName in $ciTestEnvNames) { $ciTestPrevious[$ciTestName] = [Environment]::GetEnvironmentVariable($ciTestName) }
$env:GH_TOKEN = 'mock-token-no-network'
$env:GITHUB_REPOSITORY = 'adrugalev/DzhiggaTV'
$env:GITHUB_SHA = 'a' * 40
$env:GITHUB_OUTPUT = Join-Path $ciTestWork 'outputs.txt'
$env:GITHUB_STEP_SUMMARY = Join-Path $ciTestWork 'summary.md'
$script:ciTestPassed = 0
function Assert-CiRelease([bool]$Condition, [string]$Name) { if (!$Condition) { throw $Name }; $script:ciTestPassed++; Write-Host "PASS $Name" }
function Reset-CiReleaseMock {
    $script:ciMock = @{ Existing = $null; Latest = $null; Published = $false; Created = $false; FailChecksumUpload = $false; Deny = $false; Stale = $false; Assets = [Collections.Generic.List[object]]::new(); Mutations = [Collections.Generic.List[string]]::new() }
    [IO.File]::WriteAllText($env:GITHUB_OUTPUT, '')
}
function Invoke-RestMethod {
    param([uri]$Uri, [string]$Method = 'Get', [object]$Body, [object]$Headers, [string]$ContentType, [string]$InFile, [int]$TimeoutSec)
    # This mock never calls the network, including on unexpected requests.
    if ($script:ciMock.Deny) { throw [UnauthorizedAccessException]::new('Simulated permission failure') }
    if ($Uri.Host -eq 'uploads.github.com') {
        $script:ciMock.Mutations.Add('upload')
        if ($script:ciMock.FailChecksumUpload -and $InFile.EndsWith('.sha256')) { throw [IO.IOException]::new('Simulated interrupted upload') }
        $asset = [PSCustomObject]@{ id = 100 + $script:ciMock.Assets.Count; name = [IO.Path]::GetFileName($InFile); state = 'uploaded'; size = (Get-Item -LiteralPath $InFile).Length; digest = 'sha256:' + (Get-FileHash -LiteralPath $InFile -Algorithm SHA256).Hash.ToLowerInvariant() }
        $script:ciMock.Assets.Add($asset); return $asset
    }
    if ($Uri.Host -ne 'api.github.com') { throw 'Unexpected network host in CI test.' }
    $path = $Uri.AbsolutePath -replace '^/repos/adrugalev/DzhiggaTV/', ''
    if ($Method -eq 'Get') {
        if ($path -like 'releases/tags/*' -and $script:ciMock.Existing) { return $script:ciMock.Existing }
        if ($path -eq 'releases/latest' -and $script:ciMock.Latest) { return $script:ciMock.Latest }
        if ($path -eq 'git/ref/heads/main') { return [PSCustomObject]@{ object = @{ sha = $(if ($script:ciMock.Stale) { 'b' * 40 } else { $env:GITHUB_SHA }) } } }
        if ($path -eq 'releases/42') { return [PSCustomObject]@{ id = 42; draft = !$script:ciMock.Published; assets = $script:ciMock.Assets.ToArray() } }
        if ($path -like 'releases/tags/*' -or $path -eq 'releases/latest' -or $path -like 'git/ref/tags/*') {
            throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('Simulated 404', [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::NotFound))
        }
    }
    if ($Method -eq 'Post' -and $path -eq 'releases') {
        $draft = ConvertFrom-Json -InputObject $Body
        Assert-CiRelease ($draft.draft -and !$draft.prerelease -and $draft.target_commitish -eq $env:GITHUB_SHA) 'create only a draft for the exact checked-out commit'
        $script:ciMock.Created = $true; $script:ciMock.Mutations.Add('create')
        return [PSCustomObject]@{ id = 42; draft = $true; assets = @(); upload_url = 'https://uploads.github.com/repos/adrugalev/DzhiggaTV/releases/42/assets{?name,label}' }
    }
    if ($Method -eq 'Patch' -and $path -eq 'releases/42') {
        $patch = ConvertFrom-Json -InputObject $Body
        Assert-CiRelease ($script:ciMock.Assets.Count -eq 2 -and !$patch.draft -and $patch.make_latest -eq 'true') 'publish only after both verified uploads'
        $script:ciMock.Published = $true; $script:ciMock.Mutations.Add('publish'); return [PSCustomObject]@{ id = 42 }
    }
    throw "Unexpected mocked request: $Method $path"
}
try {
    Reset-CiReleaseMock
    . (Join-Path $PSScriptRoot 'ci-release.ps1') -Mode Plan -PackageDirectory $ciTestWork
    Assert-CiRelease ((Get-Content -LiteralPath $env:GITHUB_OUTPUT -Raw) -match 'build=true') 'first source upload schedules a release'
    Assert-CiRelease ($script:ciMock.Mutations.Count -eq 0) 'planning never publishes or uploads'
    Reset-CiReleaseMock
    $script:ciMock.Existing = [PSCustomObject]@{ draft = $false }
    . (Join-Path $PSScriptRoot 'ci-release.ps1') -Mode Publish -PackageDirectory $ciTestWork
    Assert-CiRelease ($script:ciMock.Mutations.Count -eq 0) 'published versions are never overwritten'
    Reset-CiReleaseMock
    $script:ciMock.Stale = $true
    . (Join-Path $PSScriptRoot 'ci-release.ps1') -Mode Plan -PackageDirectory $ciTestWork
    Assert-CiRelease ((Get-Content -LiteralPath $env:GITHUB_OUTPUT -Raw) -match 'build=false') 'outdated source uploads do not publish'
    Reset-CiReleaseMock
    $script:ciMock.Deny = $true
    $failed = $false
    try { . (Join-Path $PSScriptRoot 'ci-release.ps1') -Mode Plan -PackageDirectory $ciTestWork } catch { $failed = $true }
    Assert-CiRelease $failed 'permission and network errors do not masquerade as an absent release'
    Reset-CiReleaseMock
    . (Join-Path $PSScriptRoot 'ci-release.ps1') -Mode Publish -PackageDirectory $ciTestWork
    Assert-CiRelease $script:ciMock.Published 'complete uploads publish the stable release'
    Reset-CiReleaseMock
    $script:ciMock.FailChecksumUpload = $true
    $failed = $false
    try { . (Join-Path $PSScriptRoot 'ci-release.ps1') -Mode Publish -PackageDirectory $ciTestWork } catch { $failed = $true }
    Assert-CiRelease ($failed -and $script:ciMock.Created -and !$script:ciMock.Published) 'interrupted uploads leave an unpublished draft'
    Reset-CiReleaseMock
    Set-Content -LiteralPath $ciTestChecksum -Value ('0' * 64 + '  DzhiggaTV-Setup-x64.exe') -Encoding ascii
    $failed = $false
    try { . (Join-Path $PSScriptRoot 'ci-release.ps1') -Mode Publish -PackageDirectory $ciTestWork } catch { $failed = $true }
    Assert-CiRelease ($failed -and $script:ciMock.Mutations.Count -eq 0) 'invalid checksum blocks all release mutations'
    Write-Host "$script:ciTestPassed CI release checks passed. No network requests made."
}
finally {
    foreach ($ciTestName in $ciTestEnvNames) { [Environment]::SetEnvironmentVariable($ciTestName, $ciTestPrevious[$ciTestName]) }
}
