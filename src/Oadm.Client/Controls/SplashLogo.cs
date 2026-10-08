using System.Globalization;
using System.Text;

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Styling;

using Path = Avalonia.Controls.Shapes.Path;

namespace Oadm.Client.Controls;

/// <summary>
/// The animated OADM logo of the start splash (the app icon: seven pointy-top hexagons with the lens in the middle; mockup
/// <c>mockups/loading/index.html</c>, user decision 2026-10-08). The six outer hexagons fly in one after another, each drawn as an
/// accent outline that then fills; then the center hexagon, the lens ring and the lens opening like an iris; a soft glow grows
/// behind the logo and keeps breathing, a scan line sweeps once around it and the lens blinks once. The logo is complete after
/// <see cref="CompleteAfter"/>. Starts when attached to a visual tree; colors from the theme. Transforms are animated through
/// their properties on the control (Avalonia's transform animator finds the scale, rotation or translation in RenderTransform).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The animation token is cancelled and disposed when the logo leaves the visual tree.")]
public sealed class SplashLogo : Panel
{
    /// <summary>Hexagon circumradius, gap between hexagons and the size of the logo area.</summary>
    public const double Radius = 44, Gap = 6, Size = 260; // the mockup's 320-unit logo drawn at 260 px

    /// <summary>When every part of the logo has appeared (the lens is open).</summary>
    public static readonly TimeSpan CompleteAfter = TimeSpan.FromSeconds(2.3);

    private static readonly double Distance = Math.Sqrt(3) * Radius + Gap;
    private readonly List<(Animatable Target, Animation Animation)> _animations = [];
    private CancellationTokenSource? _cts;

    public SplashLogo()
    {
        Width = Size;
        Height = Size;
        ClipToBounds = false;
    }

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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Build();
        _cts = new CancellationTokenSource();
        foreach (var (target, animation) in _animations)
        {
            _ = animation.RunAsync(target, _cts.Token);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void Build()
    {
        Children.Clear();
        _animations.Clear();

        // Glow behind the logo: grows in, then breathes.
        var glow = new Ellipse { Width = 280, Height = 280, Fill = Brush("Oadm.SplashGlowBrush"), Opacity = 0, IsHitTestVisible = false };
        Scale(glow, 0.6);
        Children.Add(glow);
        Tween(glow, OpacityProperty, 0, 1, 0.9, 1.2, new CubicEaseOut());
        TweenScale(glow, 0.6, 1, 0.9, 1.2, new CubicEaseOut());
        _animations.Add((glow, new Animation
        {
            Duration = TimeSpan.FromSeconds(2.4),
            Delay = TimeSpan.FromSeconds(2.1),
            IterationCount = IterationCount.Infinite,
            Easing = new SineEaseInOut(),
            Children = { Frame(0, OpacityProperty, 1.0), Frame(0.5, OpacityProperty, 0.7), Frame(1, OpacityProperty, 1.0) },
        }));

        var canvas = new Canvas { Width = Size, Height = Size };
        Children.Add(canvas);

        // Six outer hexagons clockwise from the top left, then the center one.
        int[] order = [240, 300, 0, 60, 120, 180];
        for (var i = 0; i < order.Length; i++)
        {
            var a = order[i] * Math.PI / 180;
            AddHex(canvas, Distance * Math.Cos(a), Distance * Math.Sin(a), 0.25 + i * 0.13);
        }

        AddHex(canvas, 0, 0, 1.15);

        // Lens ring pulls in, then the lens opens like an iris and later blinks once like a shutter.
        var ring = new Ellipse { Width = 40, Height = 40, Stroke = Brush("Oadm.BrandBrush"), StrokeThickness = 4, Opacity = 0 };
        Scale(ring, 1.8);
        Place(canvas, ring, 0, 0);
        Tween(ring, OpacityProperty, 0, 1, 1.55, 0.6, new CubicEaseOut());
        TweenScale(ring, 1.8, 1, 1.55, 0.6, new CubicEaseOut());

        var lens = new Ellipse { Width = 30, Height = 30, Fill = Brush("Oadm.BrandBrush") }; // even sizes: centered on whole pixels
        Scale(lens, 0);
        Place(canvas, lens, 0, 0);
        TweenScale(lens, 0, 1, 1.7, 0.55, new BackEaseOut());
        foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            _animations.Add((lens, new Animation
            {
                Duration = TimeSpan.FromSeconds(0.5),
                Delay = TimeSpan.FromSeconds(2.9),
                Easing = new SineEaseInOut(),
                Children = { Frame(0, property, 1.0), Frame(0.5, property, 0.35), Frame(1, property, 1.0) },
            }));
        }

        // Scan line: a short arc that sweeps once around the logo.
        var scan = new Ellipse
        {
            Width = 244,
            Height = 244,
            Stroke = Brush("Oadm.AccentHoverBrush"),
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeDashArray = new AvaloniaList<double> { 37, 370 },
            Opacity = 0,
            IsHitTestVisible = false,
        };
        scan.RenderTransform = new RotateTransform();
        Place(canvas, scan, 0, 0);
        _animations.Add((scan, new Animation
        {
            Duration = TimeSpan.FromSeconds(1.6),
            Delay = TimeSpan.FromSeconds(2.0),
            FillMode = FillMode.Both,
            Children = { Frame(0, OpacityProperty, 0.0), Frame(0.15, OpacityProperty, 0.9), Frame(0.85, OpacityProperty, 0.9), Frame(1, OpacityProperty, 0.0) },
        }));
        Tween(scan, RotateTransform.AngleProperty, 0, 360, 2.0, 1.6, new SineEaseInOut());
    }

