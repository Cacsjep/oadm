using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The eye button next to a stored password shown masked (SDK 0.15.0): the Credentials page's rows and the credential choice
/// of plugin forms. An icon-only command button (<c>Button.toolbar.iconOnly</c>) with the eye while <see cref="IsRevealed"/>
/// is false and the crossed-out eye while true, tooltip "Show password" / "Hide password" (<see cref="PasswordBox.ShowTip"/>,
/// <see cref="PasswordBox.HideTip"/>). Bind <see cref="Button.Command"/> to the view model's toggle and
/// <see cref="IsRevealed"/> to whether the password shows; the view model loads and forgets the password.
/// </summary>
public class PasswordRevealButton : Button
{
    /// <summary>Whether the password shows (the crossed-out eye, "Hide password").</summary>
    public static readonly StyledProperty<bool> IsRevealedProperty =
        AvaloniaProperty.Register<PasswordRevealButton, bool>(nameof(IsRevealed));

    private readonly OadmIcon _icon = new();

    /// <summary>A hidden password's eye button.</summary>
    public PasswordRevealButton()
    {
        Content = _icon;
        Classes.Add("toolbar");
        Classes.Add("iconOnly");
        Update();
    }

    /// <summary>Whether the password shows.</summary>
    public bool IsRevealed
    {
        get => GetValue(IsRevealedProperty);
        set => SetValue(IsRevealedProperty, value);
    }

    // Same theme and styles as every other button.
    protected override Type StyleKeyOverride => typeof(Button);

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsRevealedProperty)
        {
            Update();
        }
    }

    private void Update()
    {
        ToolTip.SetTip(this, IsRevealed ? PasswordBox.HideTip : PasswordBox.ShowTip);
        if (this.TryFindResource(IsRevealed ? "Icon.eyeOff" : "Icon.eye", ActualThemeVariant, out object? icon) && icon is Geometry g)
        {
            _icon.Data = g;
        }
    }
}
