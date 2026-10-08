using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Plugins;

/// <summary>
/// Plain-language texts for <see cref="ITaskPlugin.NotSupportedReason"/>: what is missing and what to do. The context
/// menu shows a task the selection cannot run greyed out with the reason as tooltip, e.g. "Needs AXIS OS 11.11 or later
/// (this device has 10.12.338)" for one device, "Needs AXIS OS 11.11 or later: 3 of 5 selected devices" for several.
/// Only claim a firmware version where the check really is a version; otherwise name the missing function.
/// </summary>
public static class TaskSupportReasons
{
    /// <summary>Shown when a plugin gives no reason (null, empty or throwing).</summary>
    public const string Default = "Not supported on this device";

    /// <summary>Longest reason the host passes on; longer ones are shortened with "…".</summary>
    public const int MaxLength = 160;

    /// <summary>The device's API list was not read yet (a device added a moment ago, or never reached).</summary>
    public const string ApisNotRead = "OADM has not read what this device supports yet: refresh the device";

    /// <summary>Start of a device-specific detail at the end of a reason, see <see cref="ThisDevice"/>.</summary>
    public const string ThisDevicePrefix = " (this device ";

    /// <summary>
    /// Why a device with this status cannot run a task, or null when the status allows it (Ok; Unknown too unless
    /// <paramref name="requireOk"/>).
    /// </summary>
    public static string? ForStatus(DeviceStatus status, bool requireOk = false) => status switch
    {
        DeviceStatus.Ok => null,
        DeviceStatus.Unknown => requireOk ? "OADM has not checked the device yet: refresh the device" : null,
        DeviceStatus.Unreachable => "The device does not answer",
        DeviceStatus.CredentialsRequired => "OADM cannot log in to the device: use Log in",
        DeviceStatus.PasswordNotSet => "The device has no password yet: use Set password",
        DeviceStatus.CertificateChanged => "The device's certificate changed since it was added",
        _ => Default,
    };

    /// <summary>"Needs AXIS OS 11.11 or later (this device has 10.12.338)"; without a known firmware version only the first part.</summary>
    public static string NeedsFirmware(string minimumVersion, IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return $"Needs AXIS OS {minimumVersion} or later" + FirmwareDetail(device);
    }

    /// <summary>
    /// "Needs the network settings API (this device has 5.51.7)" for a device whose API list does not have it, or
    /// <see cref="ApisNotRead"/> while the list is empty. <paramref name="what"/> names the function in plain words.
    /// </summary>
    public static string NeedsApi(string what, IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Apis.Count == 0 ? ApisNotRead : "Needs " + what + FirmwareDetail(device);
    }

    /// <summary>" (this device has 10.12.338)" or empty when the firmware version is unknown.</summary>
    public static string FirmwareDetail(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return string.IsNullOrWhiteSpace(device.FirmwareVersion) ? string.Empty : ThisDevice("has " + device.FirmwareVersion.Trim());
    }

    /// <summary>A detail about one device, appended at the end of a reason: " (this device has 10.12.338)".</summary>
    public static string ThisDevice(string detail) => ThisDevicePrefix + detail + ")";

    /// <summary>The reason without its trailing device detail (for a summary over several devices).</summary>
    public static string General(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var at = reason.LastIndexOf(ThisDevicePrefix, StringComparison.Ordinal);
        return at > 0 && reason.EndsWith(')') ? reason[..at] : reason;
    }

    /// <summary>
    /// The plugin's reason for the device, as the host shows it: trimmed, at most <see cref="MaxLength"/> characters,
    /// <see cref="Default"/> when the plugin gives none or throws (<paramref name="error"/> is then the exception).
    /// </summary>
    public static string Of(ITaskPlugin plugin, IDeviceInfo device, out Exception? error)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        error = null;
        string? reason;
        try
        {
            reason = plugin.NotSupportedReason(device);
        }
#pragma warning disable CA1031 // Plugin code is untrusted: a broken reason never breaks the menu.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            error = ex;
            return Default;
        }

        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            return Default;
        }

        return reason.Length <= MaxLength ? reason : reason[..(MaxLength - 1)].TrimEnd() + TaskPluginNames.Ellipsis;
    }
}
