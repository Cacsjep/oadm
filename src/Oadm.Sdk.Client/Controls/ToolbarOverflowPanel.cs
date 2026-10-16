using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// Every toolbar row of buttons (the Devices page, plugin pages): lays its entries out left to right like a horizontal
/// stack panel. Entries that do not fit (a small or scaled screen, the live view open) are hidden and offered by a
/// "More" button: opening it moves those entries into its flyout, closing moves them back. Separators never go into the
/// flyout, and a separator at the end of the visible part is hidden. The panel shows its own More button at its right
/// end unless the view places one with <see cref="OverflowButton"/>. Put only the buttons in it; a search box stays
/// outside, right of the panel.
/// </summary>
public sealed class ToolbarOverflowPanel : Panel
{
    /// <summary>Defines <see cref="Spacing"/>.</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ToolbarOverflowPanel, double>(nameof(Spacing), 4);

    /// <summary>Defines <see cref="HasOverflow"/>.</summary>
    public static readonly DirectProperty<ToolbarOverflowPanel, bool> HasOverflowProperty =
        AvaloniaProperty.RegisterDirect<ToolbarOverflowPanel, bool>(nameof(HasOverflow), p => p.HasOverflow);

    /// <summary>Defines <see cref="OverflowButton"/>.</summary>
    public static readonly StyledProperty<Button?> OverflowButtonProperty =
        AvaloniaProperty.Register<ToolbarOverflowPanel, Button?>(nameof(OverflowButton));

    private readonly List<Control> _overflow = [];
    private readonly List<(Control Control, int Index)> _moved = [];
    private readonly StackPanel _flyoutItems = new() { Spacing = 2, Classes = { "toolbarOverflow" } };
    private bool _hasOverflow;
    private ToolbarButton? _ownButton;

    static ToolbarOverflowPanel()
    {
        AffectsMeasure<ToolbarOverflowPanel>(SpacingProperty);
    }

    /// <summary>Creates the panel with its own More button.</summary>
    public ToolbarOverflowPanel()
    {
        ClipToBounds = true;
        _flyoutItems.AddHandler(Button.ClickEvent, OnFlyoutClick, RoutingStrategies.Bubble);
        _ownButton = new ToolbarButton { IconKey = "more", IsIconOnly = true };
        ToolTip.SetTip(_ownButton, "More actions");
        AttachFlyout(_ownButton);
        VisualChildren.Add(_ownButton);
        LogicalChildren.Add(_ownButton);
    }

    /// <summary>The space between two entries.</summary>
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

    /// <summary>
    /// A "More" button the view places itself (the Devices page: right of the panel, visible with
    /// <see cref="HasOverflow"/>); null = the panel's own button at its right end.
    /// </summary>
    public Button? OverflowButton
    {
        get => GetValue(OverflowButtonProperty);
        set => SetValue(OverflowButtonProperty, value);
    }

    /// <summary>The entries that did not fit at the last layout (tests).</summary>
    public IReadOnlyList<Control> OverflowItems => _overflow;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OverflowButtonProperty)
        {
            if (change.OldValue is Button old)
            {
                old.Flyout = null;
            }

            if (_ownButton is not null)
            {
                _ownButton.Flyout = null;
                VisualChildren.Remove(_ownButton);
                LogicalChildren.Remove(_ownButton);
                _ownButton = null;
            }

            if (change.NewValue is Button button)
            {
                AttachFlyout(button);
            }

            InvalidateMeasure();
        }
    }

    /// <inheritdoc />
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

        if (_ownButton is not null)
        {
            _ownButton.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, _ownButton.DesiredSize.Height);
        }

        return new Size(Math.Min(width, availableSize.Width), height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        bool overflows = Layout(finalSize);
        if (_ownButton is not null)
        {
            double buttonWidth = _ownButton.DesiredSize.Width;
            if (overflows || _moved.Count > 0)
            {
                // Room for the More button at the right end, then the entries again in what is left.
                Layout(new Size(Math.Max(0, finalSize.Width - buttonWidth - Spacing), finalSize.Height));
                _ownButton.Arrange(new Rect(Math.Max(0, finalSize.Width - buttonWidth), 0, buttonWidth, finalSize.Height));
            }
            else
            {
                _ownButton.Arrange(new Rect(-10000, 0, buttonWidth, finalSize.Height)); // clipped away
            }
        }

        HasOverflow = _overflow.Count > 0 || _moved.Count > 0;
        return finalSize;
    }

    /// <summary>Places the entries in <paramref name="finalSize"/>; returns whether some did not fit.</summary>
    private bool Layout(Size finalSize)
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

        return _overflow.Count > 0;
    }

    private void AttachFlyout(Button button)
    {
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, Content = _flyoutItems };
        flyout.Opening += (_, _) => MoveOverflowIntoFlyout();
        flyout.Closed += (_, _) => MoveBack();
        button.Flyout = flyout;
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
        if (e.Source is Button { Flyout: null } && (OverflowButton ?? _ownButton)?.Flyout is { } flyout)
        {
            flyout.Hide();
        }
    }
}
