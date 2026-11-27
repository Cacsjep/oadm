using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The eight tag colours of OADM (SDK 0.17.0), numbered like the device tags: 1 Blue, 2 Green, 3 Teal, 4 Amber, 5 Orange, 6 Red,
/// 7 Pink, 8 Violet; any other number has no colour (grey). Each is a theme brush <c>Oadm.Tag.&lt;Name&gt;Brush</c> in both
/// appearances. Plugins with tags of their own (License Plate Center's plate tags) show them like the device tags with
/// <see cref="TagChips"/>, <see cref="TagColorPicker"/> and <see cref="BrushConverter"/>.
/// </summary>
public static class TagColors
{
    /// <summary>The colours in picker order.</summary>
    public static IReadOnlyList<int> All { get; } = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>"Blue"; "No color" for a number outside the palette.</summary>
    public static string Name(int color) => color switch
    {
        1 => "Blue",
        2 => "Green",
        3 => "Teal",
        4 => "Amber",
        5 => "Orange",
        6 => "Red",
        7 => "Pink",
        8 => "Violet",
        _ => "No color",
    };

    /// <summary>The theme resource of a colour: "Oadm.Tag.BlueBrush"; outside the palette "Oadm.Tag.NeutralBrush".</summary>
    public static string ResourceKey(int color) => color is >= 1 and <= 8 ? $"Oadm.Tag.{Name(color)}Brush" : "Oadm.Tag.NeutralBrush";

    /// <summary>The colour a new tag gets by default: the palette in order, by the number of tags there are.</summary>
    public static int Next(int existingTags) => All[Math.Max(0, existingTags) % All.Count];

