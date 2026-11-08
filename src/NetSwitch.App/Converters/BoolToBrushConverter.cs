using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NetSwitch.App.Converters;

public sealed class BoolToBrushConverter : IValueConverter
{
    public IBrush TrueBrush { get; set; } = new SolidColorBrush(Color.Parse("#10B981"));

    public IBrush FalseBrush { get; set; } = new SolidColorBrush(Color.Parse("#9CA3AF"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TrueBrush : FalseBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
