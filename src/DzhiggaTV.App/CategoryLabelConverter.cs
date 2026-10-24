using System.Globalization;
using System.Windows.Data;
using DzhiggaTV.Localization;

namespace DzhiggaTV.App;

public sealed class CategoryLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ReferenceEquals(value, "Все категории") ? Texts.T("Все категории") : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
