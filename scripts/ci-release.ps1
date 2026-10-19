param(
    [Parameter(Mandatory)][ValidateSet('Plan', 'Publish')][string]$Mode,
    [string]$PackageDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ciReleaseRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$PackageDirectory) { $PackageDirectory = $ciReleaseRoot }
if ($env:GITHUB_REPOSITORY -ne 'adrugalev/DzhiggaTV' -or $env:GITHUB_SHA -notmatch '^[0-9a-f]{40}$') { throw 'Публикация разрешена только для коммита репозитория adrugalev/DzhiggaTV.' }
if (!$env:GH_TOKEN) { throw 'Отсутствует токен GitHub Actions.' }
$ciReleaseRepository = $env:GITHUB_REPOSITORY
$ciReleaseInfo = & (Join-Path $PSScriptRoot 'get-release-info.ps1')
$ciReleaseHeaders = @{ Authorization = 'Bearer ' + $env:GH_TOKEN; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'DzhiggaTV-Release' }

function Invoke-ReleaseApi {
    param([string]$Path, [string]$Method = 'Get', [object]$Body)
    $arguments = @{ Uri = "https://api.github.com/repos/$ciReleaseRepository/$Path"; Headers = $ciReleaseHeaders; Method = $Method; TimeoutSec = 60 }
    if ($null -ne $Body) { $arguments.Body = ConvertTo-Json -InputObject $Body -Depth 12 -Compress; $arguments.ContentType = 'application/json; charset=utf-8' }
    Invoke-RestMethod @arguments
}
function Get-ReleaseOrNull {
    param([string]$Path)
    try { Invoke-ReleaseApi -Path $Path }
    catch { if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { return $null }; throw }
}
function Write-ReleaseSummary {
    param([string]$Text)
    Write-Host $Text
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $Text -Encoding utf8 }
}
function Get-ReleasePlan {
    $existing = Get-ReleaseOrNull -Path ('releases/tags/' + $ciReleaseInfo.Tag)
    if ($existing -and !$existing.draft) { return [PSCustomObject]@{ Build = $false; Reason = "Выпуск $($ciReleaseInfo.Tag) уже опубликован. Для новых изменений увеличьте номер версии."; Existing = $existing } }
    $head = Invoke-ReleaseApi -Path 'git/ref/heads/main'
    if ($head.object.sha -ne $env:GITHUB_SHA) { return [PSCustomObject]@{ Build = $false; Reason = 'В main уже есть более новый коммит; его запуск подготовит выпуск.'; Existing = $existing } }
    $latest = Get-ReleaseOrNull -Path 'releases/latest'
    if ($latest -and $latest.tag_name -match '^v?(\d+\.\d+\.\d+(?:\.\d+)?)$' -and [version]$Matches[1] -gt [version]$ciReleaseInfo.Version) {
        return [PSCustomObject]@{ Build = $false; Reason = 'Более новая версия уже опубликована. Предыдущая не станет последней.'; Existing = $existing }
    }
    $tag = Get-ReleaseOrNull -Path ('git/ref/tags/' + $ciReleaseInfo.Tag)
    if ($tag) {
        $tagObject = $tag.object
        for ($depth = 0; $tagObject.type -eq 'tag' -and $depth -lt 5; $depth++) { $tagObject = (Invoke-ReleaseApi -Path ('git/tags/' + $tagObject.sha)).object }
        if ($tagObject.type -ne 'commit' -or $tagObject.sha -ne $env:GITHUB_SHA) { throw "Тег $($ciReleaseInfo.Tag) уже указывает на другой коммит. Увеличьте номер версии или перезапустите исходную сборку." }
    }
    [PSCustomObject]@{ Build = $true; Reason = "Готовится выпуск $($ciReleaseInfo.Tag)."; Existing = $existing }
}

$ciReleasePlan = Get-ReleasePlan
if ($Mode -eq 'Plan') {
    if ($env:GITHUB_OUTPUT) {
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value ('build=' + $ciReleasePlan.Build.ToString().ToLowerInvariant()) -Encoding utf8
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value ('tag=' + $ciReleaseInfo.Tag) -Encoding utf8
    }
    Write-ReleaseSummary $ciReleasePlan.Reason
    return
}
if (!$ciReleasePlan.Build) { Write-ReleaseSummary $ciReleasePlan.Reason; return }