    /// <summary>The theme brush of a colour (null without an application, e.g. view model tests).</summary>
    public static IBrush? Brush(int color) =>
        Application.Current is { } app && app.TryGetResource(ResourceKey(color), app.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : null;

    /// <summary>Binds a colour number (or an enum numbered like the palette) to its theme brush: chip text, dots.</summary>
    public static IValueConverter BrushConverter { get; } = new TagColorBrushConverter();

    private sealed class TagColorBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Brush(value is null ? 0 : System.Convert.ToInt32(value, CultureInfo.InvariantCulture));

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

/// <summary>One tag of <see cref="TagChips"/>: its name and its colour (<see cref="TagColors"/>).</summary>
/// <param name="Name">The tag's name.</param>
/// <param name="Color">The colour number, 1 to 8.</param>
public sealed record TagChip(string Name, int Color);

/// <summary>
/// The layout of tags on one line (SDK 0.17.0; the device grid's Tags column and <see cref="TagChips"/>): a chip per tag (theme
/// style <c>Border.tagChip</c>: the name in the tag colour on a subtle background) and "+N" (<c>TextBlock.tagMore</c>) for the
/// tags that do not fit the width; the tooltip lists every tag. A subclass hands its chips over with <see cref="SetChips"/>.
/// </summary>
public class TagChipRow : Panel
{
    private readonly TextBlock _more = new() { Classes = { "tagMore" }, VerticalAlignment = VerticalAlignment.Center };
    private int _shown;

    static TagChipRow()
    {
        ClipToBoundsProperty.OverrideDefaultValue<TagChipRow>(true);
    }

    public TagChipRow()
    {
        Children.Add(_more);
    }

    /// <summary>Chips that fit at the last arrange (tests).</summary>
    public int ShownCount => _shown;

    /// <summary>The "+N" text at the last arrange, empty when every chip fits (tests).</summary>
    public string MoreText => _more.IsVisible ? _more.Text ?? string.Empty : string.Empty;

    /// <summary>One chip around <paramref name="text"/> (its text and colour set or bound by the caller).</summary>
    public static Border CreateChip(TextBlock text) => new() { Classes = { "tagChip" }, Child = text };

    /// <summary>One chip of a plain tag.</summary>
    public static Border CreateChip(TagChip tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return CreateChip(new TextBlock { Text = tag.Name, Foreground = TagColors.Brush(tag.Color) });
    }

    /// <summary>Shows <paramref name="chips"/> in this order; <paramref name="names"/> make the tooltip (none: no tooltip).</summary>
    protected void SetChips(IReadOnlyList<Control> chips, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(chips);
        ArgumentNullException.ThrowIfNull(names);
        Children.Clear();
        foreach (var chip in chips)
        {
            Children.Add(chip);
        }

        Children.Add(_more);
        ToolTip.SetTip(this, names.Count == 0 ? null : string.Join(Environment.NewLine, names));
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0;
        double height = 0;
        foreach (var child in Children)
        {
            child.Measure(Size.Infinity);
            height = Math.Max(height, child.DesiredSize.Height);
            if (child != _more)
            {
                width += child.DesiredSize.Width;
            }
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = Children.Count - 1;
        double total = 0;
        for (var i = 0; i < count; i++)
        {
            total += Children[i].DesiredSize.Width;
        }

        var shown = count;
        double x = 0;
        if (total > finalSize.Width)
        {
            // Not all fit: keep room for "+N" (its width for the largest N is close enough).
            _more.Text = "+" + count;
            _more.Measure(Size.Infinity);
            var room = finalSize.Width - _more.DesiredSize.Width;
            shown = 0;
            while (shown < count && x + Children[shown].DesiredSize.Width <= room)
            {
                x += Children[shown].DesiredSize.Width;
                shown++;
            }
        }

        x = 0;
        for (var i = 0; i < count; i++)
        {
            var chip = Children[i];
            var top = (finalSize.Height - chip.DesiredSize.Height) / 2;
            // Chips that do not fit go past the right edge (clipped), so nothing is created or hidden per layout pass.
            chip.Arrange(new Rect(i < shown ? x : finalSize.Width + 1, top, chip.DesiredSize.Width, chip.DesiredSize.Height));
            x += i < shown ? chip.DesiredSize.Width : 0;
        }

        _shown = shown;
        var more = shown < count;
        _more.IsVisible = more;
        if (more)
        {
            _more.Text = "+" + (count - shown);
            _more.Measure(Size.Infinity);
            _more.Arrange(new Rect(x, (finalSize.Height - _more.DesiredSize.Height) / 2, _more.DesiredSize.Width, _more.DesiredSize.Height));
        }

        return finalSize;
    }
}

/// <summary>Plain tags on one line (SDK 0.17.0), like the device grid's Tags column: a plugin's own tags in a grid cell.</summary>
public sealed class TagChips : TagChipRow
{
    public static readonly StyledProperty<IReadOnlyList<TagChip>?> TagsProperty =
        AvaloniaProperty.Register<TagChips, IReadOnlyList<TagChip>?>(nameof(Tags));

    static TagChips()
    {
        TagsProperty.Changed.AddClassHandler<TagChips>((list, _) => list.Rebuild());
    }

    /// <summary>The tags in their order; a new list rebuilds the chips.</summary>
    public IReadOnlyList<TagChip>? Tags
    {
        get => GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    private void Rebuild()
    {
        var tags = Tags ?? [];
        SetChips([.. tags.Select(CreateChip)], [.. tags.Select(t => t.Name)]);
    }
}

/// <summary>
/// The tag colour picker (SDK 0.17.0; the device tags dialog and plugins with tags of their own): the eight colours as round
/// swatches (theme style <c>Button.colorSwatch</c>, the chosen one with class <c>selected</c>), tooltip = colour name.
/// </summary>
public sealed class TagColorPicker : StackPanel
{
    public static readonly StyledProperty<int> SelectedColorProperty =
        AvaloniaProperty.Register<TagColorPicker, int>(nameof(SelectedColor), 1, defaultBindingMode: BindingMode.TwoWay);

    private readonly List<(int Color, Button Button)> _swatches = [];

    static TagColorPicker()
    {
        SelectedColorProperty.Changed.AddClassHandler<TagColorPicker>((picker, _) => picker.UpdateSelection());
    }

    public TagColorPicker()
    {
        Orientation = Orientation.Horizontal;
        Classes.Add("colorPicker");
        foreach (var color in TagColors.All)
        {
            var dot = new Ellipse { Classes = { "tagSwatch" } };
            dot.Bind(Shape.FillProperty, dot.GetResourceObservable(TagColors.ResourceKey(color)));
            var button = new Button { Classes = { "colorSwatch" }, Content = dot };
            ToolTip.SetTip(button, TagColors.Name(color));
            Avalonia.Automation.AutomationProperties.SetName(button, TagColors.Name(color));
            button.Click += (_, _) => SelectedColor = color;
            _swatches.Add((color, button));
            Children.Add(button);
        }

        UpdateSelection();
    }

    /// <summary>The chosen colour, 1 to 8 (two-way).</summary>
    public int SelectedColor
    {
        get => GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    /// <summary>The swatch of a colour (tests).</summary>
    public Button SwatchOf(int color) => _swatches.Single(s => s.Color == color).Button;

    private void UpdateSelection()
    {
        foreach (var (color, button) in _swatches)
        {
            button.Classes.Set("selected", color == SelectedColor);
        }
    }
}
