using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Controls;

/// <summary>
/// The Devices page toolbar row: lays its entries out left to right like a horizontal stack panel. Entries that do not
/// fit (narrow window, live view open) are hidden and offered by a "More" button (<see cref="OverflowButton"/>, placed
/// by the view right of the panel): opening it moves those entries into its flyout, closing moves them back.
/// Separators never go into the flyout, and a separator at the end of the visible part is hidden.
/// </summary>
public sealed class ToolbarOverflowPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ToolbarOverflowPanel, double>(nameof(Spacing), 4);

    public static readonly DirectProperty<ToolbarOverflowPanel, bool> HasOverflowProperty =
        AvaloniaProperty.RegisterDirect<ToolbarOverflowPanel, bool>(nameof(HasOverflow), p => p.HasOverflow);

    public static readonly StyledProperty<Button?> OverflowButtonProperty =
        AvaloniaProperty.Register<ToolbarOverflowPanel, Button?>(nameof(OverflowButton));

    private readonly List<Control> _overflow = [];
    private readonly List<(Control Control, int Index)> _moved = [];
    private readonly StackPanel _flyoutItems = new() { Spacing = 2, Classes = { "toolbarOverflow" } };
    private bool _hasOverflow;

    static ToolbarOverflowPanel()
    {
        AffectsMeasure<ToolbarOverflowPanel>(SpacingProperty);
    }

    public ToolbarOverflowPanel()
    {
        ClipToBounds = true;
        _flyoutItems.AddHandler(Button.ClickEvent, OnFlyoutClick, RoutingStrategies.Bubble);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>True when entries do not fit (also while the flyout shows them).</summary>
    public bool HasOverflow
    {
        get => _hasOverflow;
        private set => SetAndRaise(HasOverflowProperty, ref _hasOverflow, value);
    }

    /// <summary>The "More" button that shows the entries that do not fit.</summary>
    public Button? OverflowButton
    {
        get => GetValue(OverflowButtonProperty);
        set => SetValue(OverflowButtonProperty, value);
    }

    /// <summary>The entries that did not fit at the last layout (tests).</summary>
    public IReadOnlyList<Control> OverflowItems => _overflow;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OverflowButtonProperty)
        {
            if (change.OldValue is Button old)
            {
                old.Flyout = null;
            }

            if (change.NewValue is Button button)
            {
                var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, Content = _flyoutItems };
                flyout.Opening += (_, _) => MoveOverflowIntoFlyout();
                flyout.Closed += (_, _) => MoveBack();
                button.Flyout = flyout;
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0;
        double height = 0;
        int visible = 0;
        foreach (Control child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (!child.IsVisible)
            {
                continue;
            }

            width += child.DesiredSize.Width + (visible > 0 ? Spacing : 0);
            height = Math.Max(height, child.DesiredSize.Height);
            visible++;
        }

        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _overflow.Clear();
        double x = 0;
        bool full = false;
        Control? lastShown = null;
        foreach (Control child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            double width = child.DesiredSize.Width;
            double start = lastShown is null ? 0 : x + Spacing;
            if (!full && start + width <= finalSize.Width + 0.5)
            {
                child.Arrange(new Rect(start, 0, width, finalSize.Height));
                x = start + width;
                lastShown = child;
                continue;
            }

            full = true;
            child.Arrange(new Rect(-10000, 0, width, finalSize.Height)); // clipped away
            if (child is not ToolbarSeparator)
            {
                _overflow.Add(child);
            }
        }

        // A separator must not end the visible part.
        if (full && lastShown is ToolbarSeparator separator)
        {
            separator.Arrange(new Rect(-10000, 0, separator.DesiredSize.Width, finalSize.Height));
        }

        HasOverflow = _overflow.Count > 0 || _moved.Count > 0;
        return finalSize;
    }

    private void MoveOverflowIntoFlyout()
    {
        MoveBack();
        foreach (Control control in _overflow.ToList())
        {
            int index = Children.IndexOf(control);
            if (index < 0)
            {
                continue;
            }

            _moved.Add((control, index));
        }

        // Remove from the end so the remembered indexes stay valid when they are put back in order.
        foreach ((Control control, _) in _moved.OrderByDescending(m => m.Index))
        {
            Children.Remove(control);
        }

        foreach ((Control control, _) in _moved)
        {
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            _flyoutItems.Children.Add(control);
        }
    }

    private void MoveBack()
    {
        if (_moved.Count == 0)
        {
            return;
        }

        foreach ((Control control, int index) in _moved.OrderBy(m => m.Index))
        {
            _flyoutItems.Children.Remove(control);
            control.ClearValue(HorizontalAlignmentProperty);
            Children.Insert(Math.Min(index, Children.Count), control);
        }

        _moved.Clear();
        InvalidateMeasure();
    }

    /// <summary>A click on a moved entry closes the flyout, unless the entry opens its own menu (Add).</summary>
    private void OnFlyoutClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { Flyout: null } && OverflowButton?.Flyout is { } flyout)
        {
            flyout.Hide();
        }
    }
}
