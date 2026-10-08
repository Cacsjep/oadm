using System.Globalization;

using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

using Oadm.Contracts.V1;

namespace Oadm.Client.Tags;

/// <summary>The fixed tag palette: eight named colors, each a theme brush <c>Oadm.Tag.&lt;Name&gt;Brush</c>.</summary>
public static class TagPalette
{
    /// <summary>The palette in picker order.</summary>
    public static IReadOnlyList<TagColor> Colors { get; } =
        [TagColor.Blue, TagColor.Green, TagColor.Teal, TagColor.Amber, TagColor.Orange, TagColor.Red, TagColor.Pink, TagColor.Violet];

    /// <summary>"Blue"; "No color" for a tag without a definition.</summary>
    public static string Name(TagColor color) => color switch
    {
        TagColor.Blue => "Blue",
        TagColor.Green => "Green",
        TagColor.Teal => "Teal",
        TagColor.Amber => "Amber",
        TagColor.Orange => "Orange",
        TagColor.Red => "Red",
        TagColor.Pink => "Pink",
        TagColor.Violet => "Violet",
        _ => "No color",
    };

    /// <summary>Theme resource of a color: "Oadm.Tag.BlueBrush"; undefined tags use "Oadm.Tag.NeutralBrush".</summary>
    public static string ResourceKey(TagColor color) =>
        color == TagColor.Unspecified || !Colors.Contains(color) ? "Oadm.Tag.NeutralBrush" : $"Oadm.Tag.{Name(color)}Brush";

    /// <summary>The color a new tag gets by default: the palette in order, by the number of tags there are.</summary>
    public static TagColor Next(int existingTags) => Colors[Math.Max(0, existingTags) % Colors.Count];

    /// <summary>The theme brush of a color (null without an application, e.g. view model tests).</summary>
    public static IBrush? Brush(TagColor color) =>
        Application.Current is { } app && app.TryGetResource(ResourceKey(color), ThemeVariant.Dark, out object? value) && value is IBrush brush
            ? brush
            : null;
}

/// <summary>Binds a <see cref="TagColor"/> to its theme brush (chip text, dots, group headers).</summary>
public sealed class TagColorBrushConverter : IValueConverter
{
    public static TagColorBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        TagPalette.Brush(value is TagColor color ? color : TagColor.Unspecified);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
