using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

using Path = Avalonia.Controls.Shapes.Path;

namespace Oadm.Client.Controls;

/// <summary>
/// The OADM mark of the start splash with the intro of the support portal: the mark is cut into <see cref="Bands"/>
/// horizontal bands that meet exactly; each band slides in from alternating sides (14 % of the width) out of a blur, one
/// after another; while it rests the whole mark stands in for the bands (no seams); on the way out the bands slide off
/// the other way blurring, in reverse order. Colors from the theme (the mark in the primary text color, the lens in the
/// brand color). Pure view animation: <see cref="ShowIntroAt"/>, <see cref="ShowRest"/> and <see cref="ShowExitAt"/>
/// draw one frame for a given time, <see cref="PlayIntroAsync"/> and <see cref="PlayExitAsync"/> run them on the
/// window's frames.
/// </summary>
public sealed class SplashMark : Panel
{
    /// <summary>The mark's path data (the seven hexagons of <c>mark.svg</c>, view box 244.6 x 234.4).</summary>
    public const string MarkPathData =
        "M199.19,76.28 Q204.53,73.20 209.86,76.28 L237.30,92.12 Q242.63,95.20 242.63,101.36 L242.63,133.04 Q242.63,139.20 237.30,142.28 L209.86,158.12 Q204.53,161.20 199.19,158.12 L171.76,142.28 Q166.42,139.20 166.42,133.04 L166.42,101.36 Q166.42,95.20 171.76,92.12 Z " +
        "M158.09,147.47 Q163.42,144.39 168.76,147.47 L196.19,163.31 Q201.53,166.39 201.53,172.55 L201.53,204.23 Q201.53,210.39 196.19,213.47 L168.76,229.31 Q163.42,232.39 158.09,229.31 L130.65,213.47 Q125.32,210.39 125.32,204.23 L125.32,172.55 Q125.32,166.39 130.65,163.31 Z " +
        "M75.88,147.47 Q81.21,144.39 86.54,147.47 L113.98,163.31 Q119.32,166.39 119.32,172.55 L119.32,204.23 Q119.32,210.39 113.98,213.47 L86.54,229.31 Q81.21,232.39 75.88,229.31 L48.44,213.47 Q43.11,210.39 43.11,204.23 L43.11,172.55 Q43.11,166.39 48.44,163.31 Z " +
        "M34.77,76.28 Q40.11,73.20 45.44,76.28 L72.88,92.12 Q78.21,95.20 78.21,101.36 L78.21,133.04 Q78.21,139.20 72.88,142.28 L45.44,158.12 Q40.11,161.20 34.77,158.12 L7.33,142.28 Q2.00,139.20 2.00,133.04 L2.00,101.36 Q2.00,95.20 7.33,92.12 Z " +
        "M75.88,5.08 Q81.21,2.00 86.54,5.08 L113.98,20.92 Q119.32,24.00 119.32,30.16 L119.32,61.84 Q119.32,68.00 113.98,71.08 L86.54,86.92 Q81.21,90.00 75.88,86.92 L48.44,71.08 Q43.11,68.00 43.11,61.84 L43.11,30.16 Q43.11,24.00 48.44,20.92 Z " +
        "M158.09,5.08 Q163.42,2.00 168.76,5.08 L196.19,20.92 Q201.53,24.00 201.53,30.16 L201.53,61.84 Q201.53,68.00 196.19,71.08 L168.76,86.92 Q163.42,90.00 158.09,86.92 L130.65,71.08 Q125.32,68.00 125.32,61.84 L125.32,30.16 Q125.32,24.00 130.65,20.92 Z " +
        "M116.98,76.28 Q122.32,73.20 127.65,76.28 L155.09,92.12 Q160.42,95.20 160.42,101.36 L160.42,133.04 Q160.42,139.20 155.09,142.28 L127.65,158.12 Q122.32,161.20 116.98,158.12 L89.54,142.28 Q84.21,139.20 84.21,133.04 L84.21,101.36 Q84.21,95.20 89.54,92.12 Z";

    /// <summary>The view box of the mark and its lens (a circle in the middle).</summary>
    public const double ViewWidth = 244.6, ViewHeight = 234.4, LensX = 122.32, LensY = 117.20, LensRadius = 14.5;

    /// <summary>Drawn height of the mark in pixels; the width follows the view box.</summary>
    public const double MarkHeight = 168;

    public const int Bands = 7;

    /// <summary>Timing of the portal intro.</summary>
    public static readonly TimeSpan Entry = TimeSpan.FromMilliseconds(420),
        Stagger = TimeSpan.FromMilliseconds(28),
        Exit = TimeSpan.FromMilliseconds(300);

    /// <summary>The whole way in and the whole way out (the last band's delay plus its duration).</summary>
    public static readonly TimeSpan IntroDuration = Entry + Stagger * (Bands - 1),
        ExitDuration = Exit + Stagger * (Bands - 1);

    /// <summary>How far a band slides, as a share of the mark's width, and its blur at the far end.</summary>
    public const double Slide = 0.14, MaxBlur = 12;

    private static readonly SplineEasing EaseOut = new() { X1 = 0.22, Y1 = 1, X2 = 0.36, Y2 = 1 };
    private static readonly SplineEasing EaseIn = new() { X1 = 0.64, Y1 = 0, X2 = 0.78, Y2 = 0 };

