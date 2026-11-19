using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>
/// How a node kind looks on the canvas of the profile editor: its icon and its colour from the theme's categorical palette.
/// A kind this version of the page does not know gets the plugin icon and the neutral colour. A camera's flow colours the
/// nodes by their result instead (<see cref="CameraFlow"/>).
/// </summary>
public static class NodeLook
{
    public static string IconKey(NodeKind kind) => kind switch
    {
        NodeKind.SetPassword => "key",
        NodeKind.AddToOadm => "devices",
        NodeKind.WaitForApproval => "hourglass",
        NodeKind.SetIp => "network",
        NodeKind.DateTime => "clock",
        NodeKind.UpgradeOnline => "cloud",
        NodeKind.UpgradeFiles => "firmware",
        NodeKind.NetworkSettings => "globe",
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
        NodeKind.AddToOadm => "Oadm.Tag.BlueBrush",
        NodeKind.WaitForApproval => "Oadm.Tag.VioletBrush",
        NodeKind.SetIp => "Oadm.Tag.TealBrush",
        NodeKind.DateTime => "Oadm.Tag.GreenBrush",
        NodeKind.UpgradeOnline or NodeKind.UpgradeFiles => "Oadm.Tag.OrangeBrush",
        NodeKind.NetworkSettings or NodeKind.Autofocus => "Oadm.Tag.TealBrush",
        NodeKind.Configuration or NodeKind.SdCard => "Oadm.Tag.PinkBrush",
        NodeKind.Users => "Oadm.Tag.BlueBrush",
        NodeKind.Certificate or NodeKind.Dot1x => "Oadm.Tag.VioletBrush",
        NodeKind.VapixCommands => "Oadm.Tag.RedBrush",
        _ => "Oadm.Tag.NeutralBrush",
    };

    /// <summary>The kinds in the order of the node list (the usual order in a flow); a kind not listed comes last.</summary>
    public static IReadOnlyList<NodeKind> Order { get; } =
    [
        NodeKind.SetPassword, NodeKind.AddToOadm, NodeKind.UpgradeOnline, NodeKind.UpgradeFiles, NodeKind.NetworkSettings, NodeKind.DateTime,
        NodeKind.Configuration, NodeKind.Users, NodeKind.Certificate, NodeKind.Dot1x, NodeKind.SdCard, NodeKind.Autofocus,
        NodeKind.VapixCommands, NodeKind.WaitForApproval, NodeKind.SetIp,
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
        NodeKind.AddToOadm => "Adds the camera to OADM with tags",
        NodeKind.UpgradeOnline => "The newest AXIS OS from axis.com",
        NodeKind.UpgradeFiles => "AXIS OS from your files, per model",
        NodeKind.NetworkSettings => "DNS servers and search domain",
        NodeKind.DateTime => "Time zone and NTP servers",
        NodeKind.Configuration => "Settings from a file or a template camera",
        NodeKind.Users => "Adds user accounts",
        NodeKind.Certificate => "The HTTPS certificate from PKI",
        NodeKind.Dot1x => "Network access with PKI's 802.1X policy",
        NodeKind.SdCard => "Formats or encrypts the SD card",
        NodeKind.Autofocus => "Finds the focus",
        NodeKind.VapixCommands => "Commands of the VAPIX Commander library",
        NodeKind.WaitForApproval => "Waits until you press Continue",
        NodeKind.SetIp => "A fixed address from a pool",
        _ => string.Empty,
    };

    /// <summary>The group heading of the kind in the node list.</summary>
    public static string Group(NodeKind kind) => kind switch
    {
        NodeKind.SetPassword or NodeKind.AddToOadm => "First steps",
        NodeKind.UpgradeOnline or NodeKind.UpgradeFiles => "AXIS OS",
        NodeKind.NetworkSettings or NodeKind.DateTime or NodeKind.Configuration or NodeKind.Users => "Settings",
        NodeKind.Certificate or NodeKind.Dot1x => "Security",
        NodeKind.SdCard or NodeKind.Autofocus or NodeKind.VapixCommands => "Camera",
        _ => "Last steps",
    };
}
