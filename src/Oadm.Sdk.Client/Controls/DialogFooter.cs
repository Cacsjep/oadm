using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Metadata;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The button row at the bottom of every dialog, laid out like the add devices page: Cancel
/// (secondary, Escape) on the left, the dialog's own buttons (<see cref="Actions"/>, e.g. a
/// secondary Back and a primary Apply) on the right. Margin comes from the theme.
/// </summary>
public sealed class DialogFooter : Grid
{
    public static readonly StyledProperty<string?> CancelTextProperty =
        AvaloniaProperty.Register<DialogFooter, string?>(nameof(CancelText), "Cancel");

    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<DialogFooter, ICommand?>(nameof(CancelCommand));

    private readonly Button _cancel = new() { HorizontalAlignment = HorizontalAlignment.Left, IsCancel = true };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    public DialogFooter()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _cancel.Classes.Add("secondary");
        _cancel.Content = CancelText;
        SetColumn(_cancel, 0);
        SetColumn(_actions, 1);
        Children.Add(_cancel);
        Children.Add(_actions);
    }

    public string? CancelText
    {
        get => GetValue(CancelTextProperty);
        set => SetValue(CancelTextProperty, value);
    }

    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    /// <summary>The right-aligned buttons, in order.</summary>
    [Content]
    public Avalonia.Controls.Controls Actions => _actions.Children;

    /// <summary>The Cancel button (for tests).</summary>
    public Button CancelButton => _cancel;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CancelTextProperty)
        {
            _cancel.Content = CancelText;
        }
        else if (change.Property == CancelCommandProperty)
        {
            _cancel.Command = CancelCommand;
        }
    }
}
