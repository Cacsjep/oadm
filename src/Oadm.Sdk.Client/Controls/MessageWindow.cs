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
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 4, 16, 24) };
    private readonly Button _confirm = new() { IsDefault = true };
    private readonly DialogFooter _footer = new();

    public MessageWindow()
    {
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.BorderOnly;
        _confirm.Classes.Add("primary");
        _confirm.Click += (_, _) => Close(true);
        _footer.CancelButton.Click += (_, _) => Close(false);
        _footer.Actions.Add(_confirm);
        _choiceField.IsVisible = false;
        _choiceField.Input = _choice;
        _choice.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        var choicePanel = new StackPanel { Margin = new Thickness(16, 0, 16, 24), Children = { _choiceField } };
        choicePanel.Classes.Add("form");
        choicePanel.Bind(IsVisibleProperty, _choiceField.GetObservable(IsVisibleProperty));
        Content = new StackPanel { Children = { _title, _message, choicePanel, _footer } };
    }

    private readonly FormField _choiceField = new();
    private readonly ComboBox _choice = new();

    /// <summary>
    /// The label of one choice below the message ("Reason"); null hides it. The choices are <see cref="Choices"/>, the
    /// chosen one <see cref="ChosenIndex"/> (the first by default).
    /// </summary>
    public string? ChoiceLabel
    {
        get => _choiceField.Label;
        set
        {
            _choiceField.Label = value;
            _choiceField.IsVisible = value is not null;
        }
    }

    /// <summary>The choices of <see cref="ChoiceLabel"/>.</summary>
    public IReadOnlyList<string> Choices
    {
        get => _choices;
        set
        {
            _choices = value ?? [];
            _choice.ItemsSource = _choices;
            _choice.SelectedIndex = _choices.Count > 0 ? 0 : -1;
        }
    }

    private IReadOnlyList<string> _choices = [];

    /// <summary>The index of the chosen choice; -1 without choices.</summary>
    public int ChosenIndex
    {
        get => _choice.SelectedIndex;
        set => _choice.SelectedIndex = value;
    }

    /// <summary>The choice's select (for tests).</summary>
    public ComboBox ChoiceBox => _choice;

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

    /// <summary>The message. Line breaks start a new line; text between <c>**</c> is bold ("Turn on **Compliance**").</summary>
    public string? Message
    {
        get => _text;
        set
        {
            _text = value;
            _message.Inlines?.Clear();
            if (value is null || !value.Contains("**", StringComparison.Ordinal))
            {
                _message.Text = value;
                return;
            }

            _message.Text = null;
            var parts = value.Split("**");
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0)
                {
                    _message.Inlines!.Add(new Avalonia.Controls.Documents.Run(parts[i]) { FontWeight = i % 2 == 1 ? FontWeight.SemiBold : FontWeight.Normal });
                }
            }
        }
    }

    private string? _text;

    /// <summary>The text of the confirm button; red when it starts with Delete, Remove or Release.</summary>
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
    /// True for confirm texts of irreversible actions: they start with Delete, Remove, Release or Format ("Delete all",
    /// "Remove", "Release", "Format": formatting deletes the recordings). One rule for the host and every plugin.
    /// </summary>
    public static bool IsDestructive(string? confirmText) =>
        confirmText is not null
        && (confirmText.StartsWith("Delete", StringComparison.OrdinalIgnoreCase)
            || confirmText.StartsWith("Remove", StringComparison.OrdinalIgnoreCase)
            || confirmText.StartsWith("Release", StringComparison.OrdinalIgnoreCase)
            || confirmText.StartsWith("Format", StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// Asks for confirmation with one choice below the message (Revoke: the reason); the index of the chosen one when the
    /// user clicked <paramref name="confirmText"/>, null for Cancel. <paramref name="danger"/>: a red confirm button for an
    /// action that cannot be undone whatever its text.
    /// </summary>
    public static async Task<int?> ConfirmWithChoiceAsync(Window owner, string title, string message, string confirmText, string choiceLabel, IReadOnlyList<string> choices, bool danger = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(choices);
        var window = new MessageWindow { Heading = title, Message = message, ConfirmText = confirmText, ChoiceLabel = choiceLabel, Choices = choices };
        if (danger)
        {
            window.IsDanger = true;
        }

        return await window.ShowDialog<bool>(owner).ConfigureAwait(true) ? window.ChosenIndex : null;
    }

    /// <summary>Shows a message with an OK button.</summary>
    public static async Task ShowMessageAsync(Window owner, string title, string message)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var window = new MessageWindow { Heading = title, Message = message, ConfirmText = "OK", CancelText = null };
        await window.ShowDialog<bool>(owner).ConfigureAwait(true);
    }
}
