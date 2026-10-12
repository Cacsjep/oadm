using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Oadm.Client.LiveView;

/// <summary>
/// PTZ over the live picture: a circle with a cross in the middle. Dragging from the circle moves the camera (speed by the
/// distance from the center, the reach is half the picture), the mouse wheel zooms, a click elsewhere centers the camera on
/// that point. The picture is placed like an <see cref="Image"/> with <see cref="Stretch.Uniform"/> of <see cref="PictureSize"/>.
/// </summary>
public sealed class PtzOverlay : Control
{
    public static readonly StyledProperty<PtzViewModel?> PtzProperty = AvaloniaProperty.Register<PtzOverlay, PtzViewModel?>(nameof(Ptz));

    public static readonly StyledProperty<Size> PictureSizeProperty = AvaloniaProperty.Register<PtzOverlay, Size>(nameof(PictureSize));

    /// <summary>Radius of the circle in the middle.</summary>
    public const double RingRadius = 34;

    /// <summary>A press that moves less than this is a click (center), not a drag.</summary>
    private const double ClickSlop = 4;

    private Point? _press;
    private Point? _pointer;
    private bool _joystick;

    static PtzOverlay()
    {
        AffectsRender<PtzOverlay>(PictureSizeProperty);
    }

    public PtzOverlay()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public PtzViewModel? Ptz { get => GetValue(PtzProperty); set => SetValue(PtzProperty, value); }

    public Size PictureSize { get => GetValue(PictureSizeProperty); set => SetValue(PictureSizeProperty, value); }

    /// <summary>The drag offset as a share of the reach (half the shorter picture side), clamped to the unit circle.</summary>
    public static Vector DragVector(Point center, Point position, Rect picture)
    {
        var reach = Math.Max(1, Math.Min(picture.Width, picture.Height) / 2);
        var v = (position - center) / reach;
        var length = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        return length > 1 ? v / length : v;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // the whole picture takes the pointer
        var picture = FocusAreaOverlay.PictureRect(Bounds.Size, PictureSize);
        var center = picture.Center;
        var accent = Brush("Oadm.AccentBrush", Colors.MediumPurple);
        var pen = new Pen(accent, _joystick ? 2.5 : 1.5);
        context.DrawEllipse(null, pen, center, RingRadius, RingRadius);
        context.DrawLine(pen, center - new Vector(6, 0), center + new Vector(6, 0));
        context.DrawLine(pen, center - new Vector(0, 6), center + new Vector(0, 6));
        if (_joystick && _pointer is { } p)
        {
            context.DrawLine(new Pen(accent, 2), center, p);
            context.DrawEllipse(accent, null, p, 5, 5);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Only the left button starts, and only once: another button or a second finger during a drag never takes over.
        if (_press is not null || e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed || Ptz is not { } ptz)
        {
            return;
        }

        var point = e.GetPosition(this);
        var center = FocusAreaOverlay.PictureRect(Bounds.Size, PictureSize).Center;
        _press = point;
        _pointer = point;
        _joystick = ptz.CanMove && Distance(point, center) <= RingRadius * 1.3;
        if (_joystick)
        {
            ptz.BeginDrag();
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_press is null || Ptz is not { } ptz)
        {
            return;
        }

        _pointer = e.GetPosition(this);
        if (_joystick)
        {
            var picture = FocusAreaOverlay.PictureRect(Bounds.Size, PictureSize);
            var v = DragVector(picture.Center, _pointer.Value, picture);
            ptz.DragTo(v.X, v.Y);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_press is not { } press || e.InitialPressMouseButton != MouseButton.Left || Ptz is not { } ptz)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (_joystick)
        {
            ptz.EndDrag();
        }
        else if (Distance(point, press) <= ClickSlop)
        {
            var picture = FocusAreaOverlay.PictureRect(Bounds.Size, PictureSize);
            if (picture.Contains(point))
            {
                ptz.CenterAt((point.X - picture.X) / picture.Width, (point.Y - picture.Y) / picture.Height);
            }
        }

        _press = null;
        _pointer = null;
        _joystick = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_joystick)
        {
            Ptz?.EndDrag();
        }

        _press = null;
        _pointer = null;
        _joystick = false;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is WindowBase window)
        {
            window.Deactivated += OnWindowDeactivated;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is WindowBase window)
        {
            window.Deactivated -= OnWindowDeactivated;
        }

        EndJoystick();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && !IsVisible)
        {
            EndJoystick();
        }
    }

    /// <summary>Another window took the focus (Alt+Tab, a message): a drag stops, the release may never come here.</summary>
    private void OnWindowDeactivated(object? sender, EventArgs e) => EndJoystick();

    private void EndJoystick()
    {
        if (_joystick)
        {
            Ptz?.EndDrag();
        }

        _press = null;
        _pointer = null;
        _joystick = false;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Ptz?.Wheel(e.Delta.Y);
        e.Handled = true;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private IBrush Brush(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : new SolidColorBrush(fallback);
}
