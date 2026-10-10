namespace Oadm.Plugins.DeviceTriggers.Runtime;

/// <summary>Timing of the device triggers; tests shorten them.</summary>
public sealed class DeviceTriggersOptions
{
    /// <summary>"Connection lost" turns on after a problem lasted this long (user decision: 30 s).</summary>
    public TimeSpan LostDelay { get; init; } = DeviceTriggersPluginInfo.LostDelay;

    /// <summary>The event list of a connected device is read again this often (user decision: 5 min).</summary>
    public TimeSpan RecheckInterval { get; init; } = DeviceTriggersPluginInfo.RecheckInterval;

    /// <summary>WebSocket ping interval and pong timeout.</summary>
    public TimeSpan KeepAlive { get; init; } = DeviceTriggersPluginInfo.KeepAlive;

    /// <summary>
    /// After a filter is set the device sends the current state of every matching on/off event at once (no end marker):
    /// what arrives within this time is the device's full report.
    /// </summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>One request to the device (event list, session token).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Waits between reconnects: 1, 2, 4, 8, then 10 s.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10)];

    /// <summary>A device OADM cannot log in to (status) is checked again this often.</summary>
    public TimeSpan StatusRetry { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Problem timers, removed devices and page notifications are checked this often.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Removed devices leave their triggers; the device list is read this often.</summary>
    public TimeSpan DeviceSweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Devices whose event lists the editor reads at the same time.</summary>
    public int ReadParallelism { get; init; } = 16;

    /// <summary>A finished read of the editor is kept this long.</summary>
    public TimeSpan ReadJobLifetime { get; init; } = TimeSpan.FromMinutes(10);
}
