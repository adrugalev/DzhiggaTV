using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DzhiggaTV.Localization;
using DzhiggaTV.Localization.Wpf;
using DzhiggaTV.Remote;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); _checks++; }

    [STAThread]
    private static void Main()
    {
        Check(Texts.DefaultLanguage == "ru", "Russian is the default, independently of OS culture");
        var ids = new HashSet<string>();
        foreach (var (key, entry) in Texts.Messages)
        {
            Check(ids.Add(Texts.IdForKey(key)) && Texts.KeyFromId(Texts.IdForKey(key)) == key, "Unique XAML message ID");
            foreach (var translated in new[] { entry.En, entry.Zh })
            {
                Check(!string.IsNullOrWhiteSpace(translated) && !translated.Contains('\ufffd'), "Complete translation: " + key);
                var original = CompositeFormat.Parse(key); var target = CompositeFormat.Parse(translated);
                Check(original.MinimumArgumentCount == target.MinimumArgumentCount && Slots(key) == Slots(translated), "Format placeholders preserved: " + key);
            }
        }
        var old = Texts.T("Телепрограмма");
        var user = new string("Телепрограмма".AsSpan());
        Texts.SetLanguage("en");
        Check(Texts.Refresh(old) == "TV guide" && ReferenceEquals(Texts.Refresh(user), user), "Refresh UI text without rewriting identical user text");
        var nested = Texts.F("{0} · {1}", Texts.T("Телепрограмма"), user);
        Texts.SetLanguage("zh-Hans");
        Check(Texts.Refresh(nested) == "节目单 · Телепрограмма", "Nested UI labels refresh; user content stays unchanged");
        Check(Texts.Culture.Name == "zh-CN", "Chinese date/number culture");
        Check(Texts.Normalize("unsupported") == "ru" && Texts.Normalize("zh-CN") == "zh-Hans", "Language normalization and fallback");
        Texts.SetLanguage("ru");
        Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
        {
            var language = i % 2 == 0 ? "en" : "zh-Hans";
            using (Texts.UseLanguage(language))
            {
                await Task.Yield();
                if (Texts.Language != language || Texts.DefaultLanguage != "ru") throw new Exception("Remote language leaked across requests");
                using (Texts.UseLanguage("ru")) if (Texts.T("Телепрограмма") != "Телепрограмма") throw new Exception("Nested language scope");
                if (Texts.Language != language) throw new Exception("Request scope was not restored");
            }
        })).GetAwaiter().GetResult();
        Check(Texts.Language == "ru", "Concurrent Remote preferences preserve PC language");
        var source = new RemotePlayerState("id", user, null, Texts.T("Нет подключения"), 50, false, false, false,
            [new(1, user, true)], new(false, false, null, null, []), 1);
        using (Texts.UseLanguage("en"))
        {
            var localized = source.InRequestLanguage();
            Check(localized.Status == "Not connected" && ReferenceEquals(localized.ChannelName, user) && ReferenceEquals(localized.Audio[0].Label, user), "Remote translates status without changing provider metadata");
        }

        var application = new Application(); ApplicationLanguage.Initialize();
        foreach (var language in DzhiggaTV.App.LanguageChoice.All)
        {
            var presenter = new ContentPresenter { Content = language, ContentTemplate = DzhiggaTV.App.LanguageChoice.Template() };
            presenter.Measure(new Size(240, 40)); presenter.Arrange(new Rect(0, 0, 240, 40)); presenter.UpdateLayout();
            var content = (StackPanel)System.Windows.Media.VisualTreeHelper.GetChild(presenter, 0);
            BindingOperations.GetBindingExpression((Image)content.Children[0], Image.SourceProperty)?.UpdateTarget();
            BindingOperations.GetBindingExpression((TextBlock)content.Children[1], TextBlock.TextProperty)?.UpdateTarget();
            Check(((TextBlock)content.Children[1]).Text == language.Name && ReferenceEquals(((Image)content.Children[0]).Source, language.Flag), "Native language label and vector flag binding: " + ((TextBlock)content.Children[1]).Text);
            Check(language.Flag.IsFrozen && language.Flag.Width == 30 && language.Flag.Height == 20, "Flag stays sharp at any DPI");
        }
        var caption = new TextBlock { Text = Texts.T("Телепрограмма") };
        var input = new TextBox { Text = user };
        var choices = new ComboBox { ItemsSource = new[] { Texts.T("Все категории"), user }, SelectedIndex = 1 };
        var bound = new TextBlock(); bound.SetBinding(TextBlock.TextProperty, new Binding(nameof(Value.Label)) { Source = new Value { Label = Texts.T("Настройки") } });
        var date = new DatePicker { SelectedDate = new DateTime(2026, 10, 24) };
        var page = new StackPanel(); foreach (var control in new UIElement[] { caption, input, choices, bound, date }) page.Children.Add(control);
        var window = new Window { Content = page };
        Texts.SetLanguage("en");
        Check(caption.Text == "TV guide" && input.Text == user, "Open window updates; editable text stays intact");
        Check(choices.SelectedIndex == 1 && ReferenceEquals(choices.SelectedItem, user), "Selection identity survives a language change");
        Check(bound.Text == "Settings" && BindingOperations.IsDataBound(bound, TextBlock.TextProperty), "Refresh preserves bindings");
        Texts.SetLanguage("zh-Hans");
        Check(caption.Text == "节目单", "Chinese label: " + caption.Text);
        Check(string.Equals(date.Language.IetfLanguageTag, "zh-CN", StringComparison.OrdinalIgnoreCase) && date.SelectedDate == new DateTime(2026, 10, 24), "Chinese date picker preserves value: " + date.Language.IetfLanguageTag);
        Texts.SetLanguage("ru"); window.Close(); application.Shutdown();
        Console.WriteLine($"PASS {_checks} localization checks across {Texts.Messages.Count} messages");
    }
    private static string Slots(string value) => string.Join("|", Regex.Matches(value, @"(?<!\{)\{\d+(?:,[+-]?\d+)?(?::[^}]+)?\}(?!\})").Select(m => m.Value).Order());
    private sealed class Value { public required string Label { get; init; } }
}
