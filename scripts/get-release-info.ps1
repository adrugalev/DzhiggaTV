$ErrorActionPreference = 'Stop'
$releaseInfoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$releaseInfoXml = Get-Content -LiteralPath (Join-Path $releaseInfoRoot 'Directory.Build.props') -Raw
$releaseInfoVersion = [string]$releaseInfoXml.Project.PropertyGroup.Version
if ($releaseInfoVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw 'Нужна числовая версия вида 1.0.14 в Directory.Build.props.' }
$null = [version]$releaseInfoVersion
$releaseInfoNotes = Get-Content -LiteralPath (Join-Path $releaseInfoRoot 'src\DzhiggaTV.App\ReleaseNotesWindow.cs') -Raw
$releaseInfoPattern = '(?m)^\s*\("' + [regex]::Escape($releaseInfoVersion) + '",\s*("(?:[^"\\]|\\.)*")\),?\s*$'
$releaseInfoMatch = [regex]::Match($releaseInfoNotes, $releaseInfoPattern)
if (!$releaseInfoMatch.Success) { throw "Добавьте описание версии $releaseInfoVersion в ReleaseNotesWindow.cs перед выпуском." }
$releaseInfoSummary = ConvertFrom-Json -InputObject $releaseInfoMatch.Groups[1].Value
$releaseInfoWork = Join-Path $releaseInfoRoot 'work'
New-Item -ItemType Directory -Path $releaseInfoWork -Force | Out-Null
$releaseInfoNotesPath = Join-Path $releaseInfoWork 'release-notes.md'
Set-Content -LiteralPath $releaseInfoNotesPath -Value ("## Что нового`n`n" + $releaseInfoSummary) -Encoding utf8
[PSCustomObject]@{ Version = $releaseInfoVersion; Tag = 'v' + $releaseInfoVersion; NotesPath = $releaseInfoNotesPath }
