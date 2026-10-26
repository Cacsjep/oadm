using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The group key of a grouped grid (<c>DataGrid.groups</c>) whose headers show an icon left of the name: the theme's group
/// header shows the chevron, the icon <c>Icon.&lt;IconKey&gt;</c> and <see cref="Name"/>. A plain string key shows the name
/// only (the Plugins page). Groups are equal by name and icon, so rows with equal keys share one header.
/// </summary>
public sealed record GridGroup(string Name, string? IconKey = null)
{
    /// <summary>The header's text.</summary>
    public override string ToString() => Name;

    /// <summary>The theme geometry of a <see cref="GridGroup"/> key's icon; null for any other key or a group without an icon.</summary>
    public static IValueConverter IconOf { get; } = new FuncValueConverter<object?, Geometry?>(key =>
        key is GridGroup { IconKey: { Length: > 0 } icon } ? ToolbarButton.ResolveIcon(icon) : null);

    /// <summary>True when the key is a <see cref="GridGroup"/> with an icon (the header shows the icon).</summary>
    public static IValueConverter HasIcon { get; } = new FuncValueConverter<object?, bool>(key => key is GridGroup { IconKey.Length: > 0 });
}
