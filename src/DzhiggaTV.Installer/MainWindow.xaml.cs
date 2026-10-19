using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace DzhiggaTV.Installer;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cancellation;
    private bool _working, _installed;
    private string? _installedPath;
    public MainWindow()
    {
        InitializeComponent();
        DestinationText.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DzhiggaTV");
        using var archive = App.OpenArchive();
        if (archive == null) { InstallButton.IsEnabled = false; StatusText.Text = "В этой сборке отсутствует установочный архив."; }
        Closing += (_, e) => { if (_working) { _cancellation?.Cancel(); StatusText.Text = "Завершение текущей операции…"; e.Cancel = true; } };
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка установки DzhiggaTV" };
        if (dialog.ShowDialog(this) == true) DestinationText.Text = string.Equals(Path.GetFileName(dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar)), "DzhiggaTV", StringComparison.OrdinalIgnoreCase) ? dialog.FolderName : Path.Combine(dialog.FolderName, "DzhiggaTV");
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (_working) _cancellation?.Cancel(); else Close(); }
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installed)
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(_installedPath!, "DzhiggaTV.exe")) { UseShellExecute = true, WorkingDirectory = _installedPath }); Close(); }
            catch { StatusText.Text = "Не удалось запустить плеер. Откройте DzhiggaTV.exe из папки установки."; }
            return;
        }
        _working = true; SetEnabled(false); CancelButton.Content = "Отмена";
        _cancellation = new();
        try
        {
            using var archive = App.OpenArchive() ?? throw new InstallException("В этой сборке отсутствует установочный архив.");
            var progress = new Progress<InstallProgress>(p => { Progress.Value = p.Percent; StatusText.Text = p.Message; });
            var result = await InstallEngine.InstallAsync(archive, DestinationText.Text, progress, _cancellation.Token);
            var warnings = new List<string>();
            if (StartMenuCheck.IsChecked == true) TryShortcut(Environment.SpecialFolder.Programs, result.Directory, warnings);
            if (DesktopCheck.IsChecked == true) TryShortcut(Environment.SpecialFolder.DesktopDirectory, result.Directory, warnings);
            _installed = true; _installedPath = result.Directory; Progress.Value = 100;
            StatusText.Text = "DzhiggaTV установлен. Инструкция по удалению находится в папке приложения.";
            if (warnings.Count > 0) StatusText.Text += " Ярлык создать не удалось; запустите DzhiggaTV.exe из папки установки.";
            if (result.PreviousVersionBackup != null) StatusText.Text += " Предыдущая версия сохранена рядом с папкой приложения.";
            InstallButton.Content = "Запустить";
        }
        catch (OperationCanceledException) { StatusText.Text = "Установка отменена."; Progress.Value = 0; }
        catch (InstallException ex) { StatusText.Text = ex.Message; }
        catch { StatusText.Text = "Не удалось завершить установку. Закройте DzhiggaTV и выберите доступную папку."; }
        finally { _working = false; _cancellation.Dispose(); _cancellation = null; SetEnabled(true); CancelButton.Content = "Закрыть"; }
    }
    private void SetEnabled(bool enabled)
    {
        DestinationText.IsEnabled = BrowseButton.IsEnabled = StartMenuCheck.IsEnabled = DesktopCheck.IsEnabled = enabled && !_installed;
        InstallButton.IsEnabled = enabled;
    }
    internal static void TryShortcut(Environment.SpecialFolder folder, string root, List<string> warnings)
    {
        object? shell = null, shortcut = null;
        try
        {
            var destination = Environment.GetFolderPath(folder);
            if (!Directory.Exists(destination)) throw new IOException();
            string path = Path.Combine(destination, "DzhiggaTV.lnk");
            if (File.Exists(path)) return;
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException();
            shell = Activator.CreateInstance(type) ?? throw new InvalidOperationException();
            shortcut = ((dynamic)shell).CreateShortcut(path);
            ((dynamic)shortcut).TargetPath = Path.Combine(root, "DzhiggaTV.exe");
            ((dynamic)shortcut).WorkingDirectory = root;
            ((dynamic)shortcut).Description = "DzhiggaTV";
            ((dynamic)shortcut).IconLocation = Path.Combine(root, "DzhiggaTV.exe") + ",0";
            ((dynamic)shortcut).Save();
        }
        catch { warnings.Add("shortcut"); }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
