using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace DzhiggaTV.App;

internal sealed record LanguageChoice(string Code, string Name, ImageSource Flag)
{
    public override string ToString() => Name;
    public static IReadOnlyList<LanguageChoice> All { get; } =
    [new("ru", "Русский", Draw(("#fff", "M0,0 H30 V20 H0 Z"), ("#0039a6", "M0,6.667 H30 V20 H0 Z"), ("#d52b1e", "M0,13.333 H30 V20 H0 Z"))),
     new("en", "English", Draw(("#012169", "M0,0 H30 V20 H0 Z"), ("#fff", "M0,0 H4 L30,17 V20 H26 L0,3 Z M30,0 H26 L0,17 V20 H4 L30,3 Z"), ("#c8102e", "M0,0 H2 L30,18.667 V20 H28 L0,1.333 Z M30,0 H28 L0,18.667 V20 H2 L30,1.333 Z"), ("#fff", "M11,0 H19 V6 H30 V14 H19 V20 H11 V14 H0 V6 H11 Z"), ("#c8102e", "M13,0 H17 V8 H30 V12 H17 V20 H13 V12 H0 V8 H13 Z"))),
     new("zh-Hans", "简体中文", China())];

    public static DataTemplate Template()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var flag = new FrameworkElementFactory(typeof(Image)); flag.SetValue(FrameworkElement.WidthProperty, 24d); flag.SetValue(FrameworkElement.HeightProperty, 16d); flag.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0)); flag.SetBinding(Image.SourceProperty, new Binding(nameof(Flag))); panel.AppendChild(flag);
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Name))); label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); panel.AppendChild(label);
        var template = new DataTemplate { VisualTree = panel }; template.Seal(); return template;
    }
    private static ImageSource Draw(params (string Color, string Path)[] shapes)
    {
        var drawing = new DrawingGroup();
        foreach (var shape in shapes) drawing.Children.Add(new GeometryDrawing((Brush)new BrushConverter().ConvertFromInvariantString(shape.Color)!, null, Geometry.Parse(shape.Path)));
        drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
    private static ImageSource China()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(222, 41, 16)), null, new RectangleGeometry(new Rect(0, 0, 30, 20))));
        foreach (var (x, y, radius) in new[] { (5d, 5d, 3d), (10d, 2d, 1d), (12d, 4d, 1d), (12d, 7d, 1d), (10d, 9d, 1d) })
        {
            double rotation = radius == 3 ? -Math.PI / 2 : Math.Atan2(5 - y, 5 - x);
            var points = Enumerable.Range(0, 10).Select(i => { var r = i % 2 == 0 ? radius : radius * .382; var a = rotation + i * Math.PI / 5; return new Point(x + r * Math.Cos(a), y + r * Math.Sin(a)); }).ToArray();
            var star = new StreamGeometry(); using (var geometry = star.Open()) { geometry.BeginFigure(points[0], true, true); geometry.PolyLineTo(points[1..], true, false); }
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(255, 222, 0)), null, star));
        }
        drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
}
