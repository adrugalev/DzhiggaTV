using System.Windows;
using System.Windows.Controls;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

internal sealed class ArchiveTimeWindow : Window
{
    public DateTimeOffset SelectedTime { get; private set; }
    public ArchiveTimeWindow(Window owner, ChannelItem channel, CatchupSource source, DateTimeOffset position)
    {
        Theme.Window(this); Owner = owner; Title = "DzhiggaTV · Архив";
        Width = 510; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var now = DateTimeOffset.Now; var earliest = source.Earliest(now);
        var root = new StackPanel { Margin = new(24) }; Content = root;
        root.Children.Add(new TextBlock { Text = "Архив канала", FontSize = 23, FontWeight = FontWeights.SemiBold });
        var heading = new TextBlock { Text = channel.Name, FontSize = 14, Margin = new(0, 7, 0, 18) }; heading.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); root.Children.Add(heading);
        var choices = new Grid(); choices.ColumnDefinitions.Add(new()); choices.ColumnDefinitions.Add(new() { Width = new(88) }); choices.ColumnDefinitions.Add(new() { Width = new(88) });
        var dates = Enumerable.Range(0, (now.Date - earliest.Date).Days + 1).Select(n => new DateChoice(now.Date.AddDays(-n))).ToArray();
        var date = new ComboBox { ItemsSource = dates, SelectedItem = dates.FirstOrDefault(d => d.Date == position.LocalDateTime.Date) ?? dates[0], Margin = new(0, 0, 10, 0), Height = 38 };
        var hours = new ComboBox { ItemsSource = Enumerable.Range(0, 24).Select(n => n.ToString("00")).ToArray(), SelectedIndex = position.LocalDateTime.Hour, Height = 38, Margin = new(0, 0, 8, 0) };
        var minutes = new ComboBox { ItemsSource = Enumerable.Range(0, 60).Select(n => n.ToString("00")).ToArray(), SelectedIndex = position.LocalDateTime.Minute, Height = 38, Margin = new(0) };
        System.Windows.Automation.AutomationProperties.SetName(date, "День архива"); System.Windows.Automation.AutomationProperties.SetName(hours, "Час"); System.Windows.Automation.AutomationProperties.SetName(minutes, "Минута");
        choices.Children.Add(date); Grid.SetColumn(hours, 1); choices.Children.Add(hours); Grid.SetColumn(minutes, 2); choices.Children.Add(minutes); root.Children.Add(choices);
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 14, 0, 20) }; hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); root.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", IsCancel = true }; var watch = new Button { Content = "Смотреть архив", IsDefault = true, Margin = new(10, 0, 0, 0) }; watch.SetResourceReference(BorderBrushProperty, "AccentBrush");
        buttons.Children.Add(cancel); buttons.Children.Add(watch); root.Children.Add(buttons);
        void Validate()
        {
            if (date.SelectedItem is not DateChoice day || hours.SelectedIndex < 0 || minutes.SelectedIndex < 0) return;
            var local = DateTime.SpecifyKind(day.Date.AddHours(hours.SelectedIndex).AddMinutes(minutes.SelectedIndex), DateTimeKind.Unspecified);
            SelectedTime = new(local, TimeZoneInfo.Local.GetUtcOffset(local));
            watch.IsEnabled = source.Contains(SelectedTime, DateTimeOffset.Now);
            hint.Text = watch.IsEnabled ? $"Доступен архив с {earliest.ToLocalTime():dd.MM HH:mm}. Время указано по вашему часовому поясу." : "Выберите время в доступном диапазоне архива, до текущего момента.";
        }
        date.SelectionChanged += (_, _) => Validate(); hours.SelectionChanged += (_, _) => Validate(); minutes.SelectionChanged += (_, _) => Validate();
        watch.Click += (_, _) => { Validate(); if (watch.IsEnabled) DialogResult = true; };
        Validate();
    }
    private sealed record DateChoice(DateTime Date)
    {
        public override string ToString() => Date == DateTime.Today ? "Сегодня" : Date == DateTime.Today.AddDays(-1) ? "Вчера" : Date.ToString("dd MMMM · ddd");
    }
}
