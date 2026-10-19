using System.Diagnostics;
using DzhiggaTV.Services;

namespace DzhiggaTV.App;

internal static class UpdateLauncher
{
    public static void Start(string installer, Version expectedVersion, string dataDirectory)
    {
        // A valid download must also contain the same product/version as its release tag.
        var info = FileVersionInfo.GetVersionInfo(installer);
        if (info.ProductName != "DzhiggaTV" || !Version.TryParse(info.FileVersion, out var version) || GitHubUpdateService.Normalize(version) != expectedVersion)
            throw new UpdateException("Версия установщика не совпадает с выпуском. Обновление не запущено.");
        string current = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string target = File.Exists(Path.Combine(current, ".dzhiggatv-install.json")) ? current :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DzhiggaTV");
        using var process = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer)! };
        foreach (var argument in new[] { "--update-target", target, "--wait-pid", process.Id.ToString(), "--wait-start", process.StartTime.ToUniversalTime().Ticks.ToString(), "--data-dir", Path.GetFullPath(dataDirectory) }) start.ArgumentList.Add(argument);
        using var launched = Process.Start(start) ?? throw new UpdateException("Не удалось запустить установку обновления.");
    }
}
