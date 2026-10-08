using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;

using Oadm.Client.Tags;
using Oadm.Contracts.V1;

namespace Oadm.Client.Controls;

/// <summary>
/// The tags of a device on one line (device grid "Tags" column): a chip per tag (theme style <c>Border.tagChip</c>:
/// the name in the tag color on a subtle background), and "+N" (<c>TextBlock.tagMore</c>) for the tags that do not fit
/// the width. The tooltip lists every tag. Chips are made once per change of <see cref="Tags"/>; a recolor reaches them
/// through their binding to the shared <see cref="TagInfo"/>.
/// </summary>
public sealed class TagChipList : Panel
{
    public static readonly StyledProperty<IReadOnlyList<TagInfo>?> TagsProperty =
        AvaloniaProperty.Register<TagChipList, IReadOnlyList<TagInfo>?>(nameof(Tags));

    private readonly TextBlock _more = new() { Classes = { "tagMore" }, VerticalAlignment = VerticalAlignment.Center };
    private int _shown;

    static TagChipList()
    {
        TagsProperty.Changed.AddClassHandler<TagChipList>((list, _) => list.Rebuild());
        ClipToBoundsProperty.OverrideDefaultValue<TagChipList>(true);
    }

    public TagChipList()
    {
        Children.Add(_more);
    }

    public IReadOnlyList<TagInfo>? Tags
    {
        get => GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    /// <summary>Chips that fit at the last arrange (tests).</summary>
    public int ShownCount => _shown;

    /// <summary>The "+N" text at the last arrange, empty when every chip fits (tests).</summary>
    public string MoreText => _more.IsVisible ? _more.Text ?? "" : "";

    /// <summary>One chip: a dot-less pill with the name in the tag color (shared by the grid, the dialog uses its own rows).</summary>
    public static Border CreateChip(TagInfo tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var text = new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(TagInfo.Name)) { Source = tag } };
        text.Bind(TextBlock.ForegroundProperty, new Binding(nameof(TagInfo.Color)) { Source = tag, Converter = TagColorBrushConverter.Instance });
        return new Border { Classes = { "tagChip" }, Child = text };
    }

    private void Rebuild()
    {
        Children.Clear();
        IReadOnlyList<TagInfo> tags = Tags ?? [];
        foreach (TagInfo tag in tags)
        {
            Children.Add(CreateChip(tag));
        }

        Children.Add(_more);
        ToolTip.SetTip(this, tags.Count == 0 ? null : string.Join(Environment.NewLine, tags.Select(t => t.Name)));
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0;
        double height = 0;
        foreach (Control child in Children)
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
        int count = Children.Count - 1;
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            total += Children[i].DesiredSize.Width;
        }

        int shown = count;
        double x = 0;
        if (total > finalSize.Width)
        {
            // Not all fit: keep room for "+N" (its width for the largest N is close enough).
            _more.Text = "+" + count;
            _more.Measure(Size.Infinity);
            double room = finalSize.Width - _more.DesiredSize.Width;
            shown = 0;
            while (shown < count && x + Children[shown].DesiredSize.Width <= room)
            {
                x += Children[shown].DesiredSize.Width;
                shown++;
            }
        }

        x = 0;
        for (int i = 0; i < count; i++)
        {
            Control chip = Children[i];
            double top = (finalSize.Height - chip.DesiredSize.Height) / 2;
            // Chips that do not fit go past the right edge (clipped), so nothing is created or hidden per layout pass.
            chip.Arrange(new Rect(i < shown ? x : finalSize.Width + 1, top, chip.DesiredSize.Width, chip.DesiredSize.Height));
            x += i < shown ? chip.DesiredSize.Width : 0;
        }

        _shown = shown;
        bool more = shown < count;
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

/// <summary>
/// The tag color picker (new tag and edit rows of the Tags dialog): the eight palette colors as round swatches
/// (theme style <c>Button.colorSwatch</c>, the chosen one with class <c>selected</c>), tooltip = color name.
/// </summary>
public sealed class TagColorPicker : StackPanel
{
    public static readonly StyledProperty<TagColor> SelectedColorProperty =
        AvaloniaProperty.Register<TagColorPicker, TagColor>(nameof(SelectedColor), defaultBindingMode: BindingMode.TwoWay);

    private readonly List<(TagColor Color, Button Button)> _swatches = [];

    static TagColorPicker()
    {
        SelectedColorProperty.Changed.AddClassHandler<TagColorPicker>((picker, _) => picker.UpdateSelection());
    }

    public TagColorPicker()
    {
        Orientation = Orientation.Horizontal;
        Classes.Add("colorPicker");
        foreach (TagColor color in TagPalette.Colors)
        {
            var dot = new Ellipse { Classes = { "tagSwatch" }, Fill = TagPalette.Brush(color) };
            var button = new Button { Classes = { "colorSwatch" }, Content = dot };
            ToolTip.SetTip(button, TagPalette.Name(color));
            Avalonia.Automation.AutomationProperties.SetName(button, TagPalette.Name(color));
            button.Click += (_, _) => SelectedColor = color;
            _swatches.Add((color, button));
            Children.Add(button);
        }

        UpdateSelection();
    }

    public TagColor SelectedColor
    {
        get => GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    private void UpdateSelection()
    {
        foreach ((TagColor color, Button button) in _swatches)
        {
            button.Classes.Set("selected", color == SelectedColor);
        }
    }
}
