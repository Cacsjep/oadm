using System.Globalization;

using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tasks;

/// <summary>
/// How the devices of a task are named, shared by the tasks grid and the task details window:
/// the address shown in the device grid (IP or host name), or "removed device 1a2b3c4d" for a
/// device that is no longer managed.
/// </summary>
public static class TaskDeviceLabels
{
    /// <summary>Addresses listed in the Devices cell before the rest is summarized as "+N".</summary>
    public const int MaxListed = 2;

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

    /// <summary>"10.0.0.48", "10.0.0.48, 10.0.0.200" or "10.0.0.48, 10.0.0.200 +3".</summary>
    public static string Summary(IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        string listed = string.Join(", ", labels.Take(MaxListed));
        return labels.Count > MaxListed
            ? string.Create(CultureInfo.CurrentCulture, $"{listed} +{labels.Count - MaxListed}")
            : listed;
    }

    /// <summary>One line per device: "10.0.0.48: Failed - Connection refused". Null without devices.</summary>
    public static string? Tooltip(IReadOnlyList<TaskDeviceResult> results, IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(labels);
        if (results.Count == 0)
        {
            return null;
        }

        return string.Join(Environment.NewLine, results.Select((r, i) =>
        {
            string line = labels[i] + ": " + TaskRowViewModel.ToText(r.State);
            return string.IsNullOrWhiteSpace(r.Message) ? line : line + " - " + r.Message.Trim();
        }));
    }
}
