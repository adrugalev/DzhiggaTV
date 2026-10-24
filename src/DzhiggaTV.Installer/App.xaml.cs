using L = DzhiggaTV.Localization.Texts;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace DzhiggaTV.Installer;

public partial class App : Application
{
    internal static Stream? OpenArchive() => Assembly.GetExecutingAssembly().GetManifestResourceStream("DzhiggaTV.Portable.zip");
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DzhiggaTV.Localization.Wpf.ApplicationLanguage.Initialize();
        int language = Array.IndexOf(e.Args, "--language");
        L.SetLanguage(language >= 0 && language + 1 < e.Args.Length ? e.Args[language + 1] : "ru");
        if (e.Args.Contains("--update-target") && !e.Args.Contains("--extract-to"))
        {
            try { MainWindow = new AutomaticUpdateWindow(UpdateRequest.Parse(e.Args)); MainWindow.Show(); }
            catch { MessageBox.Show(L.T("Не удалось подготовить обновление. Запустите плеер и повторите попытку."), "DzhiggaTV"); Shutdown(1); }
            return;
        }
        int extraction = Array.IndexOf(e.Args, "--extract-to");
        if (extraction >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int exit = 1;
            object result;
            try
            {
                if (extraction + 1 >= e.Args.Length) throw new InstallException(L.T("Не указана папка установки."));
                var external = e.Args.Contains("--update-payload") ? UpdateRequest.Parse(e.Args) : null;
                using var archive = external is not null ? await external.OpenPayloadAsync(CancellationToken.None) : OpenArchive() ?? throw new InstallException(L.T("В этой сборке отсутствует установочный архив."));
                var installed = await InstallEngine.InstallAsync(archive, e.Args[extraction + 1], null, CancellationToken.None,
                    external?.TargetVersion is { } version ? DzhiggaTV.Updating.DeltaPackage.VersionOf(version) : null);
                // Headless extraction never writes shortcuts, registry keys, or user settings.
                result = new { success = true, installed.Directory, installed.FileCount, installed.PreviousVersionBackup };
                exit = 0;
            }
            catch (InstallException ex) { result = new { success = false, error = ex.Message }; }
            catch { result = new { success = false, error = L.T("Не удалось распаковать DzhiggaTV.") }; }
            int report = Array.IndexOf(e.Args, "--report");
            if (report >= 0 && report + 1 < e.Args.Length)
            {
                try { await File.WriteAllTextAsync(Path.GetFullPath(e.Args[report + 1]), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); }
                catch { exit = 1; }
            }
            Shutdown(exit);
            return;
        }
        new MainWindow().Show();
    }
}
