using System.Globalization;
using System.Net;
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

/// <summary>A camera of the select: "P3265-V (10.0.0.48)".</summary>
public sealed class CameraOption(Guid id, string label, string address)
{
    public Guid Id { get; } = id;

    public string Label { get; } = label;

    public string Address { get; } = address;

    public override string ToString() => Label;

    /// <summary>Managed video and I/O devices, sorted by IPv4 address (numerically; host names and IPv6 after them).</summary>
    public static List<CameraOption> From(IEnumerable<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return devices
            .Where(d => d.HasVideo || d.Category == DeviceCategory.IoModule)
            .Select(d => (Key: SortKey(d.Address), Option: new CameraOption(d.Id, string.IsNullOrEmpty(d.Model) ? d.Address : $"{d.Model} ({d.Address})", d.Address)))
            .OrderBy(x => x.Key.Group)
            .ThenBy(x => x.Key.Number)
            .ThenBy(x => x.Option.Address, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Option)
            .ToList();
    }

    private static (int Group, uint Number) SortKey(string address)
    {
        var host = address;
        var colon = host.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && host.IndexOf(':', colon + 1) < 0)
        {
            host = host[..colon]; // IPv4 with a port
        }

        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return (0, ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]);
        }

        return (1, 0);
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
