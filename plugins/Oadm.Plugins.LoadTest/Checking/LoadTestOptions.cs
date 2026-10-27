namespace Oadm.Plugins.LoadTest.Checking;

/// <summary>Timing and limits of the check; tests pass a <c>FakeTimeProvider</c> and shorter phases.</summary>
public sealed class LoadTestOptions
{
    /// <summary>The clock: one sample per second of it, the timeouts and the run times.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Devices whose sources are read at the same time while planning (16).</summary>
    public int ListParallelism { get; init; } = 16;

    /// <summary>Time for one device's source list (15 s).</summary>
    public TimeSpan ListTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a device's source list is reused (Plan, then Start): 10 minutes.</summary>
    public TimeSpan SourceCacheTime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Exposure reads and writes at the same time (16).</summary>
    public int ExposureParallelism { get; init; } = 16;

    /// <summary>Time for one exposure write or restore (10 s).</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Seconds per phase instead of the request's minutes (tests); null = minutes x 60.</summary>
    public int? PhaseSeconds { get; init; }

    /// <summary>Rows per live event (the rest follow in more events of the same second).</summary>
    public int MaxRowsPerEvent { get; init; } = 200;
}
