using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DzhiggaTV.App;

public static class Theme
{
    public static bool IsLight { get; private set; }
    public static void Window(Window window)
    {
        window.Style = (Style)Application.Current.FindResource(typeof(Window));
        WindowAppearance.Attach(window);
    }
    public static void Apply(bool light)
    {
        IsLight = light;
        var resources = Application.Current.Resources;
        var colors = light
            ? new[] { "#EFF3F5", "#FFFFFF", "#E0E8ED", "#C9D4DB", "#17252E", "#576C7A", "#087D79" }
            : new[] { "#0C0F12", "#14191E", "#1D242B", "#29323A", "#EEF3F5", "#8998A4", "#63D9CD" };
        var keys = new[] { "BackgroundBrush", "PanelBrush", "CardBrush", "BorderBrush", "TextBrush", "MutedBrush", "AccentBrush" };
        for (var i = 0; i < keys.Length; i++) resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        foreach (Window window in Application.Current.Windows) WindowAppearance.Apply(window);
    }
}
public sealed class TextPrompt : Window
{
    private readonly TextBox _text;
    private TextPrompt(Window owner, string title, string label, string value)
    {
        Theme.Window(this);
        Owner = owner; Title = "DzhiggaTV · " + title; Width = 450; Height = 220; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = label, Margin = new(0,0,0,12) });
        _text = new() { Text = value }; panel.Children.Add(_text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0,16,0,0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true }; buttons.Children.Add(cancel);
        var ok = new Button { Content = "Сохранить", IsDefault = true }; ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(_text.Text)) DialogResult = true; }; buttons.Children.Add(ok); panel.Children.Add(buttons);
        Loaded += (_, _) => { _text.Focus(); _text.SelectAll(); };
    }
    public static string? Show(Window owner, string title, string label, string value = "")
    { var dialog = new TextPrompt(owner, title, label, value); return dialog.ShowDialog() == true ? dialog._text.Text.Trim() : null; }
}
