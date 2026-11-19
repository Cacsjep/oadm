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
}
