using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

public sealed class EpgTimingWindow : Window
{
    private readonly MainViewModel _model;
    private readonly ComboBox _channels = new() { IsTextSearchEnabled = true };
    private readonly ChannelItem[] _channelChoices;
    private ChannelItem? SelectedChannel => _channels.SelectedIndex >= 0 && _channels.SelectedIndex < _channelChoices.Length ? _channelChoices[_channels.SelectedIndex] : null;
    private readonly TextBox _minutes = new() { Width = 82, MaxLength = 5, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15, LineHeight = 23 };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private CancellationTokenSource? _query;
    private bool _closed;

    public EpgTimingWindow(MainWindow main, Window owner, ChannelItem? channel = null)
    {
        _model = main.Model; Theme.Window(this); Owner = owner; Title = "DzhiggaTV · Поправка времени телепрограммы";
        MaxWidth = Math.Max(320, SystemParameters.WorkArea.Width - 24); MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 24);
        Width = Math.Min(600, MaxWidth); Height = Math.Min(580, MaxHeight); MinWidth = Math.Min(420, MaxWidth); MinHeight = Math.Min(440, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowBounds.Attach(this, () => false);
        var body = new StackPanel { Margin = new(24) };
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        body.Children.Add(new TextBlock { Text = "Время телепрограммы", FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 14) });
        body.Children.Add(new TextBlock { Text = "Поправка действует только для выбранного канала.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) });
        _channelChoices = _model.AllChannels.Where(_model.IsAllowed).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        _channels.ItemsSource = _channelChoices.Select(c => c.Name).ToArray();
        body.Children.Add(_channels);
        body.Children.Add(new TextBlock { Text = "Сдвинуть передачи на, мин", Margin = new(0, 18, 0, 8) });
        var controls = new WrapPanel();
        void Step(string label, int amount)
        {
            var button = new Button { Content = label, Padding = new(10, 8, 10, 8), MinWidth = 42 };
            button.Click += (_, _) => { int.TryParse(_minutes.Text, out int value); _minutes.Text = Math.Clamp(value + amount, -1440, 1440).ToString(CultureInfo.InvariantCulture); };
            controls.Children.Add(button);
        }
        Step("−5", -5); Step("−1", -1); controls.Children.Add(_minutes); Step("+1", 1); Step("+5", 5);
        var reset = new Button { Content = "Сбросить", Padding = new(10, 8, 10, 8) }; reset.Click += (_, _) => _minutes.Text = "0"; controls.Children.Add(reset); body.Children.Add(controls);
        var help = new TextBlock { Text = "+10 — передача начинается на 10 минут позже расписания.\n−10 — на 10 минут раньше. 0 — без поправки.", TextWrapping = TextWrapping.Wrap, LineHeight = 21, FontSize = 12, Margin = new(0, 12, 0, 18) };
        help.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); body.Children.Add(help);
        var card = new Border { Child = _preview, Padding = new(14), CornerRadius = new(10) }; card.SetResourceReference(BackgroundProperty, "CardBrush"); body.Children.Add(card);
        _notice.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); body.Children.Add(_notice);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true }; var apply = new Button { Content = "Применить", IsDefault = true };
        apply.SetResourceReference(BorderBrushProperty, "AccentBrush");
        apply.Click += async (_, _) =>
        {
            if (SelectedChannel is not { } selected || !ReadMinutes(out int value)) return;
            apply.IsEnabled = false;
            try { await _model.SetEpgOffsetAsync(selected.Id, value); DialogResult = true; }
            catch { _notice.Text = "Не удалось сохранить поправку. Повторите попытку."; }
            finally { apply.IsEnabled = true; }
        };
        actions.Children.Add(cancel); actions.Children.Add(apply); body.Children.Add(actions);
        System.Windows.Automation.AutomationProperties.SetName(_channels, "Канал для поправки времени");
        System.Windows.Automation.AutomationProperties.SetName(_minutes, "Поправка телепрограммы в минутах");
        _channels.SelectionChanged += (_, _) => { if (SelectedChannel is { } selected) _minutes.Text = _model.EpgOffset(selected.Id).ToString(CultureInfo.InvariantCulture); };
        _minutes.TextChanged += async (_, _) => await PreviewAsync();
        _channels.SelectedIndex = Math.Max(0, Array.FindIndex(_channelChoices, c => c.Id == (channel ?? _model.Active)?.Id));
        if (_minutes.Text.Length == 0) _minutes.Text = "0";
        Closed += (_, _) => { _closed = true; _query?.Cancel(); };
    }
    private bool ReadMinutes(out int value)
    {
        if (int.TryParse(_minutes.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value is >= -1440 and <= 1440) { _notice.Text = ""; return true; }
        _notice.Text = "Введите целое число минут от −1440 до +1440."; return false;
    }
    private async Task PreviewAsync()
    {
        _query?.Cancel(); _query?.Dispose(); _query = new(); var token = _query.Token;
        if (SelectedChannel is not { } selected || !ReadMinutes(out int minutes)) return;
        var epg = _model.EpgFor(selected.Id);
        if (epg is null) { _preview.Text = "Сначала назначьте каналу телепрограмму через его контекстное меню."; return; }
        try
        {
            await Task.Delay(180, token);
            var guide = await _model.Guide.GetNowNextAsync(epg with { OffsetMinutes = minutes }, token: token);
            if (_closed || token.IsCancellationRequested) return;
            _preview.Text = guide.Current is { } current ? $"С учётом поправки сейчас:\n{current.Title}\n{current.Start.ToLocalTime():HH:mm} — {current.End.ToLocalTime():HH:mm}" : "На текущее время нет данных. Поправка будет применяться к загруженным передачам.";
        }
        catch (OperationCanceledException) { }
        catch { if (!_closed && !token.IsCancellationRequested) _preview.Text = "Не удалось загрузить предварительный просмотр."; }
    }
}
