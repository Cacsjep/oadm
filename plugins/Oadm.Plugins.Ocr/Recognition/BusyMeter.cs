namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// The time model calls held their worker (docs/spec/ocr.md "Runtime stats", recognition load): the sum over all calls since
/// the start, one add per call (no allocation). The page's load is this sum's growth over a window divided by the window times
/// the recognition's capacity.
/// </summary>
internal sealed class BusyMeter(TimeProvider time)
{
    private long _ticks;

    /// <summary>The time all model calls held their worker so far.</summary>
    public TimeSpan Busy => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    /// <summary>A call starts: its timestamp for <see cref="Stop"/>.</summary>
    public long Start() => time.GetTimestamp();

    /// <summary>The call that started at <paramref name="started"/> ended.</summary>
    public void Stop(long started) => Interlocked.Add(ref _ticks, time.GetElapsedTime(started).Ticks);
}
