param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$baselineRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$baselineHttp = [Net.Http.HttpClient]::new()
$baselineHttp.Timeout = [TimeSpan]::FromSeconds(45)
$baselineHttp.DefaultRequestHeaders.UserAgent.ParseAdd('DzhiggaTV-Packaging')
try {
    $baselineResponse = $baselineHttp.GetAsync('https://api.github.com/repos/adrugalev/DzhiggaTV/releases/latest').GetAwaiter().GetResult()
    if (!$baselineResponse.IsSuccessStatusCode) { Write-Host 'Нет доступной базы дельта-обновления; будет собран полный выпуск.'; return }
    $baselineRelease = $baselineResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
    if ($baselineRelease.draft -or $baselineRelease.prerelease -or $baselineRelease.tag_name -notmatch '^v?(\d+\.\d+\.\d+(?:\.\d+)?)$' -or [version]$Matches[1] -ge [version]$Version) { return }
    $baselineAssets = @($baselineRelease.assets | Where-Object { $_.name -eq 'DzhiggaTV-win-x64.manifest.json' -and $_.state -eq 'uploaded' })
    if ($baselineAssets.Count -ne 1) { Write-Host 'Предыдущий выпуск ещё не содержит базы дельта-обновления.'; return }
    $baselineAsset = $baselineAssets[0]
    if ($baselineAsset.size -le 0 -or $baselineAsset.size -gt 2MB -or $baselineAsset.digest -notmatch '^sha256:([a-fA-F0-9]{64})$') { throw 'Неверная база обновления.' }
    $baselineExpectedHash = $Matches[1]
    $baselineUri = [uri]$baselineAsset.browser_download_url
    if ($baselineUri.Scheme -ne 'https' -or $baselineUri.Host -ne 'github.com' -or $baselineUri.Port -ne 443 -or $baselineUri.UserInfo -or $baselineUri.Query -or $baselineUri.Fragment -or $baselineUri.AbsolutePath -cne ('/adrugalev/DzhiggaTV/releases/download/' + [uri]::EscapeDataString($baselineRelease.tag_name) + '/DzhiggaTV-win-x64.manifest.json')) { throw 'Неверный адрес базы обновления.' }
    $baselineDownload = $baselineHttp.GetAsync($baselineUri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    $null = $baselineDownload.EnsureSuccessStatusCode()
    $baselineInput = $baselineDownload.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $baselineBuffer = [byte[]]::new(8192)
    $baselineMemory = [IO.MemoryStream]::new()
    try {
        while (($baselineRead = $baselineInput.Read($baselineBuffer, 0, $baselineBuffer.Length)) -gt 0) {
            if ($baselineMemory.Length + $baselineRead -gt 2MB) { throw 'Слишком большая база обновления.' }
            $baselineMemory.Write($baselineBuffer, 0, $baselineRead)
        }
        $baselineBytes = $baselineMemory.ToArray()
    } finally { $baselineInput.Dispose(); $baselineMemory.Dispose(); $baselineDownload.Dispose() }
    if ($baselineBytes.Length -ne $baselineAsset.size -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($baselineBytes)) -ine $baselineExpectedHash) { throw 'Контрольная сумма базы обновления не совпадает.' }
    $baselineJson = [Text.Encoding]::UTF8.GetString($baselineBytes) | ConvertFrom-Json
    if ([version]$baselineJson.Version -ne [version]($baselineRelease.tag_name -replace '^v', '')) { throw 'Версия базы не совпадает с выпуском.' }
    $baselinePath = Join-Path $baselineRoot ('work\baseline-' + [Guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllBytes($baselinePath, $baselineBytes)
    Write-Host "База дельта-обновления: $($baselineRelease.tag_name)"
    return $baselinePath
} catch {
    Write-Warning 'Не удалось получить проверенную базу обновления. Полный установщик будет доступен; дельта-пакет в этом выпуске пропущен.'
} finally { $baselineHttp.Dispose() }
