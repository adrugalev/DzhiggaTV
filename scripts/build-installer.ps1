param([string]$PortableDirectory, [string]$CompilerPath)
. (Join-Path $PSScriptRoot 'common.ps1')
if (!$PortableDirectory) { $PortableDirectory = Join-Path $RepositoryRoot 'outputs\DzhiggaTV-portable-win-x64' }
$PortableDirectory = [IO.Path]::GetFullPath($PortableDirectory)
if (!(Test-Path -LiteralPath (Join-Path $PortableDirectory 'DzhiggaTV.exe'))) { throw 'Сначала выполните scripts\publish.ps1.' }
if (!$CompilerPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) { $CompilerPath = $compiler.Source }
    else {
        foreach ($candidate in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 7\ISCC.exe")) {
            if (Test-Path -LiteralPath $candidate) { $CompilerPath = $candidate; break }
        }
    }
}
if (!$CompilerPath -or !(Test-Path -LiteralPath $CompilerPath)) {
    throw 'Не найден компилятор Inno Setup. Укажите -CompilerPath C:\path\ISCC.exe. Скрипт не устанавливает программы в систему.'
}
$output = Join-Path $RepositoryRoot 'outputs'
& $CompilerPath "/DAppSource=$PortableDirectory" "/DOutputPath=$output" (Join-Path $PSScriptRoot 'DzhiggaTV.iss')
if ($LASTEXITCODE -ne 0) { throw "Компилятор установщика завершился с кодом $LASTEXITCODE." }
