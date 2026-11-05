using Avalonia;
using Avalonia.Controls;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.EventRules;

/// <summary>
/// The Target cell of the Recipients list: the target, trimmed, then the status of a persistent recipient. The card is narrow,
/// so the target keeps its room first: while the word would leave it less than <see cref="MinTargetWidth"/> the chip shows only
/// its icon and <see cref="CompactTip"/> says the word. Decided here at layout, nothing changes on the chip itself: the chip's
/// text simply gets no room.
/// </summary>
public sealed class RecipientTargetCell : Panel
{
    /// <summary>The target's room below which the chip shows only its icon: about 10 characters.</summary>
    public const double MinTargetWidth = 64;

    public static readonly StyledProperty<string?> WideTipProperty =
        AvaloniaProperty.Register<RecipientTargetCell, string?>(nameof(WideTip));

    public static readonly StyledProperty<string?> CompactTipProperty =
        AvaloniaProperty.Register<RecipientTargetCell, string?>(nameof(CompactTip));

    static RecipientTargetCell() => AffectsArrange<RecipientTargetCell>(WideTipProperty, CompactTipProperty);

    /// <summary>The chip's tooltip while it shows the word.</summary>
    public string? WideTip
    {
        get => GetValue(WideTipProperty);
        set => SetValue(WideTipProperty, value);
    }

    /// <summary>The chip's tooltip while it shows only its icon: with the word.</summary>
    public string? CompactTip
    {
        get => GetValue(CompactTipProperty);
        set => SetValue(CompactTipProperty, value);
    }

    /// <summary>The chip has room for its word.</summary>
    public bool ShowsWord { get; private set; }

    private TextBlock Target => Children.OfType<TextBlock>().First();

    private StatusChip Chip => Children.OfType<StatusChip>().First();

    protected override Size MeasureOverride(Size availableSize)
    {
        var chip = Chip;
        chip.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var width = ChipWidth(availableSize.Width);
        var target = Target;
        target.Measure(new Size(TargetWidth(availableSize.Width, width), availableSize.Height));
        var height = Math.Max(target.DesiredSize.Height, chip.DesiredSize.Height);
        return new Size(double.IsInfinity(availableSize.Width) ? target.DesiredSize.Width + width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = ChipWidth(finalSize.Width);
        var chip = Chip;
        Target.Arrange(new Rect(0, 0, TargetWidth(finalSize.Width, width), finalSize.Height));
        chip.Arrange(new Rect(finalSize.Width - width, 0, width, finalSize.Height));
        chip.ClipToBounds = !ShowsWord; // the word stays measured: its text keeps drawing past a narrow chip
        ToolTip.SetTip(chip, ShowsWord ? WideTip : CompactTip);
        return finalSize;
    }

    /// <summary>The target ends where the chip's own margin starts: its right margin overlaps the chip's rectangle.</summary>
    private double TargetWidth(double cell, double chip) => Math.Max(0, chip > 0 ? cell - chip + Target.Margin.Right : cell);

    private double ChipWidth(double cell)
    {
        var chip = Chip;
        if (!chip.IsVisible)
        {
            ShowsWord = false;
            return 0;
        }

        var word = chip.DesiredSize.Width;
        ShowsWord = cell - word - Target.Margin.Left >= MinTargetWidth;
        return ShowsWord ? word : word - chip.WordWidth;
    }
}
