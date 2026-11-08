using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NetSwitch.Core.Models;

namespace NetSwitch.App.Converters;

public sealed class TestStatusToGeometryConverter : IValueConverter
{
    private static readonly Geometry Pass = Geometry.Parse(
        "M9,16.17 L4.83,12 L3.41,13.41 L9,19 L21,7 L19.59,5.59 Z");
    private static readonly Geometry Warn = Geometry.Parse(
        "M1,21 L23,21 L12,2 Z M13,18 L11,18 L11,16 L13,16 Z M13,14 L11,14 L11,10 L13,10 Z");
    private static readonly Geometry Fail = Geometry.Parse(
        "M12,2 C6.48,2 2,6.48 2,12 C2,17.52 6.48,22 12,22 C17.52,22 22,17.52 22,12 C22,6.48 17.52,2 12,2 Z M13,17 L11,17 L11,15 L13,15 Z M13,13 L11,13 L11,7 L13,7 Z");
    private static readonly Geometry Skip = Geometry.Parse(
        "M12,2 C6.48,2 2,6.48 2,12 C2,17.52 6.48,22 12,22 C17.52,22 22,17.52 22,12 C22,6.48 17.52,2 12,2 Z M7,11 L17,11 L17,13 L7,13 Z");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            TestStatus.Pass => Pass,
            TestStatus.Warn => Warn,
            TestStatus.Fail => Fail,
            _ => Skip
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class TestStatusToBrushConverter : IValueConverter
{
    private static readonly IBrush Pass = new SolidColorBrush(Color.Parse("#059669"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush Fail = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush Skip = new SolidColorBrush(Color.Parse("#9CA3AF"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            TestStatus.Pass => Pass,
            TestStatus.Warn => Warn,
            TestStatus.Fail => Fail,
            _ => Skip
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ToneToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tone = value as string ?? "info";
        var slot = parameter as string ?? "background";
        return (tone, slot) switch
        {
            ("success", "background") => Brush("#ECFDF5"),
            ("success", "border") => Brush("#A7F3D0"),
            ("success", "foreground") => Brush("#047857"),
            ("warning", "background") => Brush("#FFFBEB"),
            ("warning", "border") => Brush("#FDE68A"),
            ("warning", "foreground") => Brush("#B45309"),
            ("error", "background") => Brush("#FEF2F2"),
            ("error", "border") => Brush("#FECACA"),
            ("error", "foreground") => Brush("#B91C1C"),
            ("info", "background") => Brush("#EFF6FF"),
            ("info", "border") => Brush("#BFDBFE"),
            ("info", "foreground") => Brush("#1D4ED8"),
            _ => Brush("#F3F4F6")
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}

public sealed class ToneToGeometryConverter : IValueConverter
{
    private static readonly Geometry Check = Geometry.Parse(
        "M9,16.17 L4.83,12 L3.41,13.41 L9,19 L21,7 L19.59,5.59 Z");
    private static readonly Geometry Warning = Geometry.Parse(
        "M1,21 L23,21 L12,2 Z M13,18 L11,18 L11,16 L13,16 Z M13,14 L11,14 L11,10 L13,10 Z");
    private static readonly Geometry Error = Geometry.Parse(
        "M12,2 C6.48,2 2,6.48 2,12 C2,17.52 6.48,22 12,22 C17.52,22 22,17.52 22,12 C22,6.48 17.52,2 12,2 Z M13,17 L11,17 L11,15 L13,15 Z M13,13 L11,13 L11,7 L13,7 Z");
    private static readonly Geometry Info = Geometry.Parse(
        "M12,2 C6.48,2 2,6.48 2,12 C2,17.52 6.48,22 12,22 C17.52,22 22,17.52 22,12 C22,6.48 17.52,2 12,2 Z M13,17 L11,17 L11,11 L13,11 Z M13,9 L11,9 L11,7 L13,7 Z");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "success" => Check,
            "warning" => Warning,
            "error" => Error,
            _ => Info
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
