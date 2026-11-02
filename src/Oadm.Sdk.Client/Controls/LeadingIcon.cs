using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// An icon at the left inside one input (a <see cref="ComboBox"/> or a <see cref="TextBox"/>), like the magnifier of
/// <see cref="SearchBox"/> and with the look of the login's icons (theme class <c>fieldIcon</c>): <c>&lt;ui:LeadingIcon Icon="{StaticResource Icon.squareA}"&gt;&lt;ComboBox /&gt;&lt;/ui:LeadingIcon&gt;</c>.
/// The input gets the theme class <c>leadingIcon</c> (room for the icon); the icon stays level with the input when a
/// validation error shows below it. Decoration only: it takes no clicks.
/// </summary>
public sealed class LeadingIcon : Panel
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<LeadingIcon, Geometry?>(nameof(Icon));

    private readonly OadmIcon _icon = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
        ZIndex = 1,
    };

    public LeadingIcon()
    {
        _icon.Classes.Add("fieldIcon");
        _icon.Classes.Add("leading");
        Children.Add(_icon);
    }

    /// <summary>The icon (<c>{StaticResource Icon.…}</c>).</summary>
    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty)
        {
            _icon.Data = Icon;
        }
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        foreach (var child in Children)
        {
            if (!ReferenceEquals(child, _icon) && !child.Classes.Contains("leadingIcon"))
            {
                child.Classes.Add("leadingIcon");
            }
        }
    }
}
