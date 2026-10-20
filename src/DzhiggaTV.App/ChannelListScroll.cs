using System.Windows.Controls;
using System.Windows.Threading;

namespace DzhiggaTV.App;

internal static class ChannelListScroll
{
    public static void ResetToTop(ListBox list)
    {
        if (!list.IsLoaded) return;
        // Filtering and selection can queue their own layout/BringIntoView work.
        // Reset after that work, so a recycled row cannot restore the old offset.
        list.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (!list.IsLoaded) return;
            list.UpdateLayout();
            MainWindow.FindVisualChildren<ScrollViewer>(list).FirstOrDefault()?.ScrollToTop();
        });
    }
}
