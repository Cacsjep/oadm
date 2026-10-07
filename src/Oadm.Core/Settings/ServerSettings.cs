namespace Oadm.Core.Settings;

/// <summary>Keys of the Goal 1 server settings.</summary>
public static class SettingKeys
{
    public const string PollingIntervalSeconds = "Polling.IntervalSeconds";
    public const string ScanParallelism = "Scan.Parallelism";
    public const string ScanTimeoutMs = "Scan.TimeoutMs";
    public const string ServerName = "Server.Name";
    public const string ListenUrl = "Server.ListenUrl";

    /// <summary>Prefix of plugin settings: <c>Plugin:&lt;pluginId&gt;:&lt;key&gt;</c>.</summary>
    public const string PluginPrefix = "Plugin:";
}

/// <summary>Typed snapshot of all Goal 1 server settings (defaults applied).</summary>
public sealed record ServerSettings(
    int PollingIntervalSeconds,
    int ScanParallelism,
    int ScanTimeoutMs,
    string ServerName,
    string ListenUrl)
{
    public const int DefaultPollingIntervalSeconds = 60;
    public const int DefaultScanParallelism = 32;
    public const int DefaultScanTimeoutMs = 1500;
    public const string DefaultListenUrl = "http://0.0.0.0:5080";

    public static ServerSettings Defaults => new(
        DefaultPollingIntervalSeconds,
        DefaultScanParallelism,
        DefaultScanTimeoutMs,
        DefaultServerName(),
        DefaultListenUrl);

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
