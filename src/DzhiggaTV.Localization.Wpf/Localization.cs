using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using DzhiggaTV.Localization;

namespace DzhiggaTV.Localization.Wpf;

/// <summary>Gettext-style XAML messages, with live refresh for explicitly localized C# labels.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class Loc(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => Texts.T(Texts.KeyFromId(Key));
}

public static class ApplicationLanguage
{
    private static bool _initialized;
    public static void Initialize()
    {
        if (_initialized) return; _initialized = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window) ApplyWindow(window);
        }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is ContextMenu menu) Visit(menu, [], []);
        }));
        Texts.LanguageChanged += (_, _) =>
        {
            var app = Application.Current; if (app is null) return;
            if (app.Dispatcher.CheckAccess()) Refresh(); else app.Dispatcher.Invoke(Refresh);
        };
    }
    public static void Refresh()
    {
        using var language = Texts.UseLanguage(Texts.DefaultLanguage);
        var seen = new HashSet<DependencyObject>();
        var models = new HashSet<ILocalizedViewModel>();
        foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray())
        {
            ApplyWindow(window);
            Visit(window, seen, models);
        }
    }
    private static void ApplyWindow(Window window)
    {
        window.Language = XmlLanguage.GetLanguage(Texts.Culture.Name);
        window.FontFamily = new FontFamily(Texts.Language == "zh-Hans" ? "Microsoft YaHei UI, Microsoft YaHei, Segoe UI" : "Segoe UI Variable, Segoe UI");
    }
    private static void Property(DependencyObject element, DependencyProperty property)
    {
        if (element.GetValue(property) is not string text || !Texts.IsMessage(text)) return;
        element.SetCurrentValue(property, Texts.Refresh(text));
    }
    private static void Visit(DependencyObject element, HashSet<DependencyObject> seen, HashSet<ILocalizedViewModel> models)
    {
        if (!seen.Add(element)) return;
        if (element is FrameworkElement framework)
        {
            if (framework.DataContext is ILocalizedViewModel model && models.Add(model)) model.RefreshLanguage();
            Property(framework, FrameworkElement.ToolTipProperty);
            Property(framework, System.Windows.Automation.AutomationProperties.NameProperty);
            Property(framework, System.Windows.Automation.AutomationProperties.HelpTextProperty);
        }
        if (element is Window) Property(element, Window.TitleProperty);
        if (element is DatePicker date) date.Language = XmlLanguage.GetLanguage(Texts.Culture.Name);
        if (element is TextBlock) Property(element, TextBlock.TextProperty);
        if (element is Run) Property(element, Run.TextProperty);
        if (element is ContentControl) Property(element, ContentControl.ContentProperty);
        if (element is HeaderedContentControl) Property(element, HeaderedContentControl.HeaderProperty);
        if (element is HeaderedItemsControl) Property(element, HeaderedItemsControl.HeaderProperty);
        // Labels are explicitly tagged by Texts.T/F. Do not translate editable fields or imported data.
        if (element is Selector selector && selector.ItemsSource is IEnumerable<string> source && source.Any(Texts.IsMessage))
        {
            int selected = selector.SelectedIndex;
            selector.ItemsSource = source.Select(Texts.Refresh).ToArray(); selector.SelectedIndex = selected;
        }
        if (element is ItemsControl items && items.ItemsSource is null)
            for (int i = 0; i < items.Items.Count; i++)
                if (items.Items[i] is string label && Texts.IsMessage(label)) items.Items[i] = Texts.Refresh(label);
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>().ToArray()) Visit(child, seen, models);
        if (element is Visual or System.Windows.Media.Media3D.Visual3D)
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Visit(VisualTreeHelper.GetChild(element, i), seen, models);
        if (element is Popup { Child: { } popup }) Visit(popup, seen, models);
        if (element is FrameworkElement { ContextMenu: { } context }) Visit(context, seen, models);
    }
}
