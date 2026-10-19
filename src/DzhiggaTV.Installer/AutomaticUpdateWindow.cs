using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DzhiggaTV.Installer;

public sealed class AutomaticUpdateWindow : Window
{
    private readonly UpdateRequest _request;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _retry, _open;
    private CancellationTokenSource? _cancellation;
    private bool _working;
    public AutomaticUpdateWindow(UpdateRequest request)
    {
        _request = request;
        Title = "DzhiggaTV · Обновление"; Width = 530; Height = 330; ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(12, 15, 18)); Foreground = Brushes.White; FontFamily = new("Segoe UI");
        var root = new StackPanel { Margin = new(32), VerticalAlignment = VerticalAlignment.Center }; Content = root;
        root.Children.Add(new TextBlock { Text = "Обновляем DzhiggaTV", FontSize = 25, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "Настройки и плейлисты остаются с вами.", Foreground = Brushes.LightGray, Margin = new(0, 10, 0, 24) });
        _progress = new() { Height = 6, Minimum = 0, Maximum = 100, Foreground = new SolidColorBrush(Color.FromRgb(99, 217, 205)) }; root.Children.Add(_progress);
        _status = new() { Text = "Подготовка…", TextWrapping = TextWrapping.Wrap, Margin = new(0, 16, 0, 16), MinHeight = 44, Foreground = Brushes.LightGray }; root.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _open = new() { Content = "Открыть плеер", Padding = new(13, 8, 13, 8), Margin = new(0, 0, 8, 0), Visibility = Visibility.Collapsed };
        _open.Click += (_, _) => { try { _request.LaunchPlayer(); Close(); } catch { _status.Text = "Откройте DzhiggaTV из меню «Пуск» или папки установки."; } };
        _retry = new() { Content = "Повторить", Padding = new(13, 8, 13, 8), Visibility = Visibility.Collapsed }; _retry.Click += async (_, _) => await RunAsync();
        buttons.Children.Add(_open); buttons.Children.Add(_retry); root.Children.Add(buttons);
        Loaded += async (_, _) => await RunAsync();
        Closing += (_, e) => { if (_working) { _cancellation?.Cancel(); _status.Text = "Завершаем текущую операцию…"; e.Cancel = true; } };
    }
    private async Task RunAsync()
    {
        if (_working) return;
        _working = true; _retry.Visibility = _open.Visibility = Visibility.Collapsed;
        _cancellation = new(); _progress.IsIndeterminate = true;
        bool success = false;
        try
        {
            _status.Text = "Проверяем папку приложения…";
            await InstallEngine.ValidateUpdateTargetAsync(_request.Target, _cancellation.Token);
            Version expectedVersion = _request.TargetVersion is { } targetVersion ? DzhiggaTV.Updating.DeltaPackage.VersionOf(targetVersion) : typeof(App).Assembly.GetName().Version!;
            string installedExe = System.IO.Path.Combine(_request.Target, "DzhiggaTV.exe");
            if (System.IO.File.Exists(installedExe) && Version.TryParse(System.Diagnostics.FileVersionInfo.GetVersionInfo(installedExe).FileVersion, out var installedVersion) &&
                installedVersion > expectedVersion)
                throw new InstallException("В этой папке уже установлена более новая версия. Откройте плеер.");
            bool fresh = !System.IO.Directory.Exists(_request.Target) || !System.IO.Directory.EnumerateFileSystemEntries(_request.Target).Any();
            _status.Text = "Сохраняем настройки и ждём закрытия плеера…";
            await _request.WaitForPlayerAsync(_cancellation.Token);
            _progress.IsIndeterminate = false;
            using var archive = _request.Payload is not null ? await _request.OpenPayloadAsync(_cancellation.Token) :
                App.OpenArchive() ?? throw new InstallException("В установщике отсутствует архив приложения.");
            var progress = new Progress<InstallProgress>(p => { _progress.Value = p.Percent; _status.Text = p.Message; });
            await InstallEngine.InstallAsync(archive, _request.Target, progress, _cancellation.Token, expectedVersion);
            _progress.Value = 100; _status.Text = "Готово. Открываем обновлённый плеер…";
            string defaultTarget = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DzhiggaTV");
            if (fresh && System.IO.Path.GetFullPath(_request.Target).Equals(defaultTarget, StringComparison.OrdinalIgnoreCase))
                MainWindow.TryShortcut(Environment.SpecialFolder.Programs, _request.Target, []);
            _request.LaunchPlayer(); success = true;
        }
        catch (OperationCanceledException) { _status.Text = "Обновление отменено. Можно открыть плеер или повторить попытку."; }
        catch (InstallException error) { _status.Text = error.Message; }
        catch { _status.Text = "Не удалось завершить обновление. Закройте другие окна DzhiggaTV, проверьте свободное место и повторите."; }
        finally
        {
            _working = false; _cancellation.Dispose(); _cancellation = null; _progress.IsIndeterminate = false;
            if (success) Close();
            else { _open.Visibility = _retry.Visibility = Visibility.Visible; }
        }
    }
}
