using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DzhiggaTV.Remote;

namespace DzhiggaTV.App;

/// <summary>The player overlay owns this card; hiding it cancels preparation, not an active server.</summary>
public sealed class RemoteQuickConnectView : Border
{
    private readonly MainWindow _main;
    private readonly RemoteServer _server;
    private readonly Image _qr = new() { Width = 208, Height = 208, Stretch = Stretch.Uniform };
    private readonly Border _qrFrame;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 13, LineHeight = 19 };
    private readonly TextBlock _address = new() { TextAlignment = TextAlignment.Center, FontSize = 11, Margin = new(0, 8, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 4, Margin = new(0, 14, 0, 0) };
    private readonly Button _refresh = new() { Content = "Обновить QR-код", FontSize = 12, Padding = new(12, 7, 12, 7), Margin = new(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _opening;
    private DateTimeOffset _expires;
    private HashSet<string> _knownDevices = [];
    private bool _busy, _prepared, _scanned, _connected;
    public event Action? Dismissed;

    public RemoteQuickConnectView(MainWindow main, RemoteServer? server = null)
    {
        _main = main; _server = server ?? main.Remote;
        Visibility = Visibility.Collapsed; MaxWidth = 352; Margin = new(12); Padding = new(18);
        HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
        Background = new SolidColorBrush(Color.FromArgb(248, 17, 25, 31)); CornerRadius = new(16);
        BorderBrush = new SolidColorBrush(Color.FromRgb(72, 119, 119)); BorderThickness = new(1);
        var body = new StackPanel();
        Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var heading = new DockPanel { Margin = new(0, 0, 0, 10) };
        var close = new Button { Content = "×", Width = 26, Height = 26, Padding = new(0), FontSize = 19, Background = Brushes.Transparent, BorderThickness = new(0), ToolTip = "Закрыть (Esc)" };
        System.Windows.Automation.AutomationProperties.SetName(close, "Закрыть быстрое подключение Remote");
        DockPanel.SetDock(close, Dock.Right); close.Click += (_, _) => Dismiss(); heading.Children.Add(close);
        heading.Children.Add(new TextBlock { Text = "Dzhigga Remote", Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }); body.Children.Add(heading);
        body.Children.Add(new TextBlock { Text = "Смотрите на iPhone", FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(99, 217, 205)), Margin = new(0, 0, 0, 15) });
        _qrFrame = new Border { Child = _qr, Background = Brushes.White, CornerRadius = new(10), Padding = new(8), HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 14), Visibility = Visibility.Collapsed };
        RenderOptions.SetBitmapScalingMode(_qr, BitmapScalingMode.NearestNeighbor);
        System.Windows.Automation.AutomationProperties.SetName(_qr, "QR-код быстрого подключения телефона");
        body.Children.Add(_qrFrame); body.Children.Add(_status); body.Children.Add(_address); body.Children.Add(_progress); body.Children.Add(_refresh);
        _status.Foreground = new SolidColorBrush(Color.FromRgb(224, 232, 237)); _address.Foreground = new SolidColorBrush(Color.FromRgb(142, 165, 180));
        _progress.Foreground = new SolidColorBrush(Color.FromRgb(99, 217, 205));
        _refresh.Click += async (_, _) => { if (_busy) return; if (_prepared && _server.Running) RefreshCode(); else await PrepareAsync(); };
        _timer.Tick += (_, _) => RefreshPairing();
        Unloaded += (_, _) => Dismiss();
    }
    public Task OpenAsync()
    {
        if (Visibility == Visibility.Visible) return Task.CompletedTask;
        Visibility = Visibility.Visible;
        return PrepareAsync();
    }
    public void FitToViewport(double width, double height)
    {
        MaxWidth = Math.Max(240, Math.Min(352, width - 24)); MaxHeight = Math.Max(60, height - 24);
        _qr.Width = _qr.Height = Math.Clamp(height - 230, 112, 208);
    }
    public void Dismiss()
    {
        _opening?.Cancel(); _timer.Stop();
        _qr.Source = null; _qrFrame.Visibility = Visibility.Collapsed;
        bool wasVisible = Visibility == Visibility.Visible; Visibility = Visibility.Collapsed;
        if (wasVisible) Dismissed?.Invoke();
    }
    private async Task PrepareAsync()
    {
        _opening?.Cancel();
        using var opening = new CancellationTokenSource(); _opening = opening;
        _busy = true; _prepared = _scanned = _connected = false; _timer.Stop();
        _qr.Source = null; _qrFrame.Visibility = _refresh.Visibility = Visibility.Collapsed;
        _address.Text = ""; _status.Text = "Включаем Remote и совместимость с iPhone…";
        _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = true;
        var progress = new Progress<RemotePreparation>(p =>
        {
            if (_opening != opening || opening.IsCancellationRequested) return;
            _status.Text = p.Message; _progress.IsIndeterminate = p.Percent is null; _progress.Value = p.Percent ?? 0;
        });
        try
        {
            await _main.StartQuickRemoteAsync(_server, progress, opening.Token);
            opening.Token.ThrowIfCancellationRequested();
            _prepared = true;
            RefreshCode(); _timer.Start();
        }
        catch (OperationCanceledException) when (opening.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!opening.IsCancellationRequested)
            {
                _status.Text = error is RemoteException ? error.Message : "Не удалось включить Remote. Проверьте сеть и повторите попытку.";
                _address.Text = "Параметры подключения — в Remote слева.";
                _refresh.Content = "Повторить"; _refresh.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (_opening == opening) { _opening = null; _busy = false; _progress.Visibility = Visibility.Collapsed; }
        }
    }
    private void RefreshCode()
    {
        _knownDevices = _server.Devices.Devices.Select(d => d.Id).ToHashSet();
        _scanned = _connected = false;
        _qr.Source = RemoteQrCode.Create(_server.PairingUrl()); _qrFrame.Visibility = Visibility.Visible;
        _expires = DateTimeOffset.UtcNow.AddMinutes(3);
        _status.Text = "Наведите камеру iPhone на QR-код.\nЗатем подтвердите подключение на компьютере.";
        _address.Text = "Одна домашняя сеть · " + _server.Url;
        _refresh.Content = "Обновить QR-код"; _refresh.Visibility = Visibility.Collapsed;
        _timer.Start();
    }
    private void RefreshPairing()
    {
        if (_busy || Visibility != Visibility.Visible) return;
        if (!_server.Running)
        {
            _qr.Source = null; _qrFrame.Visibility = Visibility.Collapsed;
            _status.Text = "Remote выключен. Нажмите, чтобы подключить телефон снова.";
            _refresh.Content = "Включить Remote"; _refresh.Visibility = Visibility.Visible; _timer.Stop(); return;
        }
        if (_server.Devices.Devices.FirstOrDefault(d => !_knownDevices.Contains(d.Id)) is { } device)
        {
            _connected = true; _qr.Source = null; _qrFrame.Visibility = Visibility.Collapsed;
            _status.Text = "✓ " + device.Name + " подключён\nПриятного просмотра!";
            _refresh.Content = "Подключить ещё телефон"; _refresh.Visibility = Visibility.Visible;
        }
        else if (_server.Devices.PendingRequests.Count > 0)
        {
            _scanned = true; _qr.Source = null; _qrFrame.Visibility = Visibility.Collapsed;
            _status.Text = "QR-код считан.\nПодтвердите подключение в окне на компьютере.";
        }
        else if (!_connected && (_scanned || DateTimeOffset.UtcNow >= _expires))
        {
            _qr.Source = null; _qrFrame.Visibility = Visibility.Collapsed;
            _status.Text = _scanned ? "Подключение не подтверждено.\nМожно получить новый QR-код." : "Время действия QR-кода истекло.";
            _refresh.Content = "Обновить QR-код"; _refresh.Visibility = Visibility.Visible;
        }
    }
}
