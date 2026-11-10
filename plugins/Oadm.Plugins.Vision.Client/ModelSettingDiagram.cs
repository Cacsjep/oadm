using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Client;

/// <summary>Which setting of a model a <see cref="ModelSettingDiagram"/> explains.</summary>
public enum ModelSetting
{
    /// <summary>Format: how the outputs are read, one row per format of the model's kind.</summary>
    Format,

    /// <summary>Fit: how the camera's wide picture goes into the model's input, one panel per fit.</summary>
    Fit,

    /// <summary>Scaling: what the model gets for one pixel, one row per scaling.</summary>
    Scaling,
}

/// <summary>
/// The drawing in the info icon of a model setting (docs/spec/vision-center.md "Models"), like the pulse diagrams of the event
/// rule limits: every choice of the setting side by side, the chosen one in the accent colour, the others dimmed. Follows the
/// chosen value; colours from the theme.
/// </summary>
public sealed class ModelSettingDiagram : Control
{
    public static readonly StyledProperty<ModelSetting> SettingProperty =
        AvaloniaProperty.Register<ModelSettingDiagram, ModelSetting>(nameof(Setting));

    /// <summary>The chosen value: a <see cref="ModelFormat"/>, <see cref="ModelFit"/> or <see cref="ModelScaling"/>.</summary>
    public static readonly StyledProperty<object?> ValueProperty =
        AvaloniaProperty.Register<ModelSettingDiagram, object?>(nameof(Value));

    /// <summary>The model's kind: the formats it can take.</summary>
    public static readonly StyledProperty<ModelKind> KindProperty =
        AvaloniaProperty.Register<ModelSettingDiagram, ModelKind>(nameof(Kind));

    private const double W = 440;

    static ModelSettingDiagram() => AffectsRender<ModelSettingDiagram>(SettingProperty, ValueProperty, KindProperty);

    public ModelSetting Setting
    {
        get => GetValue(SettingProperty);
        set => SetValue(SettingProperty, value);
    }

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public ModelKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>The formats of a kind with what they fit, in the order of the Settings select.</summary>
    internal static IReadOnlyList<(ModelFormat Format, string Name, string Models)> FormatsOf(ModelKind kind) => kind == ModelKind.Classification
        ? [(ModelFormat.Classification, "Classification", "One class for each picture or object")]
        :
        [
            (ModelFormat.Yolo, "YOLO", "YOLOv5, YOLOv8, YOLO11, YOLO12"),
            (ModelFormat.YoloWithSuppression, "YOLO with suppression", "YOLOv10, YOLO26 end to end"),
            (ModelFormat.Detr, "DETR", "Facebook's DETR, Hugging Face DETR"),
            (ModelFormat.RfDetr, "RF-DETR", "Roboflow's RF-DETR"),
            (ModelFormat.DetrWithLabels, "DETR with labels", "Boxes, scores and labels apart"),
            (ModelFormat.Yolox, "YOLOX", "Megvii's YOLOX"),
            (ModelFormat.RtDetr, "RT-DETR", "RT-DETR, D-FINE of Hugging Face"),
        ];

    /// <summary>The three numbers a model gets for the orange pixel (255, 128, 0) with each scaling, as drawn.</summary>
    internal static IReadOnlyList<(string Channel, string Value)> PixelOf(ModelScaling scaling)
    {
        static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        double[] rgb = [255, 128, 0];
        return scaling switch
        {
            ModelScaling.ImageNet =>
            [
                ("R", F(((rgb[0] / 255) - 0.485) / 0.229)), ("G", F(((rgb[1] / 255) - 0.456) / 0.224)), ("B", F(((rgb[2] / 255) - 0.406) / 0.225)),
            ],
            ModelScaling.ZeroTo255Bgr => [("B", "0"), ("G", "128"), ("R", "255")],
            _ => [("R", F(rgb[0] / 255)), ("G", F(rgb[1] / 255)), ("B", F(rgb[2] / 255))],
        };
    }

    private double Height0 => Setting switch
    {
        ModelSetting.Fit => 150,
        ModelSetting.Scaling => 112,
        _ => (FormatsOf(Kind).Count * 22) + 4,
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Min(W, availableSize.Width) : W;
        return new Size(width, width * Height0 / W);
    }

