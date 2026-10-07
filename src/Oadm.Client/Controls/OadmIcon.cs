using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Oadm.Client.Controls;

/// <summary>
/// Monochrome outline icon. <see cref="Data"/> is a stroke geometry on a 24x24 grid (Lucide geometry),
/// scaled to the control size, so all icons share one optical size and stroke width.
/// </summary>
public sealed class OadmIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<OadmIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<OadmIcon>();

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<OadmIcon, double>(nameof(StrokeThickness), 2.0);

    static OadmIcon()
    {
        AffectsRender<OadmIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Geometry? data = Data;
        IBrush? brush = Foreground;
        if (data is null || brush is null)
        {
            return;
        }

        double size = Math.Min(Bounds.Width, Bounds.Height);
        double scale = size / 24.0;
        double dx = (Bounds.Width - size) / 2;
        double dy = (Bounds.Height - size) / 2;
        var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
        {
            context.DrawGeometry(null, pen, data);
        }
    }
}
