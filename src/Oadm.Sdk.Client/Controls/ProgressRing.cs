using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// An indeterminate progress ring: a faint circle with an accent arc that turns (the turning is the theme's style
/// <c>ui|ProgressRing</c>). Used where something loads without a known progress, e.g. a live picture that connects.
/// </summary>
public sealed class ProgressRing : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(Track));

    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Thickness), 3);

    static ProgressRing()
    {
        AffectsRender<ProgressRing>(ForegroundProperty, TrackProperty, ThicknessProperty);
    }

    public ProgressRing()
    {
        Width = 40;
        Height = 40;
        RenderTransformOrigin = RelativePoint.Center;
        RenderTransform = new RotateTransform();
    }

    /// <summary>The turning arc (theme: the accent).</summary>
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>The faint full circle behind the arc.</summary>
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    /// <summary>The line width of the ring.</summary>
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= Thickness * 2)
        {
            return;
        }

        var radius = (size - Thickness) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (Track is { } track)
        {
            context.DrawEllipse(null, new Pen(track, Thickness), center, radius, radius);
        }

        if (Foreground is not { } arc)
        {
            return;
        }

        // A quarter circle from the top, clockwise.
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius, center.Y);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(arc, Thickness, lineCap: PenLineCap.Round), geometry);
    }
}
