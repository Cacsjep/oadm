using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The one password field used on every page and in every plugin dialog: a <see cref="TextBox"/> with the
/// theme's text box look (same height, padding and centering), masked with <see cref="Mask"/>, and an
/// eye button on the right that shows the password as plain text and hides it again (tooltip
/// "Show password" / "Hide password"). Bind <see cref="TextBox.Text"/> as usual; key bindings and focus
/// work like on any text box. <see cref="TextBox.RevealPassword"/> is the shown state.
/// </summary>
public class PasswordBox : TextBox
{
    /// <summary>The mask character (bullet).</summary>
    public const char Mask = '•';

    public const string ShowTip = "Show password";
    public const string HideTip = "Hide password";

    private readonly OadmIcon _icon = new() { Width = 16, Height = 16 };

    public PasswordBox()
    {
        PasswordChar = Mask;
        RevealButton = new Button
        {
            Content = _icon,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 4, 0),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false, // the caret stays in the field
        };
        RevealButton.Classes.Add("toolbar");
        RevealButton.Classes.Add("iconOnly");
        RevealButton.Classes.Add("reveal");
        RevealButton.Click += (_, _) => RevealPassword = !RevealPassword;
        InnerRightContent = RevealButton;
        UpdateReveal();
    }

    /// <summary>The eye button (exposed for tests).</summary>
    public Button RevealButton { get; }

    // Same theme and styles as every other text box.
    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.TryFindResource("Oadm.TextSecondaryBrush", ActualThemeVariant, out object? brush) && brush is IBrush b)
        {
            _icon.Foreground = b;
        }

        UpdateReveal();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RevealPasswordProperty)
        {
            UpdateReveal();
        }
    }

    private void UpdateReveal()
    {
        if (RevealButton is null)
        {
            return; // base constructor
        }

        bool shown = RevealPassword;
        ToolTip.SetTip(RevealButton, shown ? HideTip : ShowTip);
        if (this.TryFindResource(shown ? "Icon.eyeOff" : "Icon.eye", ActualThemeVariant, out object? icon) && icon is Geometry g)
        {
            _icon.Data = g;
        }
    }
}
