using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Oadm.Plugins.Vision.Client;

/// <summary>A node kind's icon in its colour (<see cref="NodeLook"/>): the node list and the heading of the selected node.</summary>
public sealed class NodeKindIcon : Control
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<NodeKindIcon, string?>(nameof(Kind));

    static NodeKindIcon() => AffectsRender<NodeKindIcon>(KindProperty);

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Kind is not { } kind
            || !this.TryFindResource("Icon." + NodeLook.IconKey(kind), ActualThemeVariant, out var value) || value is not Avalonia.Media.Geometry geometry)
        {
            return;
        }

        var brush = new OverlayTheme(this).Brush(NodeLook.BrushKey(kind));
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var scale = size / 24.0;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2)))
        {
            context.DrawGeometry(null, new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }
}
