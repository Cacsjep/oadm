using System.Globalization;

using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace Oadm.Client.Controls;

/// <summary>Maps a plugin or navigation icon key ("restart") to the theme geometry "Icon.restart"; unknown keys get the plugin icon.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public static IconKeyConverter Instance { get; } = new();

    public static Geometry? Resolve(string? key)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(key) && app.TryGetResource("Icon." + key, ThemeVariant.Dark, out object? value) && value is Geometry geometry)
        {
            return geometry;
        }

        return app.TryGetResource("Icon.plugin", ThemeVariant.Dark, out object? fallback) ? fallback as Geometry : null;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Resolve(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
