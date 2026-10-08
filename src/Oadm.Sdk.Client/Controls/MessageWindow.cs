using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The message and confirmation dialog of the host and of plugin dialogs (one window, never a copy): title bar, the
/// message, and a <see cref="DialogFooter"/> with Cancel on the left and the primary confirm button on the right.
/// Use <see cref="ConfirmAsync"/> / <see cref="ShowMessageAsync"/>; the window size follows the message. A confirm
/// button that deletes or removes something (<see cref="IsDestructive"/>) is the red <c>Button.danger</c>.
/// </summary>
public sealed class MessageWindow : Window
{
    private readonly DialogTitleBar _title = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 4, 24, 24) };
    private readonly Button _confirm = new() { IsDefault = true };
    private readonly DialogFooter _footer = new();

    public MessageWindow()
    {
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ExtendClientAreaToDecorationsHint = true;
        Bind(ExtendClientAreaTitleBarHeightHintProperty, this.GetResourceObservable("Oadm.TitleBarHeight"));
        _confirm.Classes.Add("primary");
        _confirm.Click += (_, _) => Close(true);
        _footer.CancelButton.Click += (_, _) => Close(false);
        _footer.Actions.Add(_confirm);
        Content = new StackPanel { Children = { _title, _message, _footer } };
    }

    /// <summary>The window title (title bar and task switcher).</summary>
    public string? Heading
    {
        get => _title.Text;
        set
        {
            _title.Text = value;
            Title = value;
        }
    }

    public string? Message
    {
        get => _message.Text;
        set => _message.Text = value;
    }

    public string? ConfirmText
    {
        get => _confirm.Content as string;
        set
        {
            _confirm.Content = value;
            IsDanger = IsDestructive(value);
        }
    }

    /// <summary>The confirm button is the red danger button (set from <see cref="ConfirmText"/>; can be overridden).</summary>
    public bool IsDanger
    {
        get => _confirm.Classes.Contains("danger");
        set
        {
            _confirm.Classes.Set("danger", value);
            _confirm.Classes.Set("primary", !value);
        }
    }

    /// <summary>
    /// True for confirm texts of irreversible actions: they start with Delete, Remove or Release ("Delete all",
    /// "Remove", "Release"). One rule for the host and every plugin.
    /// </summary>
    public static bool IsDestructive(string? confirmText) =>
        confirmText is not null
        && (confirmText.StartsWith("Delete", StringComparison.OrdinalIgnoreCase)
            || confirmText.StartsWith("Remove", StringComparison.OrdinalIgnoreCase)
            || confirmText.StartsWith("Release", StringComparison.OrdinalIgnoreCase));

    /// <summary>Text of the Cancel button; null or empty hides it (a plain message with OK only).</summary>
    public string? CancelText
    {
        get => _footer.CancelText;
        set
        {
            _footer.CancelText = value;
            _footer.CancelButton.IsVisible = !string.IsNullOrEmpty(value);
        }
    }

    /// <summary>The confirm button (for tests).</summary>
    public Button ConfirmButton => _confirm;

    /// <summary>The Cancel button (for tests).</summary>
    public Button CancelButton => _footer.CancelButton;

    /// <summary>Asks for confirmation; true when the user clicked <paramref name="confirmText"/>.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirmText, string cancelText = "Cancel")
    {
        ArgumentNullException.ThrowIfNull(owner);
        var window = new MessageWindow { Heading = title, Message = message, ConfirmText = confirmText, CancelText = cancelText };
        return await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    /// <summary>Shows a message with an OK button.</summary>
    public static async Task ShowMessageAsync(Window owner, string title, string message)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var window = new MessageWindow { Heading = title, Message = message, ConfirmText = "OK", CancelText = null };
        await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }
}
