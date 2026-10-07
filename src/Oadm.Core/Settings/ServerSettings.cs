namespace Oadm.Core.Settings;

/// <summary>Keys of the Goal 1 server settings.</summary>
public static class SettingKeys
{
    public const string PollingIntervalSeconds = "Polling.IntervalSeconds";
    public const string PollingFullRefreshMinutes = "Polling.FullRefreshMinutes";
    public const string ScanParallelism = "Scan.Parallelism";
    public const string ScanTimeoutMs = "Scan.TimeoutMs";
    public const string ServerName = "Server.Name";
    public const string ListenUrl = "Server.ListenUrl";

    /// <summary>Largest accepted upload (firmware, ACAP packages) in MB. Not on the settings page yet.</summary>
    public const string UploadsMaxMegabytes = "Uploads.MaxMegabytes";

    /// <summary>Uploads older than this are deleted by the server. Not on the settings page yet.</summary>
    public const string UploadsRetentionHours = "Uploads.RetentionHours";

    /// <summary>Finished tasks older than this many days are deleted from the history. Int, default 90, 0..3650 (0 = no age limit). Not on the settings page yet.</summary>
    public const string TasksRetentionDays = "Tasks.RetentionDays";

    /// <summary>The history keeps at most this many tasks; older finished ones are deleted. Int, default 50000, 0 or 100..1000000 (0 = no limit). Not on the settings page yet.</summary>
    public const string TasksMaxHistory = "Tasks.MaxHistory";
    /// <summary>Add devices by host name when one is known, otherwise by IP address. Bool, default false.</summary>
    public const string DevicesUseHostName = "Devices.UseHostName";

    /// <summary>A zero-conf scan of the add page ends after this many seconds. Int, default 30, 5..300.</summary>
    public const string DiscoveryZeroConfSeconds = "Discovery.ZeroConfSeconds";

    /// <summary>Prefix of plugin settings: <c>Plugin:&lt;pluginId&gt;:&lt;key&gt;</c>.</summary>
    public const string PluginPrefix = "Plugin:";

    /// <summary>Storage key of a plugin setting: <c>Plugin:&lt;pluginId&gt;:&lt;key&gt;</c>. Used by every plugin settings store.</summary>
    public static string PluginKey(string pluginId, string key)
    {
        ValidatePluginId(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return PluginPrefix + pluginId + ":" + key;
    }

    /// <summary>Plugin ids must be non-empty and must not contain ':' (the key separator).</summary>
    public static void ValidatePluginId(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (pluginId.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Plugin id must not contain ':'.", nameof(pluginId));
        }
    }

    /// <summary>Throws <see cref="ArgumentException"/> when a non-null value is not valid JSON.</summary>
    public static void ValidateJson(string key, string? valueJson)
    {
        if (valueJson is null)
        {
            return;
        }

        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(valueJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ArgumentException($"Value of '{key}' is not valid JSON.", nameof(valueJson), ex);
        }
    }
}

/// <summary>Typed snapshot of all Goal 1 server settings (defaults applied).</summary>
public sealed record ServerSettings(
    int PollingIntervalSeconds,
    int ScanParallelism,
    int ScanTimeoutMs,
    string ServerName,
    string ListenUrl,
    int FullRefreshMinutes = ServerSettings.DefaultFullRefreshMinutes,
    bool UseHostName = ServerSettings.DefaultUseHostName,
    int ZeroConfSeconds = ServerSettings.DefaultZeroConfSeconds)
{
    public const int DefaultPollingIntervalSeconds = 60;
    public const int DefaultFullRefreshMinutes = 10;
    public const int MinFullRefreshMinutes = 1;
    public const int MaxFullRefreshMinutes = 1440;
    public const int DefaultScanParallelism = 32;
    public const int DefaultScanTimeoutMs = 1500;
    public const string DefaultListenUrl = "http://0.0.0.0:5080";
    public const int DefaultUploadsMaxMegabytes = 2048;
    public const int MaxUploadsMaxMegabytes = 65_536;
    public const int DefaultUploadsRetentionHours = 24;
    public const int MaxUploadsRetentionHours = 8760;
    public const int DefaultTasksRetentionDays = 90;
    public const int MaxTasksRetentionDays = 3650;
    public const int DefaultTasksMaxHistory = 50_000;
    public const int MinTasksMaxHistory = 100;
    public const int MaxTasksMaxHistory = 1_000_000;
    public const bool DefaultUseHostName = false;
    public const int DefaultZeroConfSeconds = 30;
    public const int MinZeroConfSeconds = 5;
    public const int MaxZeroConfSeconds = 300;

    public static ServerSettings Defaults => new(
        DefaultPollingIntervalSeconds,
        DefaultScanParallelism,
        DefaultScanTimeoutMs,
        DefaultServerName(),
        DefaultListenUrl,
        DefaultFullRefreshMinutes,
        DefaultUseHostName,
        DefaultZeroConfSeconds);

    /// <summary>The machine host name (DNS host name, falling back to the NetBIOS/machine name).</summary>
    public static string DefaultServerName()
    {
        try
        {
            var host = System.Net.Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(host))
            {
                return host;
            }
        }
        catch (System.Net.Sockets.SocketException)
        {
            // fall through
        }

        return Environment.MachineName;
    }
}

/// <summary>Raised after a setting was written. <see cref="ValueJson"/> is null when the key was deleted.</summary>
public sealed class SettingChangedEventArgs(string key, string? valueJson) : EventArgs
{
    public string Key { get; } = key;
    public string? ValueJson { get; } = valueJson;
}
