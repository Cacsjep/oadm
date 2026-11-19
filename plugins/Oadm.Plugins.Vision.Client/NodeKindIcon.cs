using Avalonia;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Vision.Client;

/// <summary>A node kind's icon in its colour (<see cref="NodeLook"/>): the heading of the selected node. The shared <see cref="NodeIcon"/> by kind id.</summary>
public sealed class NodeKindIcon : NodeIcon
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<NodeKindIcon, string?>(nameof(Kind));

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(NodeIcon);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty)
        {
            IconKey = Kind is { } kind ? NodeLook.IconKey(kind) : null;
            BrushKey = Kind is { } k ? NodeLook.BrushKey(k) : null;
        }
    }
}
