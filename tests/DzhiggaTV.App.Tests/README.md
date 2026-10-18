# App regressions

Run on Windows x64 with the .NET 10 SDK and Windows Desktop runtime:

```powershell
.\work\dotnet\dotnet.exe run --project tests/DzhiggaTV.App.Tests/DzhiggaTV.App.Tests.csproj -c Release
```

After a solution build, append `--no-build` to run the existing binaries. The harness returns exit code 0 only when every case passes.

The tests link the production view model and row classes, use a WPF STA dispatcher, and create an isolated SQLite library with synthetic programmes and valid local PNGs. They use no network, provider playlist, libmpv, or visible window. Each fixture clears SQLite pools and removes its own temporary directory.

The cases cover a missing EPG/logo followed by the first successful data refresh, replacement of an already displayed programme and logo after remapping, removal of a mapping, active/selected row references plus user changes after the settings reload sequence, a local picon archive replacing the logo fallback, and EPG retry/progress state after a setup failure.
