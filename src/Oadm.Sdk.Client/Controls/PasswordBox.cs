using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The one password field used on every page and in every plugin dialog: a <see cref="TextBox"/> with the
/// theme's text box look (same height, padding and centering), masked with <see cref="Mask"/>, and an
/// eye button on the right that shows the password as plain text and hides it again (tooltip
/// "Show password" / "Hide password"). Bind <see cref="TextBox.Text"/> as usual; key bindings and focus
/// work like on any text box. <see cref="TextBox.RevealPassword"/> is the shown state. A paste drops its line breaks (a
/// password never holds one; a plain text box keeps only the first line, which is empty when the copied text starts with a
/// line break).
/// </summary>
public class PasswordBox : TextBox
{
    /// <summary>The mask character (bullet).</summary>
    public const char Mask = '•';

    /// <summary>The tooltip of the eye button while the password is hidden.</summary>
    public const string ShowTip = "Show password";
    /// <summary>The tooltip of the eye button while the password shows.</summary>
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
        PastingFromClipboard += OnPasting;
    }

    /// <summary>The text a paste inserts: the clipboard's text without its line breaks.</summary>
    internal static string WithoutLineBreaks(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);

    private void OnPasting(object? sender, RoutedEventArgs e)
    {
        if (IsReadOnly || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return; // the text box's own paste
        }

        e.Handled = true;
        _ = PasteWithoutLineBreaksAsync(clipboard);
    }

    private async Task PasteWithoutLineBreaksAsync(IClipboard clipboard)
    {
        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return; // a clipboard the system would not hand out: nothing pasted, like the text box's own paste
        }

        if (!string.IsNullOrEmpty(text))
        {
            SelectedText = WithoutLineBreaks(text); // replaces the selection or inserts at the caret
        }
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
