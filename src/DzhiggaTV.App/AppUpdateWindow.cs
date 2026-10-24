using L = DzhiggaTV.Localization.Texts;
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
    public PreparedAppUpdate? PreparedUpdate { get; private set; }
    public AppRelease? SelectedRelease { get; private set; }

    public AppUpdateWindow(Window owner, UpdateCoordinator updates, string cache, bool checkNow)
    {
        Theme.Window(this); Owner = owner; _updates = updates; _cache = cache;
        Title = L.T("DzhiggaTV · Обновление"); ShowInTaskbar = false;
        Width = 630; Height = 640; MinWidth = 500; MinHeight = 440;
        MaxHeight = Math.Max(440, SystemParameters.WorkArea.Height - 40); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(28) }; Content = root;
        var header = new StackPanel { Margin = new(0, 0, 0, 20) };
        var mark = new TextBlock { Text = "↑", FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        mark.SetResourceReference(ForegroundProperty, "AccentBrush");
        var icon = new Border { Width = 52, Height = 52, CornerRadius = new(15), Child = mark, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 14) };
        icon.SetResourceReference(BackgroundProperty, "CardBrush"); header.Children.Add(icon);
        _heading = new() { Text = L.T("Обновление DzhiggaTV"), FontSize = 25, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _detail = Muted(L.F("Установлена версия {0}", App.DisplayVersion)); _detail.Margin = new(0, 8, 0, 0);
        header.Children.Add(_heading); header.Children.Add(_detail); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Margin = new(0, 18, 0, 0) };
        _progress = new() { Height = 5, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
        footer.Children.Add(_progress); _status = Muted(""); _status.Margin = new(0, 12, 0, 8); footer.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _later = new() { Content = L.T("Позже"), MinWidth = 90, IsCancel = true };
        _later.Click += (_, _) => Close();
        _primary = new() { Content = L.T("Проверить ещё раз"), MinWidth = 170, IsDefault = true };
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
        _primary.IsEnabled = false; _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = true; _status.Text = L.T("Проверяем новые выпуски на GitHub…");
        try { await _updates.CheckAsync(true, _lifetime.Token); if (!IsVisible) return; ShowRelease(); }
        catch (OperationCanceledException) { }
        finally { if (IsVisible) { _primary.IsEnabled = true; _progress.IsIndeterminate = false; _progress.Visibility = Visibility.Collapsed; } }
    }
    private void ShowRelease()
    {
        SelectedRelease = _updates.Available; _notes.Children.Clear();
        if (SelectedRelease is { } release)
        {
            _heading.Text = L.F("Доступна версия {0}", release.Tag);
            bool delta = release.Delta is not null && UpdateLauncher.CanUseDelta;
            long size = delta ? release.Delta!.Size : release.Size;
            _detail.Text = L.F("Сейчас {0}  ·  Загрузка {1:N1} МБ", App.DisplayVersion, size / 1024d / 1024d);
            _primary.Content = L.T("Обновить и перезапустить");
            _status.Text = L.T("Плейлисты, избранное и настройки сохранятся. Загрузка не прерывает просмотр.");
            AddNotes(release.Notes);
        }
        else
        {
            _heading.Text = _updates.LastError is null ? L.T("У вас актуальная версия") : L.T("Проверка не завершена");
            _primary.Content = L.T("Проверить ещё раз");
            _status.Text = _updates.LastError ?? L.T("Плеер сообщит, когда на GitHub появится новый выпуск.");
            AddNotes(_updates.LastError is null ? L.T("Новых версий пока нет. Можно продолжать просмотр.") : L.T("Подключитесь к интернету и повторите проверку."));
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
        _downloading = true; _primary.IsEnabled = false; _later.Content = L.T("Отмена");
        _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = false;
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(p => { if (!IsVisible) return; _progress.Value = p.Percent; _status.Text = L.F("Загрузка обновления · {0:N0}%  ({1:N1} из {2:N1} МБ)", p.Percent, p.Received / 1024d / 1024d, p.Total / 1024d / 1024d); });
            if (release.Delta is not null && UpdateLauncher.CanUseDelta)
            {
                try
                {
                    var package = await _updates.Service.DownloadDeltaAsync(release, _cache, progress, _lifetime.Token);
                    var preparing = new Progress<DzhiggaTV.Installer.InstallProgress>(p => { if (!IsVisible) return; _progress.Value = p.Percent; _status.Text = p.Message; });
                    PreparedUpdate = await UpdateLauncher.PrepareDeltaAsync(package, release.Version, _cache, preparing, _lifetime.Token);
                    if (IsVisible) DialogResult = true;
                    return;
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                catch
                {
                    if (!IsVisible) return;
                    _status.Text = L.T("Подготовим полный пакет обновления…"); _progress.Value = 0;
                    _detail.Text = L.F("Сейчас {0}  ·  Загрузка {1:N1} МБ", App.DisplayVersion, release.Size / 1024d / 1024d);
                }
            }
            var installer = await _updates.Service.DownloadAsync(release, _cache, progress, _lifetime.Token);
            if (!IsVisible) return;
            // Validate the embedded executable version before asking the running player to close.
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(installer);
            if (info.ProductName != "DzhiggaTV" || !Version.TryParse(info.FileVersion, out var version) || GitHubUpdateService.Normalize(version) != release.Version)
                throw new UpdateException(L.T("Версия установщика не совпадает с выпуском. Обновление не запущено."));
            PreparedUpdate = new(installer); DialogResult = true;
        }
        catch (OperationCanceledException) { if (IsVisible) _status.Text = L.T("Загрузка отменена. Можно повторить."); }
        catch (UpdateException error) { if (IsVisible) _status.Text = error.Message; }
        catch { if (IsVisible) _status.Text = L.T("Не удалось скачать обновление. Проверьте интернет и свободное место, затем повторите."); }
        finally
        {
            _downloading = false;
            if (IsVisible) { _primary.IsEnabled = true; _later.Content = L.T("Позже"); _progress.Visibility = Visibility.Collapsed; }
            else _lifetime.Dispose();
        }
    }
}
