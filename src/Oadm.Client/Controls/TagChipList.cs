using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

using Oadm.Client.Tags;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Controls;

/// <summary>
/// The tags of a device on one line (device grid "Tags" column), laid out by the shared <see cref="TagChipRow"/>: a chip per
/// tag (theme style <c>Border.tagChip</c>: the name in the tag color on a subtle background) and "+N" for the tags that do not
/// fit the width; the tooltip lists every tag. Chips are made once per change of <see cref="Tags"/>; a recolor reaches them
/// through their binding to the shared <see cref="TagInfo"/>.
/// </summary>
public sealed class TagChipList : TagChipRow
{
    public static readonly StyledProperty<IReadOnlyList<TagInfo>?> TagsProperty =
        AvaloniaProperty.Register<TagChipList, IReadOnlyList<TagInfo>?>(nameof(Tags));

    static TagChipList()
    {
        TagsProperty.Changed.AddClassHandler<TagChipList>((list, _) => list.Rebuild());
    }

    public IReadOnlyList<TagInfo>? Tags
    {
        get => GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    /// <summary>One chip: a dot-less pill with the name in the tag color (shared by the grid, the dialog uses its own rows).</summary>
    public static Border CreateChip(TagInfo tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var text = new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(TagInfo.Name)) { Source = tag } };
        text.Bind(TextBlock.ForegroundProperty, new Binding(nameof(TagInfo.Color)) { Source = tag, Converter = TagColorBrushConverter.Instance });
        return CreateChip(text);
    }

    private void Rebuild()
    {
        IReadOnlyList<TagInfo> tags = Tags ?? [];
        SetChips([.. tags.Select(t => (Control)CreateChip(t))], [.. tags.Select(t => t.Name)]);
    }
}
