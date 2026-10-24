using L = DzhiggaTV.Localization.Texts;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DzhiggaTV.Installer;

public sealed class AutomaticUpdateWindow : Window
{
    private readonly UpdateRequest _request;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _retry;
    private CancellationTokenSource? _cancellation;
    private bool _working;
    public AutomaticUpdateWindow(UpdateRequest request)
    {
        _request = request;
        Title = L.T("DzhiggaTV · Обновление"); Width = 530; Height = 280; ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(12, 15, 18)); Foreground = Brushes.White; FontFamily = new("Segoe UI");
        WindowAppearance.Attach(this);
        var root = new StackPanel { Margin = new(32), VerticalAlignment = VerticalAlignment.Center }; Content = root;
        root.Children.Add(new TextBlock { Text = L.T("Обновляем DzhiggaTV"), FontSize = 25, FontWeight = FontWeights.SemiBold });
        _status = new() { Text = L.T("Настройки и плейлисты остаются с вами."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 24), Foreground = Brushes.LightGray }; root.Children.Add(_status);
        _progress = new() { Height = 6, Minimum = 0, Maximum = 100, Foreground = new SolidColorBrush(Color.FromRgb(99, 217, 205)) }; root.Children.Add(_progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        _retry = new() { Content = L.T("Повторить"), Padding = new(13, 8, 13, 8), Visibility = Visibility.Collapsed }; _retry.Click += async (_, _) => await RunAsync();
        buttons.Children.Add(_retry); root.Children.Add(buttons);
        Loaded += async (_, _) => await RunAsync();
        Closing += (_, e) => { if (_working) { _cancellation?.Cancel(); _status.Text = L.T("Завершаем текущую операцию…"); e.Cancel = true; } };
    }
    private async Task RunAsync()
    {
        if (_working) return;
        _working = true; _retry.Visibility = Visibility.Collapsed;
        _cancellation = new(); _progress.IsIndeterminate = true;
        bool success = false;
        try
        {
            _status.Text = L.T("Проверяем папку приложения…");
            await InstallEngine.ValidateUpdateTargetAsync(_request.Target, _cancellation.Token);
            Version expectedVersion = _request.TargetVersion is { } targetVersion ? DzhiggaTV.Updating.DeltaPackage.VersionOf(targetVersion) : typeof(App).Assembly.GetName().Version!;
            string installedExe = System.IO.Path.Combine(_request.Target, "DzhiggaTV.exe");
            if (System.IO.File.Exists(installedExe) && Version.TryParse(System.Diagnostics.FileVersionInfo.GetVersionInfo(installedExe).FileVersion, out var installedVersion) &&
                installedVersion > expectedVersion)
                throw new InstallException(L.T("В этой папке уже установлена более новая версия. Откройте плеер."));
            bool fresh = !System.IO.Directory.Exists(_request.Target) || !System.IO.Directory.EnumerateFileSystemEntries(_request.Target).Any();
            _status.Text = L.T("Сохраняем настройки и ждём закрытия плеера…");
            await _request.WaitForPlayerAsync(_cancellation.Token);
            _progress.IsIndeterminate = false;
            using var archive = _request.Payload is not null ? await _request.OpenPayloadAsync(_cancellation.Token) :
                App.OpenArchive() ?? throw new InstallException(L.T("В установщике отсутствует архив приложения."));
            var progress = new Progress<InstallProgress>(p => { _progress.Value = p.Percent; _status.Text = p.Message; });
            await InstallEngine.InstallAsync(archive, _request.Target, progress, _cancellation.Token, expectedVersion);
            _progress.Value = 100; _status.Text = L.T("Готово. Открываем обновлённый плеер…");
            string defaultTarget = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DzhiggaTV");
            if (fresh && System.IO.Path.GetFullPath(_request.Target).Equals(defaultTarget, StringComparison.OrdinalIgnoreCase))
                MainWindow.TryShortcut(Environment.SpecialFolder.Programs, _request.Target, []);
            _request.LaunchPlayer(); success = true;
        }
        catch (OperationCanceledException) { _status.Text = L.T("Обновление отменено. Можно повторить попытку."); }
        catch (InstallException error) { _status.Text = error.Message; }
        catch { _status.Text = L.T("Не удалось завершить обновление. Закройте другие окна DzhiggaTV, проверьте свободное место и повторите."); }
        finally
        {
            _working = false; _cancellation.Dispose(); _cancellation = null; _progress.IsIndeterminate = false;
            if (success) Close();
            else _retry.Visibility = Visibility.Visible;
        }
    }
}
