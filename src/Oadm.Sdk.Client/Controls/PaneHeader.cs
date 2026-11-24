using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The header bar of a pane below a list (the Tasks pane of the Devices page, the runs of Schedules): the title left with
/// an optional badge, an optional middle part (a progress row), the pane's actions right (flat command buttons, a
/// separator, the red one last). One row of the theme's <c>titleRow</c>, so the buttons sit on the title's baseline.
/// </summary>
public sealed class PaneHeader : Grid
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PaneHeader, string?>(nameof(Title));

    public static readonly StyledProperty<Control?> BadgeProperty =
        AvaloniaProperty.Register<PaneHeader, Control?>(nameof(Badge));

    public static readonly StyledProperty<Control?> MiddleProperty =
        AvaloniaProperty.Register<PaneHeader, Control?>(nameof(Middle));

    public static readonly StyledProperty<Control?> ActionsProperty =
        AvaloniaProperty.Register<PaneHeader, Control?>(nameof(Actions));

    private readonly StackPanel _start = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center };

    public PaneHeader()
    {
        Classes.Add("titleRow");
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        _title.Classes.Add("sectionTitle");
        _start.Children.Add(_title);
        Children.Add(_start);
    }

    /// <summary>The pane's name ("Tasks", "Runs").</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Right of the title, e.g. the count of running tasks (<c>Border.badge</c>).</summary>
    public Control? Badge
    {
        get => GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    /// <summary>Between title and actions, e.g. a <see cref="ProgressRow"/>.</summary>
    public Control? Middle
    {
        get => GetValue(MiddleProperty);
        set => SetValue(MiddleProperty, value);
    }

    /// <summary>The actions at the right (a horizontal panel of command buttons).</summary>
    public Control? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Grid);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty)
        {
            _title.Text = Title;
        }
        else if (change.Property == BadgeProperty)
        {
            Replace(_start.Children, change.OldValue as Control, Badge);
        }
        else if (change.Property == MiddleProperty)
        {
            Place(change.OldValue as Control, Middle, 1);
        }
        else if (change.Property == ActionsProperty)
        {
            Place(change.OldValue as Control, Actions, 2);
        }
    }

    private static void Replace(Avalonia.Controls.Controls children, Control? old, Control? value)
    {
        if (old is not null)
        {
            children.Remove(old);
        }

        if (value is not null)
        {
            children.Add(value);
        }
    }

    private void Place(Control? old, Control? value, int column)
    {
        if (value is not null)
        {
            SetColumn(value, column);
        }

        Replace(Children, old, value);
    }
}
