using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DzhiggaTV.Services;

namespace DzhiggaTV.App;

public sealed class AppUpdateWindow : Window
{
    private readonly UpdateCoordinator _updates;
    private readonly string _cache;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _heading, _detail, _status;
    private readonly StackPanel _notes = new();
    private readonly Button _primary, _later;
    private readonly ProgressBar _progress;
    private bool _downloading;
    public string? PreparedInstaller { get; private set; }
    public AppRelease? SelectedRelease { get; private set; }

    public AppUpdateWindow(Window owner, UpdateCoordinator updates, string cache, bool checkNow)
    {
        Theme.Window(this); Owner = owner; _updates = updates; _cache = cache;
        Title = "DzhiggaTV · Обновление"; ShowInTaskbar = false;
        Width = 630; Height = 640; MinWidth = 500; MinHeight = 440;
        MaxHeight = Math.Max(440, SystemParameters.WorkArea.Height - 40); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(28) }; Content = root;
        var header = new StackPanel { Margin = new(0, 0, 0, 20) };
        var mark = new TextBlock { Text = "↑", FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        mark.SetResourceReference(ForegroundProperty, "AccentBrush");
        var icon = new Border { Width = 52, Height = 52, CornerRadius = new(15), Child = mark, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 14) };
        icon.SetResourceReference(BackgroundProperty, "CardBrush"); header.Children.Add(icon);
        _heading = new() { Text = "Обновление DzhiggaTV", FontSize = 25, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _detail = Muted($"Установлена версия {App.DisplayVersion}"); _detail.Margin = new(0, 8, 0, 0);
        header.Children.Add(_heading); header.Children.Add(_detail); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Margin = new(0, 18, 0, 0) };
        _progress = new() { Height = 5, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
        footer.Children.Add(_progress); _status = Muted(""); _status.Margin = new(0, 12, 0, 8); footer.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _later = new() { Content = "Позже", MinWidth = 90, IsCancel = true };
        _later.Click += (_, _) => Close();
        _primary = new() { Content = "Проверить ещё раз", MinWidth = 170, IsDefault = true };
        _primary.SetResourceReference(BorderBrushProperty, "AccentBrush"); _primary.Click += Primary_Click;
        buttons.Children.Add(_later); buttons.Children.Add(_primary); footer.Children.Add(buttons); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var card = new Border { CornerRadius = new(12), Padding = new(18), Child = new ScrollViewer { Content = _notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        card.SetResourceReference(BackgroundProperty, "PanelBrush"); root.Children.Add(card);
        Closing += (_, _) => _lifetime.Cancel(); Closed += (_, _) => { if (!_downloading) _lifetime.Dispose(); };
        Loaded += async (_, _) => { if (checkNow || !updates.HasUpdate) await CheckAsync(); else ShowRelease(); };
    }

    private static TextBlock Muted(string text)
    { var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 19 }; block.SetResourceReference(ForegroundProperty, "MutedBrush"); return block; }
    private async Task CheckAsync()
    {
        _primary.IsEnabled = false; _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = true; _status.Text = "Проверяем новые выпуски на GitHub…";
        try { await _updates.CheckAsync(true, _lifetime.Token); if (!IsVisible) return; ShowRelease(); }
        catch (OperationCanceledException) { }
        finally { if (IsVisible) { _primary.IsEnabled = true; _progress.IsIndeterminate = false; _progress.Visibility = Visibility.Collapsed; } }
    }
    private void ShowRelease()
    {
        SelectedRelease = _updates.Available; _notes.Children.Clear();
        if (SelectedRelease is { } release)
        {
            _heading.Text = $"Доступна версия {release.Tag}";
            _detail.Text = $"Сейчас {App.DisplayVersion}  ·  Загрузка {release.Size / 1024d / 1024d:N0} МБ";
            _primary.Content = "Обновить и перезапустить";
            _status.Text = "Плейлисты, избранное и настройки сохранятся. Загрузка не прерывает просмотр.";
            AddNotes(release.Notes);
        }
        else
        {
            _heading.Text = _updates.LastError is null ? "У вас актуальная версия" : "Проверка не завершена";
            _primary.Content = "Проверить ещё раз";
            _status.Text = _updates.LastError ?? "Плеер сообщит, когда на GitHub появится новый выпуск.";
            AddNotes(_updates.LastError is null ? "Новых версий пока нет. Можно продолжать просмотр." : "Подключитесь к интернету и повторите проверку.");
        }
    }
    private void AddNotes(string notes)
    {
        foreach (string line in notes.Replace("\r", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            bool heading = line.TrimStart().StartsWith('#');
            string text = Regex.Replace(line.Trim().TrimStart('#').Trim(), @"!?\[([^\]]+)\]\([^)]*\)", "$1");
            text = text.Replace("**", "").Replace("__", "").Replace("`", "");
            text = Regex.Replace(text, @"^[-*]\s+", "•  ");
            var paragraph = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = heading ? 16 : 13, LineHeight = 21, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal, Margin = new(0, heading ? 10 : 2, 6, 8) };
            _notes.Children.Add(paragraph);
        }
    }
    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        if (SelectedRelease is not { } release) { await CheckAsync(); return; }
        _downloading = true; _primary.IsEnabled = false; _later.Content = "Отмена";
        _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = false;
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(p => { if (!IsVisible) return; _progress.Value = p.Percent; _status.Text = $"Загрузка обновления · {p.Percent:N0}%  ({p.Received / 1024d / 1024d:N1} из {p.Total / 1024d / 1024d:N1} МБ)"; });
            var installer = await _updates.Service.DownloadAsync(release, _cache, progress, _lifetime.Token);
            if (!IsVisible) return;
            // Validate the embedded executable version before asking the running player to close.
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(installer);
            if (info.ProductName != "DzhiggaTV" || !Version.TryParse(info.FileVersion, out var version) || GitHubUpdateService.Normalize(version) != release.Version)
                throw new UpdateException("Версия установщика не совпадает с выпуском. Обновление не запущено.");
            PreparedInstaller = installer; DialogResult = true;
        }
        catch (OperationCanceledException) { if (IsVisible) _status.Text = "Загрузка отменена. Можно повторить."; }
        catch (UpdateException error) { if (IsVisible) _status.Text = error.Message; }
        catch { if (IsVisible) _status.Text = "Не удалось скачать обновление. Проверьте интернет и свободное место, затем повторите."; }
        finally
        {
            _downloading = false;
            if (IsVisible) { _primary.IsEnabled = true; _later.Content = "Позже"; _progress.Visibility = Visibility.Collapsed; }
            else _lifetime.Dispose();
        }
    }
}
