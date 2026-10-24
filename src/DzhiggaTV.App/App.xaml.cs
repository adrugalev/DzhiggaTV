using L = DzhiggaTV.Localization.Texts;
using System.Windows;
using System.Windows.Controls;

namespace DzhiggaTV.App;

public partial class App : Application
{
    public static string DisplayVersion => FormatVersion(typeof(App).Assembly.GetName().Version);
    internal static string FormatVersion(Version? version) => version is null ? "v—" : "v" + version.ToString(version.Revision > 0 ? 4 : version.Build > 0 ? 3 : 2);


    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DzhiggaTV.Localization.Wpf.ApplicationLanguage.Initialize();
        DispatcherUnhandledException += (_, args) =>
        {
            var diagnosticIndex = Array.IndexOf(e.Args, "--diagnostic-report");
            if (diagnosticIndex >= 0 && diagnosticIndex + 1 < e.Args.Length)
            {
                // Only exception type + method stack, never Message/Data/URL or credentials.
                try { File.AppendAllText(e.Args[diagnosticIndex + 1], args.Exception.GetType().FullName + "\n" + args.Exception.StackTrace + "\n"); } catch { }
            }
            // Exception text may contain provider credentials. Display a fixed, safe message.
            MessageBox.Show(L.T("Не удалось выполнить действие. Попробуйте ещё раз. Приватные адреса не записываются в журнал."), "DzhiggaTV", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        MainWindow = new MainWindow(e.Args);
        MainWindow.Show();
    }
    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: ChannelItem item } && MainWindow is MainWindow window)
            await window.Model.ToggleFavoriteAsync(item);
    }
}