    public override void Render(DrawingContext context)
    {
        var scale = Math.Min(Bounds.Width / W, Bounds.Height / Height0);
        if (scale <= 0)
        {
            return;
        }

        var paint = new Paint(this);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            switch (Setting)
            {
                case ModelSetting.Fit:
                    DrawFits(context, paint, Value as ModelFit? ?? ModelFit.Letterbox);
                    break;
                case ModelSetting.Scaling:
                    DrawScalings(context, paint, Value as ModelScaling? ?? ModelScaling.ZeroToOne);
                    break;
                default:
                    DrawFormats(context, paint, Value as ModelFormat? ?? ModelFormat.Yolo);
                    break;
            }
        }
    }

    // ---- Fit: the camera's 16:9 picture in the model's square input, three ways ----

    private static void DrawFits(DrawingContext context, Paint paint, ModelFit chosen)
    {
        (ModelFit Fit, string Name)[] fits = [(ModelFit.Letterbox, "Letterbox"), (ModelFit.Stretch, "Stretch"), (ModelFit.KeepShapePad, "Keep shape and pad")];
        const double side = 104, gap = (W - (3 * side)) / 2, top = 8;
        for (var i = 0; i < fits.Length; i++)
        {
            var (fit, name) = fits[i];
            var on = fit == chosen;
            using var dim = on ? default(DrawingContext.PushedState?) : context.PushOpacity(0.45);
            var box = new Rect(i * (side + gap), top, side, side);
            context.DrawRectangle(paint.Pad, null, box);
            var picture = fit switch
            {
                ModelFit.Stretch => box,
                ModelFit.KeepShapePad => new Rect(box.X, box.Y, side, side * 9 / 16),
                _ => new Rect(box.X, box.Y + ((side - (side * 9 / 16)) / 2), side, side * 9 / 16),
            };
            DrawScene(context, paint, picture);
            context.DrawRectangle(null, new Pen(on ? paint.Accent : paint.Line, on ? 2 : 1), box);
            var label = paint.Text(name, on ? paint.Accent : paint.Secondary, bold: on);
            context.DrawText(label, new Point(box.X + ((side - label.Width) / 2), box.Bottom + 10));
        }
    }

    /// <summary>A camera picture: sky, ground, the sun (a circle in the picture's shape) and a person.</summary>
    internal static void DrawScene(DrawingContext context, Paint paint, Rect r)
    {
        using var clip = context.PushClip(r);
        context.DrawRectangle(paint.Sky, null, r);
        context.DrawRectangle(paint.Ground, null, new Rect(r.X, r.Y + (r.Height * 0.68), r.Width, r.Height * 0.32));

        // Sizes relative to a 16:9 picture: a stretched picture shows the circle tall and the person wide.
        var sx = r.Width / 16;
        var sy = r.Height / 9;
        context.DrawEllipse(paint.Sun, null, new Point(r.X + (sx * 12.5), r.Y + (sy * 2.4)), sx * 1.3, sy * 1.3);
        var person = paint.Person;
        context.DrawEllipse(person, null, new Point(r.X + (sx * 5), r.Y + (sy * 3.4)), sx * 0.55, sy * 0.55);
        context.DrawRectangle(person, null, new Rect(r.X + (sx * 4.35), r.Y + (sy * 4.05), sx * 1.3, sy * 3.2), 2, 2);
    }

    // ---- Scaling: one orange pixel, the three numbers the model gets ----

    private static void DrawScalings(DrawingContext context, Paint paint, ModelScaling chosen)
    {
        (ModelScaling Scaling, string Name)[] rows = [(ModelScaling.ZeroToOne, "0 to 1"), (ModelScaling.ImageNet, "ImageNet"), (ModelScaling.ZeroTo255Bgr, "0 to 255, BGR")];
        const double rowHeight = 34, swatch = 20;
        var pixel = paint.Text("Pixel 255, 128, 0", paint.Secondary);
        context.DrawText(pixel, new Point(0, 0));
        for (var i = 0; i < rows.Length; i++)
        {
            var (scaling, name) = rows[i];
            var on = scaling == chosen;
            var y = 22 + (i * rowHeight);
            using var dim = on ? default(DrawingContext.PushedState?) : context.PushOpacity(0.45);
            if (on)
            {
                context.DrawRectangle(paint.Zone, null, new Rect(0, y - 4, W, rowHeight - 6), 4, 4);
            }

            context.DrawText(paint.Text(name, on ? paint.Accent : paint.Secondary, bold: on), new Point(8, y + 3));
            context.DrawRectangle(paint.Orange, null, new Rect(130, y + 1, swatch, swatch), 3, 3);
            var arrow = new Pen(paint.Secondary, 1.2);
            context.DrawLine(arrow, new Point(158, y + 11), new Point(184, y + 11));
            context.DrawGeometry(paint.Secondary, null, Arrow(188, y + 11));
            var x = 200.0;
            foreach (var (channel, value) in PixelOf(scaling))
            {
                var brush = channel switch { "R" => paint.Red, "G" => paint.Green, _ => paint.Blue };
                var text = paint.Text($"{channel} {value}", brush, bold: true);
                context.DrawText(text, new Point(x, y + 3));
                x += 78;
            }
        }
    }

    // ---- Format: one row per format with the models it reads ----

    private void DrawFormats(DrawingContext context, Paint paint, ModelFormat chosen)
    {
        var y = 2.0;
        foreach (var (format, name, models) in FormatsOf(Kind))
        {
            var on = format == chosen;
            using var dim = on ? default(DrawingContext.PushedState?) : context.PushOpacity(0.55);
            if (on)
            {
                context.DrawRectangle(paint.Zone, null, new Rect(0, y, W, 20), 4, 4);
            }

            context.DrawText(paint.Text(name, on ? paint.Accent : paint.Primary, bold: on), new Point(8, y + 2));
            context.DrawText(paint.Text(models, paint.Secondary), new Point(170, y + 2));
            y += 22;
        }
    }

    internal static StreamGeometry Arrow(double x, double y)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(x, y), true);
        g.LineTo(new Point(x - 6, y - 3.5));
        g.LineTo(new Point(x - 6, y + 3.5));
        g.EndFigure(true);
        return geometry;
    }

    /// <summary>The theme's brushes and font for the control's appearance.</summary>
    internal sealed class Paint
    {
        private readonly Control _owner;
        private readonly Typeface _typeface;
        private readonly Typeface _bold;
        private readonly double _size;

        public Paint(Control owner)
        {
            _owner = owner;
            var family = Find("Oadm.FontFamily") as FontFamily ?? FontFamily.Default;
            _typeface = new Typeface(family, FontStyle.Normal, FontWeight.Normal);
            _bold = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold);
            _size = Find("Oadm.FontSizeBody") is double body ? body - 1 : 11;
            Line = Brush("Oadm.InputBorderBrush");
            Accent = Brush("Oadm.AccentBrush");
            Zone = Brush("Oadm.StatusAccentBackgroundBrush");
            Primary = Brush("Oadm.TextPrimaryBrush");
            Secondary = Brush("Oadm.TextSecondaryBrush");
            Pad = Brush("Oadm.InputBackgroundBrush");
            Red = Brush("Oadm.StatusErrorBrush");
            Green = Brush("Oadm.StatusOkBrush");
            Blue = Brush("Oadm.TealBrush");
        }

        public IBrush Line { get; }

        public IBrush Accent { get; }

        public IBrush Zone { get; }

        public IBrush Primary { get; }

        public IBrush Secondary { get; }

        public IBrush Pad { get; }

        public IBrush Red { get; }

        public IBrush Green { get; }

        public IBrush Blue { get; }

        // The picture's own colours: the same in both appearances (it is a camera picture).
        public IBrush Sky { get; } = new SolidColorBrush(Color.FromRgb(0x5B, 0x8F, 0xC9));

        public IBrush Ground { get; } = new SolidColorBrush(Color.FromRgb(0x4E, 0x7D, 0x4A));

        public IBrush Sun { get; } = new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x4C));

        public IBrush Person { get; } = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x33));

        public IBrush Orange { get; } = new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x00));

        public FormattedText Text(string text, IBrush brush, bool bold = false) =>
            new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? _bold : _typeface, _size, brush);

        private object? Find(string key) => _owner.TryFindResource(key, _owner.ActualThemeVariant, out var value) ? value : null;

        private IBrush Brush(string key) => Find(key) as IBrush ?? Brushes.Gray;
    }
}
