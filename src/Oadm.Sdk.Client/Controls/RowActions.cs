using Avalonia.Controls;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The ⋯ menu of a table row, one look everywhere (the certificate tables, PKI's policies, the 802.1X switches, the EST
/// profiles; decision 2026-10-25): the everyday entries, a separator, the red ones last. The row's ⋯ button
/// (<c>Button.toolbar.iconOnly</c> with <c>Icon.more</c> and the tooltip <see cref="Tip"/>) calls <see cref="Show"/>.
/// </summary>
public static class RowActions
{
    /// <summary>The tooltip of a row's ⋯ button.</summary>
    public const string Tip = IssuedCertificateTexts.ActionsTip;

    /// <summary>Opens the menu of <paramref name="actions"/> below <paramref name="anchor"/>, right-aligned.</summary>
    public static void Show(Control anchor, IReadOnlyList<IssuedCertificateAction> actions)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(actions);
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var item in IssuedCertificateList.MenuItems(actions))
        {
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor);
    }
}
