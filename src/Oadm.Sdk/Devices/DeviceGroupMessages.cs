namespace Oadm.Sdk.Devices;

/// <summary>
/// The shared user texts of device groups (docs/spec/device-scope.md): one phrase per concept for the server, the client
/// and the plugins, so a refusal of the server and the client's own wording never differ.
/// </summary>
public static class DeviceGroupMessages
{
    /// <summary>The name of "every device without a site" in the group editor, summaries and Your role.</summary>
    public const string ThisServer = "This server";

    /// <summary>The choice "every device" of the user form and the owner of shared objects.</summary>
    public const string AllDevices = "All devices";

    /// <summary>A user or an object without devices (its group was deleted).</summary>
    public const string NoDevices = "No devices";

    /// <summary>The refusal of a method only users with all devices may call.</summary>
    public const string AllDevicesOnly = "Only users with all devices can do this.";

    /// <summary>A user or an object needs devices: no choice made, or its group was deleted.</summary>
    public const string ChooseDevices = "Choose All devices or a device group.";

    /// <summary>A user whose device group has no site (tags or single devices only) cannot add devices.</summary>
    public const string CannotAdd = "Your device group has no site, so you cannot add devices.";

    /// <summary>The refusal of a save by a user without devices (no group, or the group was deleted).</summary>
    public const string NoDevicesRefusal = "Your account has no devices.";

    /// <summary>The note of a tag or site a group uses, before it is deleted: "Used by device group Building B".</summary>
    public static string UsedBy(IReadOnlyCollection<string> groupNames)
    {
        ArgumentNullException.ThrowIfNull(groupNames);
        return groupNames.Count == 1
            ? "Used by device group " + groupNames.First()
            : "Used by device groups " + string.Join(", ", groupNames);
    }
}
