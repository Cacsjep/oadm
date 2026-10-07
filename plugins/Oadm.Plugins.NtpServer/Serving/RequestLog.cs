using System.Globalization;

namespace Oadm.Plugins.NtpServer.Serving;

public enum RequestResult : byte
{
    Answered = 0,
    RateLimited = 1,
}

/// <summary>
/// The last <see cref="Capacity"/> requests in memory (ring buffer of value entries, no database). <see cref="Add"/>
/// allocates nothing; texts are made only when the page reads the log.
/// </summary>
public sealed class RequestLog(int capacity = NtpServerPluginInfo.MaxRequests)
{
    private readonly Slot[] _slots = new Slot[capacity];
    private readonly Lock _sync = new();
    private long _seq;

    public int Capacity => _slots.Length;

    /// <summary>Sequence number of the newest entry (0 = empty).</summary>
    public long LastSeq
    {
        get
        {
            lock (_sync)
            {
                return _seq;
            }
        }
    }

    /// <param name="timeUtc">Receive time.</param>
    /// <param name="client">Client key (<see cref="ClientAddress"/>).</param>
    /// <param name="offsetMilliseconds">Client minus server clock, NaN when unknown.</param>
    /// <param name="result">Answered or rate limited.</param>
    public void Add(DateTime timeUtc, UInt128 client, double offsetMilliseconds, RequestResult result)
    {
        lock (_sync)
        {
            _seq++;
            _slots[(int)(_seq % _slots.Length)] = new Slot(_seq, timeUtc, client, offsetMilliseconds, result);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_slots);
        }
    }

    /// <summary>Entries with a sequence number above <paramref name="afterSeq"/>, newest first.</summary>
    public IReadOnlyList<RequestEntry> Snapshot(long afterSeq = 0)
    {
        Slot[] copy;
        long last;
        lock (_sync)
        {
            copy = (Slot[])_slots.Clone();
            last = _seq;
        }

        var result = new List<RequestEntry>(copy.Length);
        for (var seq = last; seq > afterSeq && seq > last - copy.Length; seq--)
        {
            var slot = copy[(int)(seq % copy.Length)];
            if (slot.Seq != seq)
            {
                break; // cleared
            }

            result.Add(new RequestEntry(
                slot.Seq,
                slot.TimeUtc,
                ClientAddress.ToIPAddress(slot.Client).ToString(),
                double.IsNaN(slot.OffsetMilliseconds) ? null : Math.Round(slot.OffsetMilliseconds, 1),
                slot.Result == RequestResult.Answered ? RequestEntry.Answered : RequestEntry.RateLimited));
        }

        return result;
    }

    /// <summary>"+2 ms", "-1.3 s", "-" for the offset column.</summary>
    public static string FormatOffset(double? milliseconds)
    {
        if (milliseconds is not { } ms)
        {
            return "-";
        }

        var abs = Math.Abs(ms);
        if (abs < 0.5)
        {
            return "0 ms";
        }

        var sign = ms >= 0 ? "+" : "-";
        return abs >= 999.5
            ? sign + (abs / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s"
            : sign + Math.Round(abs, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " ms";
    }

    private readonly record struct Slot(long Seq, DateTime TimeUtc, UInt128 Client, double OffsetMilliseconds, RequestResult Result);
}
