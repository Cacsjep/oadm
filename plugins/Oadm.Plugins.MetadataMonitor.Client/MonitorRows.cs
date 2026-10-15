using System.Globalization;
using System.Text.Json;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.MetadataMonitor.Client;

/// <summary>One row of the message list.</summary>
public sealed class MessageRow(MetadataMessage message)
{
    public MetadataMessage Message { get; } = message;

    public long Seq => Message.Seq;

    public string Timestamp { get; } = Format(message.UtcTime);

    public string Category => Message.Category;

    public string Topic => Message.Topic;

    /// <summary>Time only (the date is in Timestamp): "09:30:01.000".</summary>
    public string CaptureTime { get; } = message.CaptureUtc.UtcDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string Operation => Message.Operation ?? string.Empty;

    public string Info => Message.Info;

    public string Xml => Message.Xml;

    /// <summary>Filter: case-insensitive over topic, info and the raw XML.</summary>
    public bool Matches(string filter) =>
        filter.Length == 0
        || Topic.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Info.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Xml.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string Format(DateTimeOffset? time) =>
        time?.UtcDateTime.ToString(MetadataMonitorPluginInfo.TimeFormat, CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>The chosen camera: "10.0.0.48 · P3265-V" (the address alone without a model).</summary>
public sealed record CameraOption(Guid Id, string Address, string Label)
{
    public override string ToString() => Label;

    /// <summary>Devices the page offers: video devices and I/O modules.</summary>
    public static bool Fits(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.HasVideo || device.Category == DeviceCategory.IoModule;
    }

    public static CameraOption Of(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new CameraOption(device.Id, device.Address, string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Address} · {device.Model}");
    }
}

/// <summary>What the page remembers per client: the height of the detail pane.</summary>
public sealed class MetadataClientSettings
{
    public double DetailHeight { get; set; } = 260;
}

/// <summary>
/// <c>LocalApplicationData/Oadm/plugins/oadm.metadata-monitor/client.json</c>. Failures are ignored (defaults):
/// remembering is a convenience only.
/// </summary>
public sealed class MetadataClientSettingsStore(string? path = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Oadm",
        "plugins",
        MetadataMonitorPluginInfo.PluginId,
        "client.json");

    public MetadataClientSettings Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<MetadataClientSettings>(File.ReadAllText(Path), MetadataJson.Options) ?? new MetadataClientSettings()
                : new MetadataClientSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new MetadataClientSettings();
        }
    }

    public void Save(MetadataClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings, MetadataJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // convenience only
        }
    }
}
