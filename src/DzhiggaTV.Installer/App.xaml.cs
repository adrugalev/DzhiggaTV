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
        int extraction = Array.IndexOf(e.Args, "--extract-to");
        if (extraction >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int exit = 1;
            object result;
            try
            {
                if (extraction + 1 >= e.Args.Length) throw new InstallException("Не указана папка установки.");
                using var archive = OpenArchive() ?? throw new InstallException("В этой сборке отсутствует установочный архив.");
                var installed = await InstallEngine.InstallAsync(archive, e.Args[extraction + 1], null, CancellationToken.None);
                // Headless extraction never writes shortcuts, registry keys, or user settings.
                result = new { success = true, installed.Directory, installed.FileCount, installed.PreviousVersionBackup };
                exit = 0;
            }
            catch (InstallException ex) { result = new { success = false, error = ex.Message }; }
            catch { result = new { success = false, error = "Не удалось распаковать DzhiggaTV." }; }
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
