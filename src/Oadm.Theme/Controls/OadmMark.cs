using System.Globalization;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Oadm.Theme.Controls;

/// <summary>
/// The OADM logo without animation (the app icon: seven pointy-top hexagons with the lens in the middle, as the start
/// splash draws it at its end), scaled to the control's size; colors from the theme (<c>Oadm.LogoBrush</c>,
/// <c>Oadm.BrandBrush</c>). The OADM Site Node app shows it for the server.
/// </summary>
public sealed class OadmMark : Control
{
    /// <summary>Hexagon circumradius, gap between hexagons and the size of the drawn logo (in logo units).</summary>
    public const double Radius = 44, Gap = 6, Size = 260;

    private static readonly double Distance = Math.Sqrt(3) * Radius + Gap;
    private static readonly Geometry Hex = StreamGeometry.Parse(HexPathData(Radius));

    /// <summary>A hexagon of the logo's shape, 72 px high (vertex at the top): the frame of an icon next to the logo.</summary>
    public static Geometry Hexagon { get; } = StreamGeometry.Parse(HexPathData(36));

    /// <summary>The hexagon outline with slightly rounded corners, vertex at the top, inside a 2R x 2R box.</summary>
    public static string HexPathData(double r)
    {
        var points = new (double X, double Y)[6];
        for (var i = 0; i < 6; i++)
        {
            var a = Math.PI / 180 * (60 * i - 90);
            points[i] = (r + r * Math.Cos(a), r + r * Math.Sin(a));
        }

        const double k = 0.14;
        var d = new StringBuilder();
        for (var i = 0; i < 6; i++)
        {
            var p = points[i];
            var n = points[(i + 1) % 6];
            var prev = points[(i + 5) % 6];
            d.Append(i == 0 ? 'M' : 'L').Append(F(p.X + (prev.X - p.X) * k)).Append(',').Append(F(p.Y + (prev.Y - p.Y) * k))
             .Append(" Q").Append(F(p.X)).Append(',').Append(F(p.Y)).Append(' ').Append(F(p.X + (n.X - p.X) * k)).Append(',').Append(F(p.Y + (n.Y - p.Y) * k)).Append(' ');
        }

        return d.Append('Z').ToString();
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scale = Math.Min(Bounds.Width, Bounds.Height) / Size;
        if (scale <= 0)
            return;
        var logo = Brush("Oadm.LogoBrush");
        var brand = Brush("Oadm.BrandBrush");
        var offset = new Point((Bounds.Width - Size * scale) / 2, (Bounds.Height - Size * scale) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            var center = new Point(Size / 2, Size / 2);
            foreach (var degrees in new[] { 0, 60, 120, 180, 240, 300 })
            {
                var a = degrees * Math.PI / 180;
                DrawHex(context, logo, center + new Point(Distance * Math.Cos(a), Distance * Math.Sin(a)));
            }

            DrawHex(context, logo, center);
            context.DrawEllipse(null, new Pen(brand, 4), center, 20, 20);
            context.DrawEllipse(brand, null, center, 15, 15);
        }
    }

    private static void DrawHex(DrawingContext context, IBrush? brush, Point center)
    {
        using (context.PushTransform(Matrix.CreateTranslation(center.X - Radius, center.Y - Radius)))
            context.DrawGeometry(brush, null, Hex);
    }

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
