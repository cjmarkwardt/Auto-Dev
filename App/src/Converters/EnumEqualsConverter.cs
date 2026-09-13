using System.Globalization;
using Avalonia.Data.Converters;

namespace AutoDev.Converters;

/// <summary>Compares a bound enum value against ConverterParameter (by name) - used to drive IsVisible for "which schedule kind is selected" sections.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
