using System.Globalization;

using Avalonia.Controls;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The theme's brushes, numbers and fonts as a control finds them (dark or light), for what is drawn in code: a node canvas
/// (<see cref="NodeCanvasPainter"/>), an overlay on a picture.
/// </summary>
public class CanvasTheme(Control control)
{
    private readonly Control _control = control ?? throw new ArgumentNullException(nameof(control));

    /// <summary>The theme's brush of <paramref name="key"/> (for example <c>Oadm.Accent</c>), grey when the theme has none.</summary>
    public IBrush Brush(string key) => Find(key) as IBrush ?? Brushes.Gray;

    /// <summary>The theme's number of <paramref name="key"/> (a font size, a radius), else <paramref name="fallback"/>.</summary>
    public double Number(string key, double fallback) => Find(key) is double value ? value : fallback;

    /// <summary>The theme's font family of <paramref name="key"/> at <paramref name="weight"/>, else the default font.</summary>
    public Typeface Font(string key, FontWeight weight = FontWeight.Normal) => new(Find(key) as FontFamily ?? FontFamily.Default, FontStyle.Normal, weight);

    /// <summary>The theme's small text (11 px), e.g. the tags and labels on a picture.</summary>
    public FormattedText Small(string text, IBrush brush, string font = "Oadm.FontFamily") =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font(font), Number("Oadm.FontSizeSmall", 11), brush);

    /// <summary>The theme's body text (12 px).</summary>
    public FormattedText Body(string text, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font("Oadm.FontFamily", weight), Number("Oadm.FontSizeBody", 12), brush);

    /// <summary>A brush of the same colour at <paramref name="opacity"/>.</summary>
    public static IBrush Tint(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : brush;

    private object? Find(string key) => _control.TryFindResource(key, _control.ActualThemeVariant, out var value) ? value : null;
}
