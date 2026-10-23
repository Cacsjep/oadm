namespace Oadm.Client.Settings;

/// <summary>
/// The client's phrases of device groups (docs/spec/device-scope.md), the same as the server's <c>DeviceGroupTexts</c>:
/// one phrase per concept in the Users page, the user form, the group editor and Your role.
/// </summary>
public static class DeviceGroupWords
{
    /// <summary>Every device without a site, in the editor's site list.</summary>
    public const string ThisServer = "This server";

    /// <summary>The choice "every device" of the user form.</summary>
    public const string AllDevices = "All devices";

    /// <summary>A user whose group was deleted.</summary>
    public const string NoDevices = "No devices";

    /// <summary>The Devices field has no choice (the user's group was deleted).</summary>
    public const string ChooseDevices = "Choose All devices or a device group.";

    /// <summary>The hint of the Devices field for an administrator.</summary>
    public const string AdministratorsHaveAll = "Administrators always have all devices.";

    /// <summary>The note of the user form and the role editor on server-wide services.</summary>
    public const string ServerWideNote = "Users with a device group view the NTP server, DHCP server and PKI pages but cannot change them.";

    /// <summary>"412 devices", "1 device".</summary>
    public static string Devices(int count) =>
        string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{count:N0} {(count == 1 ? "device" : "devices")}");
}
