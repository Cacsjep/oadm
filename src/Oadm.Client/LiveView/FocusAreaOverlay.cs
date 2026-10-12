using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Oadm.Client.LiveView;

/// <summary>
/// The autofocus area over the live picture: the picture outside the area is dimmed, the area has an accent frame, and a
/// drag draws a new one. <see cref="Area"/> is in fractions of the picture (0..1); the picture is placed like an
/// <see cref="Image"/> with <see cref="Stretch.Uniform"/> of <see cref="PictureSize"/>, so the frame stays on the
/// picture whatever the panel's size.
/// </summary>
public sealed class FocusAreaOverlay : Control
{
    public static readonly StyledProperty<Rect> AreaProperty =
        AvaloniaProperty.Register<FocusAreaOverlay, Rect>(nameof(Area), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<Size> PictureSizeProperty = AvaloniaProperty.Register<FocusAreaOverlay, Size>(nameof(PictureSize));

    /// <summary>Smallest side of a drawn area, as a fraction of the picture.</summary>
    private const double MinSide = 0.05;

    private Point? _dragStart;

    static FocusAreaOverlay()
    {
        AffectsRender<FocusAreaOverlay>(AreaProperty, PictureSizeProperty);
    }

    public FocusAreaOverlay()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public Rect Area { get => GetValue(AreaProperty); set => SetValue(AreaProperty, value); }

    public Size PictureSize { get => GetValue(PictureSizeProperty); set => SetValue(PictureSizeProperty, value); }

    /// <summary>Where the picture is drawn inside the control (uniform fit, centered).</summary>
    public static Rect PictureRect(Size bounds, Size picture)
    {
        if (picture.Width <= 0 || picture.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return new Rect(bounds);
        }

        var scale = Math.Min(bounds.Width / picture.Width, bounds.Height / picture.Height);
        var size = new Size(picture.Width * scale, picture.Height * scale);
        return new Rect(new Point((bounds.Width - size.Width) / 2, (bounds.Height - size.Height) / 2), size);
    }

    /// <summary>The normalized area between two points of the control, clamped into the picture.</summary>
    public static Rect AreaBetween(Point a, Point b, Rect picture)
    {
        static double Clamp01(double v) => Math.Clamp(v, 0, 1);
        double x1 = Clamp01((Math.Min(a.X, b.X) - picture.X) / picture.Width), x2 = Clamp01((Math.Max(a.X, b.X) - picture.X) / picture.Width);
        double y1 = Clamp01((Math.Min(a.Y, b.Y) - picture.Y) / picture.Height), y2 = Clamp01((Math.Max(a.Y, b.Y) - picture.Y) / picture.Height);
        double w = Math.Max(MinSide, x2 - x1), h = Math.Max(MinSide, y2 - y1);
        x1 = Math.Min(x1, 1 - w);
        y1 = Math.Min(y1, 1 - h);
        return new Rect(x1, y1, w, h);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var picture = PictureRect(Bounds.Size, PictureSize);
        var area = new Rect(picture.X + Area.X * picture.Width, picture.Y + Area.Y * picture.Height, Area.Width * picture.Width, Area.Height * picture.Height);
        var dim = Brush("Oadm.OverlayBrush", Color.FromArgb(0x99, 0, 0, 0));
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // the whole control takes the drag
        // Dim the picture around the area (four bands), then the frame.
        context.FillRectangle(dim, new Rect(picture.X, picture.Y, picture.Width, Math.Max(0, area.Y - picture.Y)));
        context.FillRectangle(dim, new Rect(picture.X, area.Bottom, picture.Width, Math.Max(0, picture.Bottom - area.Bottom)));
        context.FillRectangle(dim, new Rect(picture.X, area.Y, Math.Max(0, area.X - picture.X), area.Height));
        context.FillRectangle(dim, new Rect(area.Right, area.Y, Math.Max(0, picture.Right - area.Right), area.Height));
        context.DrawRectangle(null, new Pen(Brush("Oadm.AccentBrush", Colors.MediumPurple), 2), area);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragStart = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is { } start)
        {
            Area = AreaBetween(start, e.GetPosition(this), PictureRect(Bounds.Size, PictureSize));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is { } start)
        {
            Area = AreaBetween(start, e.GetPosition(this), PictureRect(Bounds.Size, PictureSize));
            _dragStart = null;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragStart = null;
    }

    private IBrush Brush(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : new SolidColorBrush(fallback);
}
