namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// One entry of a grouped select field (<c>ComboBox</c> with the class <c>grouped</c>): a group header (grey, not
/// selectable) or a choice below it (indented). The theme draws both; the selected choice shows without indent.
/// </summary>
/// <example>
/// <code>
/// &lt;ComboBox Classes="grouped" ItemsSource="{Binding Choices}" SelectedItem="{Binding SelectedChoice}" /&gt;
/// Choices = GroupedChoice.Build(events, e =&gt; e.Group, e =&gt; e.Title);
/// </code>
/// </example>
/// <param name="Text">What the select shows.</param>
/// <param name="Value">The item behind a choice; null for a header.</param>
/// <param name="IsHeader">A group header that cannot be chosen.</param>
public sealed record GroupedChoice(string Text, object? Value, bool IsHeader)
{
    /// <summary>A choice, not a header.</summary>
    public bool IsSelectable => !IsHeader;

    public override string ToString() => Text;

    /// <summary>Headers and choices, groups and the choices in them sorted by text (current culture, case-insensitive).</summary>
    public static IReadOnlyList<GroupedChoice> Build<T>(IEnumerable<T> items, Func<T, string> group, Func<T, string> text)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(text);
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var list = new List<GroupedChoice>();
        foreach (var g in items.GroupBy(group).OrderBy(g => g.Key, comparer))
        {
            list.Add(new GroupedChoice(g.Key, null, IsHeader: true));
            list.AddRange(g.OrderBy(text, comparer).Select(i => new GroupedChoice(text(i), i, IsHeader: false)));
        }

        return list;
    }
}
