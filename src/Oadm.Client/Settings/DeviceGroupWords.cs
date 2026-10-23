namespace Oadm.Client.Settings;

/// <summary>
/// The client's phrases of device groups (docs/spec/device-scope.md), the shared ones from <see cref="Oadm.Sdk.Devices.DeviceGroupMessages"/>:
/// one phrase per concept in the Users page, the user form, the group editor and Your role.
/// </summary>
public static class DeviceGroupWords
{
    /// <summary>Every device without a site, in the editor's site list.</summary>
    public const string ThisServer = Oadm.Sdk.Devices.DeviceGroupMessages.ThisServer;

    /// <summary>The choice "every device" of the user form.</summary>
    public const string AllDevices = Oadm.Sdk.Devices.DeviceGroupMessages.AllDevices;

    /// <summary>A user whose group was deleted.</summary>
    public const string NoDevices = Oadm.Sdk.Devices.DeviceGroupMessages.NoDevices;

    /// <summary>The Devices field has no choice (the user's group was deleted).</summary>
    public const string ChooseDevices = Oadm.Sdk.Devices.DeviceGroupMessages.ChooseDevices;

    /// <summary>The hint of the Devices field for an administrator.</summary>
    public const string AdministratorsHaveAll = "Administrators always have all devices.";

    /// <summary>The note of the user form and the role editor on server-wide services.</summary>
    public const string ServerWideNote = "Users with a device group view the NTP server, DHCP server and PKI pages but cannot change them.";

    /// <summary>Add devices for a user whose device group has no site (the server's refusal).</summary>
    public const string CannotAdd = Oadm.Sdk.Devices.DeviceGroupMessages.CannotAdd;

    /// <summary>"Used by device group Building B" (deleting a tag or removing a site a group uses).</summary>
    public static string UsedBy(IReadOnlyCollection<string> groupNames) => Oadm.Sdk.Devices.DeviceGroupMessages.UsedBy(groupNames);

    /// <summary>"412 devices", "1 device".</summary>
    public static string Devices(int count) =>
        string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{count:N0} {(count == 1 ? "device" : "devices")}");
}
