using Avalonia;
using Avalonia.Controls;

namespace Oadm.Sdk.Client.Controls;

/// <summary>How <see cref="FlowColumns"/> places its children.</summary>
public enum FlowPacking
{
    /// <summary>Row by row, left to right (form fields: the reading order stays).</summary>
    Rows,

    /// <summary>Each child into the shortest column (cards of different heights, no gaps).</summary>
    Columns,
}

/// <summary>
/// A panel that uses the width it gets instead of stretching its children: as many equal columns as fit
/// <see cref="MinColumnWidth"/> (at most <see cref="MaxColumns"/>, never more than the visible children), each child as wide as
/// its column. A narrow place gives one column, a wide one several, like a flex wrap. Invisible children take no room.
/// Used by the node panel of Vision Center (its cards, and the fields in its forms).
/// </summary>
public sealed class FlowColumns : Panel
{
    /// <summary>The property of <see cref="MinColumnWidth"/>.</summary>
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<FlowColumns, double>(nameof(MinColumnWidth), 320);

    /// <summary>The property of <see cref="MaxColumns"/>.</summary>
    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<FlowColumns, int>(nameof(MaxColumns), 4);

    /// <summary>The property of <see cref="ColumnSpacing"/>.</summary>
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<FlowColumns, double>(nameof(ColumnSpacing), 12);

    /// <summary>The property of <see cref="RowSpacing"/>.</summary>
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<FlowColumns, double>(nameof(RowSpacing), 8);

    /// <summary>The property of <see cref="Packing"/>.</summary>
    public static readonly StyledProperty<FlowPacking> PackingProperty =
        AvaloniaProperty.Register<FlowColumns, FlowPacking>(nameof(Packing));

    static FlowColumns() =>
        AffectsMeasure<FlowColumns>(MinColumnWidthProperty, MaxColumnsProperty, ColumnSpacingProperty, RowSpacingProperty, PackingProperty);

    /// <summary>A column is never narrower than this (unless the whole panel is).</summary>
    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>The most columns, however wide the panel (default 4).</summary>
    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>The gap between two columns (default 12).</summary>
    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>The gap between two children one above the other (default 8; the theme's class <c>form</c> gives 10).</summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>Row by row (form fields) or into the shortest column (cards).</summary>
    public FlowPacking Packing
    {
        get => GetValue(PackingProperty);
        set => SetValue(PackingProperty, value);
    }

    /// <summary>The number of columns at <paramref name="width"/> for <paramref name="visible"/> children.</summary>
    public static int ColumnsFor(double width, double minColumnWidth, double spacing, int maxColumns, int visible)
    {
        if (visible <= 1 || double.IsInfinity(width) || double.IsNaN(width))
        {
            return 1;
        }

        var fit = (int)Math.Floor((width + spacing) / (Math.Max(1, minColumnWidth) + spacing));
        return Math.Clamp(fit, 1, Math.Max(1, Math.Min(maxColumns, visible)));
    }

    protected override Size MeasureOverride(Size availableSize) => Layout(availableSize, arrange: false);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize, arrange: true);
        return finalSize;
    }

    private Size Layout(Size size, bool arrange)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        var columns = ColumnsFor(size.Width, MinColumnWidth, ColumnSpacing, MaxColumns, visible.Count);
        var width = double.IsInfinity(size.Width)
            ? double.PositiveInfinity
            : Math.Max(0, (size.Width - (ColumnSpacing * (columns - 1))) / columns);
        var heights = new double[columns];
        var widest = 0.0;
        var rowTop = 0.0;
        var rowHeight = 0.0;
        for (var i = 0; i < visible.Count; i++)
        {
            var child = visible[i];
            if (!arrange)
            {
                child.Measure(new Size(width, double.PositiveInfinity));
            }

            var desired = child.DesiredSize;
            widest = Math.Max(widest, desired.Width);
            int column;
            double top;
            if (Packing == FlowPacking.Columns)
            {
                column = Array.IndexOf(heights, heights.Min());
                top = heights[column] == 0 ? 0 : heights[column] + RowSpacing;
                heights[column] = top + desired.Height;
            }
            else
            {
                column = i % columns;
                if (column == 0 && i > 0)
                {
                    rowTop += rowHeight + RowSpacing;
                    rowHeight = 0;
                }

                top = rowTop;
                rowHeight = Math.Max(rowHeight, desired.Height);
                heights[column] = rowTop + rowHeight;
            }

            if (arrange)
            {
                var x = column * ((double.IsInfinity(width) ? desired.Width : width) + ColumnSpacing);
                child.Arrange(new Rect(x, top, double.IsInfinity(width) ? desired.Width : width, desired.Height));
            }
        }

        var total = visible.Count == 0 ? 0 : heights.Max();
        var used = double.IsInfinity(size.Width) ? widest : size.Width;
        return new Size(visible.Count == 0 ? 0 : used, total);
    }
}