$ciReleaseInstaller = Join-Path $PackageDirectory 'DzhiggaTV-Setup-x64.exe'
$ciReleaseChecksum = $ciReleaseInstaller + '.sha256'
if (!(Test-Path -LiteralPath $ciReleaseInstaller -PathType Leaf) -or !(Test-Path -LiteralPath $ciReleaseChecksum -PathType Leaf)) { throw 'Для публикации нужны установщик и его SHA-256.' }
$ciReleaseHash = (Get-FileHash -LiteralPath $ciReleaseInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-Content -LiteralPath $ciReleaseChecksum -Raw).Trim() -cne ($ciReleaseHash + '  DzhiggaTV-Setup-x64.exe')) { throw 'Контрольная сумма установщика не совпадает. Выпуск не опубликован.' }

$ciReleaseBody = Get-Content -LiteralPath $ciReleaseInfo.NotesPath -Raw
$ciReleaseDraft = $ciReleasePlan.Existing
if (!$ciReleaseDraft) {
    $ciReleaseDraft = Invoke-ReleaseApi -Path 'releases' -Method Post -Body @{ tag_name = $ciReleaseInfo.Tag; target_commitish = $env:GITHUB_SHA; name = 'DzhiggaTV ' + $ciReleaseInfo.Version; body = $ciReleaseBody; draft = $true; prerelease = $false }
}
if (!$ciReleaseDraft.draft) { throw 'Опубликованный выпуск не будет перезаписан.' }
foreach ($ciReleaseFile in @($ciReleaseInstaller, $ciReleaseChecksum)) {
    $ciAssetName = [IO.Path]::GetFileName($ciReleaseFile)
    foreach ($ciExistingAsset in @($ciReleaseDraft.assets)) {
        if ($ciExistingAsset.name -eq $ciAssetName) { $null = Invoke-ReleaseApi -Path ('releases/assets/' + $ciExistingAsset.id) -Method Delete }
    }
    $ciUploadUri = [uri](($ciReleaseDraft.upload_url -replace '\{.*$', '') + '?name=' + [uri]::EscapeDataString($ciAssetName))
    if ($ciUploadUri.Scheme -ne 'https' -or $ciUploadUri.Host -ne 'uploads.github.com' -or $ciUploadUri.AbsolutePath -ne "/repos/$ciReleaseRepository/releases/$($ciReleaseDraft.id)/assets") { throw 'GitHub вернул неверный адрес загрузки вложений.' }
    $null = Invoke-RestMethod -Uri $ciUploadUri -Method Post -Headers $ciReleaseHeaders -ContentType 'application/octet-stream' -InFile $ciReleaseFile -TimeoutSec 600
}
$ciUploadedRelease = Invoke-ReleaseApi -Path ('releases/' + $ciReleaseDraft.id)
if (!$ciUploadedRelease.draft) { throw 'Выпуск был опубликован до завершения загрузки.' }
foreach ($ciReleaseFile in @($ciReleaseInstaller, $ciReleaseChecksum)) {
    $ciAssetName = [IO.Path]::GetFileName($ciReleaseFile)
    $ciUploadedAssets = @($ciUploadedRelease.assets | Where-Object { $_.name -eq $ciAssetName -and $_.state -eq 'uploaded' })
    if ($ciUploadedAssets.Count -ne 1 -or $ciUploadedAssets[0].size -ne (Get-Item -LiteralPath $ciReleaseFile).Length) { throw "Не подтверждена полная загрузка $ciAssetName. Выпуск остался черновиком." }
    $ciExpectedDigest = 'sha256:' + (Get-FileHash -LiteralPath $ciReleaseFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ciUploadedAssets[0].digest -and $ciUploadedAssets[0].digest -cne $ciExpectedDigest) { throw "GitHub вернул неверную SHA-256 для $ciAssetName. Выпуск остался черновиком." }
}
$null = Invoke-ReleaseApi -Path ('releases/' + $ciReleaseDraft.id) -Method Patch -Body @{ draft = $false; prerelease = $false; make_latest = 'true'; name = 'DzhiggaTV ' + $ciReleaseInfo.Version; body = $ciReleaseBody }
Write-ReleaseSummary "Опубликован [DzhiggaTV $($ciReleaseInfo.Version)](https://github.com/$ciReleaseRepository/releases/tag/$($ciReleaseInfo.Tag)) с установщиком и SHA-256."
