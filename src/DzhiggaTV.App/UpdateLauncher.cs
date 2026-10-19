using System.Diagnostics;
using DzhiggaTV.Services;
using DzhiggaTV.Installer;
using DzhiggaTV.Updating;

namespace DzhiggaTV.App;

public sealed record PreparedAppUpdate(string Installer, string? Payload = null, string? PayloadSha256 = null);

internal static class UpdateLauncher
{
    private static readonly string[] RunnerFiles = ["DzhiggaTV.Setup.exe", "DzhiggaTV.Setup.dll", "DzhiggaTV.Setup.deps.json", "DzhiggaTV.Setup.runtimeconfig.json"];
    internal static bool CanUseDelta => File.Exists(Path.Combine(AppContext.BaseDirectory, ".dzhiggatv-install.json")) &&
        RunnerFiles.All(f => File.Exists(Path.Combine(AppContext.BaseDirectory, f)));

    public static async Task<PreparedAppUpdate> PrepareDeltaAsync(string package, Version version, string cache, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        string current = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        await Task.Run(() => InstallEngine.ValidateUpdateTargetAsync(current, token), token);
        string work = Path.Combine(cache, version.ToString(4), "prepared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var payload = await Task.Run(() => DeltaPackage.ReconstructAsync(package, current, version, Path.Combine(work, "application.zip"), progress, token), token);
            // Use the already installed runtime. The isolated runner must not lock files in the folder being replaced.
            string runner = Path.Combine(work, "runner"); Directory.CreateDirectory(runner);
            await Task.Run(() =>
            {
                foreach (string file in Directory.EnumerateFiles(current))
                {
                    token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(file);
                    if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        File.Copy(file, Path.Combine(runner, name));
                }
            }, token);
            return new(Path.Combine(runner, "DzhiggaTV.Setup.exe"), payload.Path, payload.Sha256);
        }
        catch
        {
            // This randomly named directory was created exclusively for this attempt.
            try { Directory.Delete(work, true); } catch { }
            throw;
        }
    }

    public static void Start(PreparedAppUpdate prepared, Version expectedVersion, string dataDirectory)
    {
        string installer = prepared.Installer;
        // A valid download must also contain the same product/version as its release tag.
        var info = FileVersionInfo.GetVersionInfo(installer);
        Version runnerVersion = prepared.Payload is null ? expectedVersion : GitHubUpdateService.Normalize(typeof(App).Assembly.GetName().Version!);
        if (info.ProductName != "DzhiggaTV" || !Version.TryParse(info.FileVersion, out var version) || GitHubUpdateService.Normalize(version) != runnerVersion)
            throw new UpdateException("Версия установщика не совпадает с выпуском. Обновление не запущено.");
        string current = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string target = File.Exists(Path.Combine(current, ".dzhiggatv-install.json")) ? current :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DzhiggaTV");
        using var process = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer)! };
        foreach (var argument in new[] { "--update-target", target, "--wait-pid", process.Id.ToString(), "--wait-start", process.StartTime.ToUniversalTime().Ticks.ToString(), "--data-dir", Path.GetFullPath(dataDirectory) }) start.ArgumentList.Add(argument);
        if (prepared.Payload is not null)
            foreach (var argument in new[] { "--update-payload", prepared.Payload, "--payload-sha256", prepared.PayloadSha256!, "--target-version", expectedVersion.ToString(4) }) start.ArgumentList.Add(argument);
        using var launched = Process.Start(start) ?? throw new UpdateException("Не удалось запустить установку обновления.");
    }
}
