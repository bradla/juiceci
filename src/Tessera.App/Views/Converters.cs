using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Tessera.App.Views;

/// <summary>Looks up an application resource (e.g. an icon geometry) by its key.</summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public static readonly ResourceKeyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current!.TryFindResource(key, out var resource) ? resource : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>#RRGGBB text to a brush; null or bad text gives a transparent brush.</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public static readonly HexBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex && Avalonia.Media.Color.TryParse(hex, out var c)
            ? new Avalonia.Media.SolidColorBrush(c)
            : Avalonia.Media.Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Picks one of two texts, given as "whenTrue|whenFalse" in the parameter.</summary>
public sealed class BoolTextConverter : IValueConverter
{
    public static readonly BoolTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "|").Split('|');
        return value is true ? parts[0] : parts.ElementAtOrDefault(1);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Picks one of two application resources, given as "whenTrue|whenFalse" keys in the parameter.</summary>
public sealed class BoolResourceConverter : IValueConverter
{
    public static readonly BoolResourceConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ResourceKeyConverter.Instance.Convert(BoolTextConverter.Instance.Convert(value, targetType, parameter, culture),
            targetType, null, culture);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
