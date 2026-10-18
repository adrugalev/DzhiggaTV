using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DzhiggaTV.Core;

namespace DzhiggaTV.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
}
public sealed class ActionCommand(Action<object?> action) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action(parameter);
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
public sealed class ChannelItem(Channel channel) : Observable
{
    public Channel Channel { get; } = channel;
    public string Id => Channel.Id;
    private string _name = channel.Name;
    public string Name { get => _name; set { if (Set(ref _name, value)) Raise(nameof(Initials)); } }
    public string Group { get; set; } = channel.Group;
    public string Initials => string.Join("", Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => x[0])).ToUpperInvariant();
    private bool _favorite;
    public bool IsFavorite { get => _favorite; set { if (Set(ref _favorite, value)) Raise(nameof(FavoriteGlyph)); } }
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    private string? _logoPath;
    public string? LogoPath { get => _logoPath; set => Set(ref _logoPath, value); }
    private string _nowTitle = "Телепрограмма не назначена";
    public string NowTitle { get => _nowTitle; set => Set(ref _nowTitle, value); }
    private string _endTime = "";
    public string EndTime { get => _endTime; set => Set(ref _endTime, value); }
    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public Programme? Current { get; set; }
    public Programme? Next { get; set; }
}
