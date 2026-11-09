using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Oadm.Plugins.Vision.Client;

/// <summary>
/// The drawing of the Import model wizard's first step (docs/spec/vision-center.md "Import model"): a camera picture, your
/// model, the objects it finds with their boxes, in the style of the settings' drawings. Colours from the theme.
/// </summary>
public sealed class ModelImportDrawing : Control
{
    private const double W = 520, H = 150, PictureW = 150, PictureH = PictureW * 9 / 16, Top = 18;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Min(W, availableSize.Width) : W;
        return new Size(width, width * H / W);
    }

    public override void Render(DrawingContext context)
    {
        var scale = Math.Min(Bounds.Width / W, Bounds.Height / H);
        if (scale <= 0)
        {
            return;
        }

        var paint = new ModelSettingDiagram.Paint(this);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            // The camera picture.
            var picture = new Rect(0, Top, PictureW, PictureH);
            ModelSettingDiagram.DrawScene(context, paint, picture);
            context.DrawRectangle(null, new Pen(paint.Line, 1), picture);
            Caption(context, paint, "Camera picture", picture);

            // Your model.
            var model = new Rect(PictureW + 50, Top + 10, W - (2 * PictureW) - 100, PictureH - 20);
            context.DrawRectangle(paint.Zone, new Pen(paint.Accent, 1.5), model, 6, 6);
            var name = paint.Text("your-model.onnx", paint.Accent, bold: true);
            context.DrawText(name, new Point(model.X + ((model.Width - name.Width) / 2), model.Center.Y - name.Height));
            var what = paint.Text("Your model", paint.Secondary);
            context.DrawText(what, new Point(model.X + ((model.Width - what.Width) / 2), model.Center.Y + 2));
            Arrow(context, paint, picture.Right + 8, model.X - 8, picture.Center.Y);

            // The objects it finds.
            var found = new Rect(W - PictureW, Top, PictureW, PictureH);
            ModelSettingDiagram.DrawScene(context, paint, found);
            context.DrawRectangle(null, new Pen(paint.Line, 1), found);
            var sx = PictureW / 16;
            var sy = PictureH / 9;
            Box(context, paint, new Rect(found.X + (sx * 3.9), found.Y + (sy * 2.5), sx * 2.2, sy * 5), "person 92 %");
            Caption(context, paint, "Objects found", found);
            Arrow(context, paint, model.Right + 8, found.X - 8, picture.Center.Y);
        }
    }

    private static void Caption(DrawingContext context, ModelSettingDiagram.Paint paint, string text, Rect under)
    {
        var caption = paint.Text(text, paint.Secondary);
        context.DrawText(caption, new Point(under.X + ((under.Width - caption.Width) / 2), under.Bottom + 10));
    }

    private static void Arrow(DrawingContext context, ModelSettingDiagram.Paint paint, double from, double to, double y)
    {
        context.DrawLine(new Pen(paint.Secondary, 1.2), new Point(from, y), new Point(to - 4, y));
        context.DrawGeometry(paint.Secondary, null, ModelSettingDiagram.Arrow(to, y));
    }

    private static void Box(DrawingContext context, ModelSettingDiagram.Paint paint, Rect box, string label)
    {
        context.DrawRectangle(null, new Pen(paint.Accent, 1.5), box);
        var text = paint.Text(label, paint.Accent, bold: true);
        var tag = new Rect(box.X, box.Y - text.Height - 2, text.Width + 6, text.Height + 2);
        context.DrawRectangle(paint.Pad, null, tag, 2, 2);
        context.DrawText(text, new Point(tag.X + 3, tag.Y + 1));
    }
}
