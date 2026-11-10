namespace Oadm.Plugins.Shared.Vision;

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
    public void Stop(long started) => Stop(started, 1);

    /// <summary>
    /// A call of the graphics card ended with <paramref name="filled"/> of its places in use: only that share counts, so 100 % still
    /// means no more fits (blank places take the next lines at no cost).
    /// </summary>
    public void Stop(long started, double filled) => Interlocked.Add(ref _ticks, (long)(time.GetElapsedTime(started).Ticks * filled));
}

/// <summary>
/// The recognition's busy time over the CPU workers and the graphics card together (docs/spec/ocr.md "Runtime stats",
/// recognition load), in worker time: each time it is read it grows by the larger of the workers' busy time and the card's
/// busy time times <paramref name="workers"/> since the last read. With the capacity of <paramref name="workers"/> the load is
/// the larger of the CPU's and the card's load per sample, so 100 % still means no more fits: every worker busy, or the card
/// never idle (its calls run one at a time).
/// </summary>
internal sealed class CombinedBusy(BusyMeter cpu, BusyMeter card, int workers, BusyMeter? cardReader = null)
{
    private readonly object _lock = new();
    private TimeSpan _cpuSeen;
    private TimeSpan _cardSeen;
    private TimeSpan _readerSeen;
    private TimeSpan _total;

    /// <summary>The combined busy time so far (each read takes the growth since the last one).</summary>
    public TimeSpan Busy
    {
        get
        {
            lock (_lock)
            {
                var cpuNow = cpu.Busy;
                var cardNow = card.Busy;
                var cpuGrowth = cpuNow - _cpuSeen;
                var readerNow = cardReader?.Busy ?? TimeSpan.Zero;
                var finderGrowth = cardNow - _cardSeen;
                var readerGrowth = readerNow - _readerSeen;
                var cardGrowth = (finderGrowth > readerGrowth ? finderGrowth : readerGrowth) * workers;
                _readerSeen = readerNow;
                _total += cpuGrowth > cardGrowth ? cpuGrowth : cardGrowth;
                _cpuSeen = cpuNow;
                _cardSeen = cardNow;
                return _total;
            }
        }
    }
}