    private void AddHex(Canvas canvas, double cx, double cy, double delay)
    {
        var geometry = StreamGeometry.Parse(HexPathData(Radius));
        var fill = new Path { Data = geometry, Fill = Brush("Oadm.TextPrimaryBrush"), Opacity = 0 };
        var outline = new Path
        {
            Data = geometry,
            Stroke = Brush("Oadm.AccentBrush"),
            StrokeThickness = 3,
            StrokeJoin = PenLineJoin.Round,
            StrokeDashArray = new AvaloniaList<double> { 110, 110 }, // in stroke widths: 330 px > the perimeter
            StrokeDashOffset = 110,
        };
        var hex = new Panel { Width = 2 * Radius, Height = 2 * Radius, Opacity = 0, Children = { fill, outline } };
        var scale = new ScaleTransform(0.4, 0.4);
        var rotation = new RotateTransform(-30);
        var translation = new TranslateTransform(cx * 0.9, cy * 0.9);
        hex.RenderTransform = new TransformGroup { Children = { scale, rotation, translation } };
        Place(canvas, hex, cx, cy);

        // Flies in from a bit further out, turning and growing; the outline draws itself, then the hexagon fills.
        _animations.Add((hex, new Animation
        {
            Duration = TimeSpan.FromSeconds(0.7),
            Delay = TimeSpan.FromSeconds(delay),
            FillMode = FillMode.Both,
            Children = { Frame(0, OpacityProperty, 0.0), Frame(0.6, OpacityProperty, 1.0), Frame(1, OpacityProperty, 1.0) },
        }));
        var fly = new BackEaseOut();
        TweenScale(hex, 0.4, 1, delay, 0.7, fly);
        Tween(hex, RotateTransform.AngleProperty, -30, 0, delay, 0.7, fly);
        Tween(hex, TranslateTransform.XProperty, cx * 0.9, 0, delay, 0.7, fly);
        Tween(hex, TranslateTransform.YProperty, cy * 0.9, 0, delay, 0.7, fly);
        Tween(outline, Shape.StrokeDashOffsetProperty, 110, 0, delay, 0.55, new CubicEaseOut());
        Tween(fill, OpacityProperty, 0, 1, delay + 0.45, 0.45, new CubicEaseOut());
        Tween(outline, OpacityProperty, 1, 0, delay + 0.45, 0.45, new CubicEaseOut());
    }

    private static void Scale(Control control, double initial) => control.RenderTransform = new ScaleTransform(initial, initial);

    /// <summary>Animates the control's <see cref="ScaleTransform"/> (the transform animator finds it in RenderTransform).</summary>
    private void TweenScale(Control control, double from, double to, double delay, double duration, Easing easing)
    {
        Tween(control, ScaleTransform.ScaleXProperty, from, to, delay, duration, easing);
        Tween(control, ScaleTransform.ScaleYProperty, from, to, delay, duration, easing);
    }

    private void Tween(Animatable target, AvaloniaProperty property, double from, double to, double delay, double duration, Easing easing) =>
        _animations.Add((target, new Animation
        {
            Duration = TimeSpan.FromSeconds(duration),
            Delay = TimeSpan.FromSeconds(delay),
            FillMode = FillMode.Both,
            Easing = easing,
            Children = { Frame(0, property, from), Frame(1, property, to) },
        }));

    private static KeyFrame Frame(double cue, AvaloniaProperty property, double value) =>
        new() { Cue = new Cue(cue), Setters = { new Setter(property, value) } };

    /// <summary>Puts a control so that its center is (cx, cy) relative to the logo center.</summary>
    private static void Place(Canvas canvas, Control control, double cx, double cy)
    {
        Canvas.SetLeft(control, Size / 2 + cx - control.Width / 2);
        Canvas.SetTop(control, Size / 2 + cy - control.Height / 2);
        canvas.Children.Add(control);
    }

    private static IBrush? Brush(string key) =>
        Application.Current?.TryGetResource(key, ThemeVariant.Dark, out var value) == true ? value as IBrush : null;

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
