Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Get-DzhiggaDotnet {
    $local = Join-Path $script:RepositoryRoot 'work\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) { return $local }
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($installed) { return $installed.Source }
    throw 'Нужен .NET 10 SDK x64: https://dotnet.microsoft.com/download/dotnet/10.0'
}

function Invoke-DzhiggaDotnet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & (Get-DzhiggaDotnet) @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet завершился с кодом $LASTEXITCODE." }
}

function Assert-DzhiggaHash {
    param([string]$Path, [string]$Expected)
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ne $Expected) { throw 'Контрольная сумма зависимости не совпадает. Файл не будет использован.' }
}
