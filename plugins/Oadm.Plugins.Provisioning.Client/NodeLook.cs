using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>
/// How a node kind looks on the canvas of the profile editor: its icon and its colour from the theme's categorical palette.
/// A kind this version of the client does not know gets the plugin icon and the neutral colour.
/// </summary>
public static class NodeLook
{
    public static string IconKey(NodeKind kind) => kind switch
    {
        NodeKind.SetPassword => "key",
        NodeKind.Network => "network",
        NodeKind.DateTime => "clock",
        NodeKind.UpgradeOnline => "cloud",
        NodeKind.UpgradeFiles => "firmware",
        NodeKind.Configuration => "sliders",
        NodeKind.Users => "users",
        NodeKind.Certificate => "shield",
        NodeKind.Dot1x => "lock",
        NodeKind.SdCard => "hardDrive",
        NodeKind.Autofocus => "focus",
        NodeKind.VapixCommands => "command",
        _ => "plugin",
    };

    public static string BrushKey(NodeKind kind) => kind switch
    {
        NodeKind.SetPassword => "Oadm.Tag.AmberBrush",
        NodeKind.Network => "Oadm.Tag.TealBrush",
        NodeKind.DateTime => "Oadm.Tag.GreenBrush",
        NodeKind.UpgradeOnline or NodeKind.UpgradeFiles => "Oadm.Tag.OrangeBrush",
        NodeKind.Autofocus => "Oadm.Tag.TealBrush",
        NodeKind.Configuration or NodeKind.SdCard => "Oadm.Tag.PinkBrush",
        NodeKind.Users => "Oadm.Tag.BlueBrush",
        NodeKind.Certificate or NodeKind.Dot1x => "Oadm.Tag.VioletBrush",
        NodeKind.VapixCommands => "Oadm.Tag.RedBrush",
        _ => "Oadm.Tag.NeutralBrush",
    };

    /// <summary>The kinds in the order of the node list (the usual order in a flow); a kind not listed comes last.</summary>
    public static IReadOnlyList<NodeKind> Order { get; } =
    [
        NodeKind.SetPassword, NodeKind.UpgradeOnline, NodeKind.UpgradeFiles, NodeKind.Network, NodeKind.DateTime,
        NodeKind.Configuration, NodeKind.Users, NodeKind.Certificate, NodeKind.Dot1x, NodeKind.SdCard, NodeKind.Autofocus,
        NodeKind.VapixCommands,
    ];

    /// <summary>The place of the kind in <see cref="Order"/>; a kind not listed comes last.</summary>
    public static int Rank(NodeKind kind)
    {
        for (var i = 0; i < Order.Count; i++)
        {
            if (Order[i] == kind)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>The line below the kind's name in the node list.</summary>
    public static string Description(NodeKind kind) => kind switch
    {
        NodeKind.SetPassword => "The first root password",
        NodeKind.UpgradeOnline => "The newest AXIS OS from axis.com",
        NodeKind.UpgradeFiles => "AXIS OS from your files, per model",
        NodeKind.Network => "Address, DNS servers and search domain",
        NodeKind.DateTime => "Time zone and NTP servers",
        NodeKind.Configuration => "Settings from a file or a template camera",
        NodeKind.Users => "Adds user accounts",
        NodeKind.Certificate => "The HTTPS certificate from PKI",
        NodeKind.Dot1x => "Network access with PKI's 802.1X policy",
        NodeKind.SdCard => "Formats or encrypts the SD card",
        NodeKind.Autofocus => "Finds the focus",
        NodeKind.VapixCommands => "Commands of the VAPIX Commander library",
        _ => string.Empty,
    };

    /// <summary>The group heading of the kind in the node list.</summary>
    public static string Group(NodeKind kind) => kind switch
    {
        NodeKind.SetPassword => "First steps",
        NodeKind.UpgradeOnline or NodeKind.UpgradeFiles => "AXIS OS",
        NodeKind.Network or NodeKind.DateTime or NodeKind.Configuration or NodeKind.Users => "Settings",
        NodeKind.Certificate or NodeKind.Dot1x => "Security",
        NodeKind.SdCard or NodeKind.Autofocus or NodeKind.VapixCommands => "Camera",
        _ => "Last steps",
    };
}
