namespace Oadm.Plugins.MetadataMonitor;

/// <summary>Timing and limits of the Metadata Monitor (tests shorten them).</summary>
public sealed class MetadataMonitorOptions
{
    /// <summary>New messages are pushed to the page at most this often.</summary>
    public TimeSpan BatchInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Messages per batch; above that the oldest of the batch are dropped and counted as lost.</summary>
    public int MaxBatchMessages { get; init; } = MetadataMonitorPluginInfo.MaxBatchMessages;

    /// <summary>Waits before reconnecting after a broken connection (the last one repeats).</summary>
    public IReadOnlyList<TimeSpan> ReconnectDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10)];

    /// <summary>A stream without keep-alive from its page for this long ends (client closed or gone).</summary>
    public TimeSpan LeaseTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How often expired leases are looked for.</summary>
    public TimeSpan LeaseCheckInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Streams running at the same time (all pages of all clients).</summary>
    public int MaxStreams { get; init; } = 16;

    /// <summary>Largest characters of one pushed event (the host drops events above 1 M characters).</summary>
    public int MaxEventCharacters { get; init; } = 700_000;

    public TimeProvider Time { get; init; } = TimeProvider.System;
}