    private readonly Control _whole;
    private readonly Band[] _bands = new Band[Bands];

    public SplashMark()
    {
        MarkWidth = MarkHeight * ViewWidth / ViewHeight;
        Width = MarkWidth;
        Height = MarkHeight;
        ClipToBounds = false;
        IsHitTestVisible = false;

        _whole = CreateMark();
        Children.Add(_whole);
        for (var i = 0; i < Bands; i++)
        {
            var mark = CreateMark();
            double top = MarkHeight * i / Bands, bottom = MarkHeight * (i + 1) / Bands;
            // The bands meet exactly (an overlap shows as a bright line while they move); wider than the mark, so the
            // blur is not cut at the sides.
            mark.Clip = new RectangleGeometry(new Rect(-MarkWidth * 0.2, top, MarkWidth * 1.4, bottom - top));
            var translate = new TranslateTransform();
            var blur = new BlurEffect();
            mark.RenderTransform = translate;
            mark.Effect = blur;
            Children.Add(mark);
            _bands[i] = new Band(mark, translate, blur, i % 2 == 0 ? -1 : 1);
        }

        ShowIntroAt(TimeSpan.Zero);
    }

    public double MarkWidth { get; }

    /// <summary>True while the whole mark is shown (resting), false while the bands move.</summary>
    public bool IsResting => _whole.IsVisible;

    /// <summary>The bands' opacity, top to bottom (tests).</summary>
    public IReadOnlyList<double> BandOpacities => [.. _bands.Select(b => b.Mark.IsVisible ? b.Mark.Opacity : 0)];

    /// <summary>The frame of the way in at <paramref name="time"/> after its start.</summary>
    public void ShowIntroAt(TimeSpan time)
    {
        _whole.IsVisible = false;
        for (var i = 0; i < Bands; i++)
        {
            var band = _bands[i];
            var p = EaseOut.Ease(Progress(time - Stagger * i, Entry));
            band.Show(band.Side * Slide * MarkWidth * (1 - p), p, MaxBlur * Math.Max(0, 1 - p / 0.6));
        }
    }

    /// <summary>The resting mark: the whole mark instead of the bands.</summary>
    public void ShowRest()
    {
        _whole.IsVisible = true;
        foreach (var band in _bands)
        {
            band.Mark.IsVisible = false;
        }
    }

    /// <summary>The frame of the way out at <paramref name="time"/> after its start (the last band leaves first).</summary>
    public void ShowExitAt(TimeSpan time)
    {
        _whole.IsVisible = false;
        for (var i = 0; i < Bands; i++)
        {
            var band = _bands[i];
            var p = EaseIn.Ease(Progress(time - Stagger * (Bands - 1 - i), Exit));
            band.Show(-band.Side * Slide * MarkWidth * p, 1 - p, MaxBlur * Math.Max(0, (p - 0.4) / 0.6));
        }
    }

    /// <summary>Runs the way in on the window's frames and ends resting.</summary>
    public async Task PlayIntroAsync()
    {
        await RunAsync(IntroDuration, ShowIntroAt).ConfigureAwait(true);
        ShowRest();
    }

    /// <summary>Runs the way out on the window's frames; the bands end invisible.</summary>
    public Task PlayExitAsync() => RunAsync(ExitDuration, ShowExitAt);

    private static double Progress(TimeSpan local, TimeSpan duration) =>
        Math.Clamp(local.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);

    private Task RunAsync(TimeSpan duration, Action<TimeSpan> frame)
    {
        frame(TimeSpan.Zero);
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            frame(duration);
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? start = null;
        void Tick(TimeSpan now)
        {
            start ??= now;
            var elapsed = now - start.Value;
            if (elapsed >= duration)
            {
                frame(duration);
                done.TrySetResult();
                return;
            }

            frame(elapsed);
            top.RequestAnimationFrame(Tick);
        }

        top.RequestAnimationFrame(Tick);
        return done.Task;
    }

    private Viewbox CreateMark()
    {
        var path = new Path { Data = Geometry.Parse(MarkPathData) };
        path.Bind(Shape.FillProperty, this.GetResourceObservable("Oadm.TextPrimaryBrush"));
        var lens = new Ellipse { Width = LensRadius * 2, Height = LensRadius * 2 };
        lens.Bind(Shape.FillProperty, this.GetResourceObservable("Oadm.BrandBrush"));
        Canvas.SetLeft(lens, LensX - LensRadius);
        Canvas.SetTop(lens, LensY - LensRadius);
        return new Viewbox
        {
            Width = MarkWidth,
            Height = MarkHeight,
            Stretch = Stretch.Uniform,
            ClipToBounds = false,
            Child = new Canvas { Width = ViewWidth, Height = ViewHeight, Children = { path, lens } },
        };
    }

    private sealed record Band(Viewbox Mark, TranslateTransform Translate, BlurEffect Blur, int Side)
    {
        public void Show(double x, double opacity, double blur)
        {
            Mark.IsVisible = opacity > 0;
            Mark.Opacity = opacity;
            Translate.X = x;
            Blur.Radius = blur;
            Mark.Effect = blur > 0.01 ? Blur : null;
        }
    }
}
