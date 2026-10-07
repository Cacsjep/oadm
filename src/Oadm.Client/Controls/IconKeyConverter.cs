using System.Globalization;

using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Oadm.Client.Controls;

/// <summary>Maps a plugin or navigation icon key ("restart") to the theme geometry "Icon.restart"; unknown keys get the plugin icon.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public static IconKeyConverter Instance { get; } = new();

    /// <summary>Same lookup as the shared <see cref="Oadm.Sdk.Client.Controls.ToolbarButton.ResolveIcon"/>.</summary>
    public static Geometry? Resolve(string? key) => Oadm.Sdk.Client.Controls.ToolbarButton.ResolveIcon(key);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Resolve(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
