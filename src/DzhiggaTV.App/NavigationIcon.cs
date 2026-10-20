using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DzhiggaTV.App;

public sealed class NavigationIcon : Grid
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(NavigationIcon), new PropertyMetadata("", IconChanged));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(NavigationIcon), new PropertyMetadata(false, ActiveChanged));
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    private readonly Image _color = new() { Opacity = 0, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly Rectangle _neutral = new() { IsHitTestVisible = false };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly RotateTransform _rotation = new();
    private Button? _button;
    private Window? _window;
    private bool _keyboardNavigation;
    private bool _suppressHighlight;
    static NavigationIcon()
    {
        WidthProperty.OverrideMetadata(typeof(NavigationIcon), new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure));
        HeightProperty.OverrideMetadata(typeof(NavigationIcon), new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    }
    public NavigationIcon()
    {
        IsHitTestVisible = false; VerticalAlignment = VerticalAlignment.Center;
        RenderTransformOrigin = new(.5, .5);
        var transform = new TransformGroup(); transform.Children.Add(_scale); transform.Children.Add(_rotation); RenderTransform = transform;
        _neutral.SetResourceReference(Shape.FillProperty, "MutedBrush"); Children.Add(_neutral); Children.Add(_color);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) =>
        {
            Detach();
            _button = FindButton(this);
            if (_button is null) return;
            _button.MouseEnter += Enter; _button.MouseLeave += Leave;
            _button.IsKeyboardFocusWithinChanged += FocusChanged;
            _window = Window.GetWindow(this);
            if (_window is not null)
            {
                _window.PreviewKeyDown += KeyPressed; _window.PreviewMouseDown += PointerPressed;
                _window.Activated += Activated; _window.Deactivated += Deactivated;
            }
            Refresh();
        };
        Unloaded += (_, _) => Detach();
    }
    private void Detach()
    {
        if (_button is not null)
        {
            _button.MouseEnter -= Enter; _button.MouseLeave -= Leave; _button.IsKeyboardFocusWithinChanged -= FocusChanged; _button = null;
        }
        if (_window is not null)
        {
            _window.PreviewKeyDown -= KeyPressed; _window.PreviewMouseDown -= PointerPressed;
            _window.Activated -= Activated; _window.Deactivated -= Deactivated; _window = null;
        }
        _keyboardNavigation = false;
    }
    private static Button? FindButton(DependencyObject item)
    {
        while ((item = VisualTreeHelper.GetParent(item)) is not null) if (item is Button button) return button;
        return null;
    }
    private static void IconChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var icon = (NavigationIcon)sender;
        var source = new BitmapImage(new Uri($"pack://application:,,,/DzhiggaTV;component/Assets/Navigation/{e.NewValue}.png")); source.Freeze();
        icon._color.Source = source; icon._neutral.OpacityMask = new ImageBrush(source) { Stretch = Stretch.Uniform };
    }
    private static void ActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var icon = (NavigationIcon)sender;
        // A second click must visibly clear the star even while the pointer is still over it.
        icon._suppressHighlight = !(bool)e.NewValue;
        icon.Refresh();
    }
    private void Enter(object sender, MouseEventArgs e) { _suppressHighlight = false; Refresh(); }
    private void Leave(object sender, MouseEventArgs e) => Refresh();
    private void FocusChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && _keyboardNavigation) _suppressHighlight = false;
        Refresh();
    }
    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab && !(_button?.IsKeyboardFocusWithin == true && e.Key is Key.Enter or Key.Space)) return;
        _keyboardNavigation = true; _suppressHighlight = false; Refresh();
    }
    private void PointerPressed(object sender, MouseButtonEventArgs e) { _keyboardNavigation = false; Refresh(); }
    private void Activated(object? sender, EventArgs e) => Refresh();
    private void Deactivated(object? sender, EventArgs e) { _keyboardNavigation = false; Refresh(); }
    // A click may leave keyboard focus on the launcher after its dialog closes.
    // Highlight keyboard focus only when the user actually navigates with keys.
    // Pointer hover works even while another app (or the owned video overlay)
    // has keyboard focus. Only keyboard highlighting depends on activation.
    private void Refresh() => Animate(_button?.IsMouseOver == true ||
        _window?.IsActive != false && _keyboardNavigation && _button?.IsKeyboardFocusWithin == true);
    private void Animate(bool active)
    {
        var owner = Window.GetWindow(this);
        while (owner is not MainWindow && owner?.Owner is not null) owner = owner.Owner;
        bool reduce = !SystemParameters.ClientAreaAnimation || (owner is MainWindow main && main.Model.Flag("reducedMotion"));
        var duration = TimeSpan.FromMilliseconds(reduce ? 0 : active ? 180 : 230);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        void Change(DependencyObject target, DependencyProperty property, double value)
        {
            var animation = new DoubleAnimation(value, duration) { EasingFunction = easing };
            if (target is UIElement element) element.BeginAnimation(property, animation);
            else if (target is Animatable animatable) animatable.BeginAnimation(property, animation);
        }
        bool highlighted = IsActive || active && !_suppressHighlight;
        Change(_color, OpacityProperty, highlighted ? 1 : 0);
        Change(_neutral, OpacityProperty, highlighted ? 0 : 1);
        Change(_scale, ScaleTransform.ScaleXProperty, active && !reduce ? 1.13 : 1);
        Change(_scale, ScaleTransform.ScaleYProperty, active && !reduce ? 1.13 : 1);
        Change(_rotation, RotateTransform.AngleProperty, active && !reduce ? Icon == "settings" ? 18 : Icon == "recent" ? -10 : -4 : 0);
    }
}
