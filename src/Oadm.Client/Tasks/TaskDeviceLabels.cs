using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tasks;

/// <summary>
/// How the device of a task is named, shared by the tasks grid and the task details window: the
/// address shown in the device grid (IP or host name), or "removed device 1a2b3c4d" for a device
/// that is no longer managed. A task always targets exactly one device.
/// </summary>
public static class TaskDeviceLabels
{
    /// <summary>Length of the shortened device id of a removed device.</summary>
    public const int ShortIdLength = 8;

    /// <summary>The device grid address of <paramref name="deviceId"/>, or "removed device &lt;short id&gt;".</summary>
    public static string Label(string deviceId, DeviceStore devices)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(devices);
        DeviceRowViewModel? device = devices.Find(deviceId);
        return device is not null && device.DisplayAddress.Length > 0 ? device.DisplayAddress : RemovedLabel(deviceId);
    }

    public static string RemovedLabel(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        string shortId = deviceId.Length > ShortIdLength ? deviceId[..ShortIdLength] : deviceId;
        return "removed device " + shortId;
    }

    /// <summary>"10.0.0.48: Failed - Connection refused". Null without a device result.</summary>
    public static string? Tooltip(TaskDeviceResult? result, string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (result is null)
        {
            return null;
        }

        string line = label + ": " + TaskRowViewModel.ToText(result.State);
        return string.IsNullOrWhiteSpace(result.Message) ? line : line + " - " + result.Message.Trim();
    }
}
