using System.Windows;
using System.Windows.Controls;
using DzhiggaTV.Core;
using DzhiggaTV.Services;

namespace DzhiggaTV.App;

public sealed class MappingWindow : Window
{
    public MappingWindow(MainWindow main, ChannelItem channel)
    {
        Theme.Window(this);
        Owner = main; Title = "DzhiggaTV · сопоставление EPG"; Width = 650; Height = 610; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new(24) }; Content = dock;
        var title = new TextBlock { Text = channel.Name, FontSize = 24, Margin = new(0,0,0,15) }; DockPanel.SetDock(title,Dock.Top); dock.Children.Add(title);
        var search = new TextBox { ToolTip = "Название или ID канала EPG.ONE", Margin = new(0,0,0,12) }; DockPanel.SetDock(search,Dock.Top); dock.Children.Add(search);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons,Dock.Bottom); dock.Children.Add(buttons);
        var list = new ListBox { DisplayMemberPath = "Label" }; dock.Children.Add(list);
        var matcher = new ChannelMatcher(main.Model.EpgChannels.Values.ToArray());
        void Update() => list.ItemsSource = matcher.Candidates(channel.Channel, 150, search.Text).Select(c => new MappingChoice(c.EpgId,$"{c.Name}  ·  {c.EpgId}  ·  {c.Confidence:P0} {c.Reason}")).ToArray();
        search.TextChanged += (_, _) => Update(); Update();
        var clear = new Button { Content = "Снять назначение" }; clear.Click += async (_, _) => { await main.Model.Store.RemoveMappingAsync(channel.Id); DialogResult = true; }; buttons.Children.Add(clear);
        var save = new Button { Content = "Назначить", IsDefault = true }; save.Click += async (_, _) =>
        {
            if (list.SelectedItem is MappingChoice match)
            { await main.Model.Store.SaveMappingsAsync([new(channel.Id,match.Id,main.Model.EpgChannels[match.Id].Name,"manual",1,true)]); main.Model.InvalidateLogo(channel.Id); DialogResult = true; }
        }; buttons.Children.Add(save);
    }
    private sealed record MappingChoice(string Id,string Label);
}
