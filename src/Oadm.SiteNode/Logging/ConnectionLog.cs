using System.Globalization;
using System.Text;
using System.Threading.Channels;

using Oadm.SiteNode.Admin;

namespace Oadm.SiteNode.Logging;

/// <summary>
/// The node's own record of the connections through it (docs/sites.md, "Connection log"): time, server, target, short or
/// permanent, result. Never contents. The newest 10,000 in memory, every line also in a daily file
/// (logs/connections-yyyyMMdd.log) kept 30 days and at most 1 GB together (the oldest files go first).
/// Bounded at every step: the same line (server, target, kind, result) again within the same minute is only counted and
/// written once as "... (N more)" when the minute is over; at most 10,000 lines wait for the disk, beyond that they are
/// dropped and counted in one line. Writes to the file happen on a background loop.
/// </summary>
public sealed class ConnectionLog : IAsyncDisposable
{
    public const int DefaultCapacity = 10_000;
    public const int KeepDays = 30;
    public const string FilePrefix = "connections-";

    /// <summary>All connection log files together.</summary>
    public const long DefaultMaxBytes = 1024L * 1024 * 1024;

    /// <summary>Lines waiting for the disk; more are dropped and counted.</summary>
    public const int MaxPending = 10_000;

    // The folder is checked against the size cap after this much was written.
    private const long PruneEveryBytes = 16 * 1024 * 1024;

    private readonly ConnectionLogEntry[] _ring;
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly string? _folder;
    private readonly long _maxBytes;
    private readonly Channel<ConnectionLogEntry> _pending;
    private readonly Dictionary<(string Server, string Target, string Kind, string Result), int> _repeats = [];
    private readonly Task _writer;
    private DateTimeOffset _minute;
    private int _next;
    private int _count;
    private long _written;
    private long _added;
    private long _dropped;
    private long _sincePrune;
    private string _day = "";

    /// <param name="folder">The logs folder; null keeps the log in memory only.</param>
    public ConnectionLog(string? folder, TimeProvider time, int capacity = DefaultCapacity, long maxBytes = DefaultMaxBytes, int maxPending = MaxPending)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPending, 1);
        _ring = new ConnectionLogEntry[capacity];
        _time = time;
        _folder = folder;
        _maxBytes = maxBytes;
        _pending = Channel.CreateBounded<ConnectionLogEntry>(new BoundedChannelOptions(maxPending) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        if (folder is not null)
            Directory.CreateDirectory(folder);
        _writer = Task.Run(WriteLoopAsync);
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _count;
        }
    }

    /// <summary>Lines that never reached the file because too many waited for the disk.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Add(string server, string target, string kind, string result)
    {
        var now = _time.GetUtcNow();
        var entry = new ConnectionLogEntry(now, OneLine(server), OneLine(target), OneLine(kind), OneLine(result));
        List<ConnectionLogEntry> lines;
        lock (_lock)
        {
            lines = RollRepeats(now);
            var key = (entry.Server, entry.Target, entry.Kind, entry.Result);
            if (_repeats.TryGetValue(key, out var seen))
            {
                _repeats[key] = seen + 1; // written as "(N more)" when the minute is over
            }
            else
            {
                _repeats[key] = 0;
                lines.Add(entry);
            }

            foreach (var line in lines)
                Keep(line);
        }

        foreach (var line in lines)
            Queue(line);
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ConnectionLogEntry> Newest(int count)
    {
        List<ConnectionLogEntry> rolled;
        ConnectionLogEntry[] result;
        lock (_lock)
        {
            rolled = RollRepeats(_time.GetUtcNow());
            foreach (var line in rolled)
                Keep(line);
            var n = Math.Clamp(count, 0, _count);
            result = new ConnectionLogEntry[n];
            for (var i = 0; i < n; i++)
                result[i] = _ring[((_next - 1 - i) % _ring.Length + _ring.Length) % _ring.Length];
        }

        foreach (var line in rolled)
            Queue(line);
        return result;
    }

    /// <summary>Waits until every line added so far is in the file (tests, shutdown); the counts of the current minute are written first.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        List<ConnectionLogEntry> rolled;
        long target;
        lock (_lock)
        {
            rolled = RollRepeats(DateTimeOffset.MaxValue);
            foreach (var line in rolled)
                Keep(line);
        }

        foreach (var line in rolled)
            Queue(line);
        lock (_lock)
            target = _added;
        while (Interlocked.Read(ref _written) < target && !_writer.IsCompleted)
            await Task.Delay(10, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await FlushAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _pending.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    /// <summary>The file of a day: logs/connections-20261019.log.</summary>
    public static string FileName(DateTimeOffset day) => FilePrefix + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";

    // Under the lock: once the minute is over, the lines that came again in it as one line each with their count.
    private List<ConnectionLogEntry> RollRepeats(DateTimeOffset now)
    {
        var lines = new List<ConnectionLogEntry>();
        var minute = now == DateTimeOffset.MaxValue ? now : new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, now.Offset);
        if (minute == _minute)
            return lines;
        var clock = _time.GetUtcNow();
        var end = _minute.AddMinutes(1).AddTicks(-1);
        if (end > clock)
            end = clock; // a flush in the middle of the minute
        foreach (var ((server, target, kind, result), more) in _repeats)
        {
            if (more > 0)
                lines.Add(new ConnectionLogEntry(end, server, target, kind, $"{result} ({more.ToString(CultureInfo.InvariantCulture)} more)"));
        }

        _repeats.Clear();
        if (now != DateTimeOffset.MaxValue)
            _minute = minute;
        return lines;
    }

    // Under the lock: into the in-memory ring.
    private void Keep(ConnectionLogEntry entry)
    {
        _ring[_next] = entry;
        _next = (_next + 1) % _ring.Length;
        _count = Math.Min(_count + 1, _ring.Length);
        _added++;
    }

    private void Queue(ConnectionLogEntry entry)
    {
        if (_pending.Writer.TryWrite(entry))
            return;
        // The disk does not keep up: never wait or grow, count instead.
        Interlocked.Increment(ref _dropped);
        Interlocked.Increment(ref _written);
    }

    private async Task WriteLoopAsync()
    {
        var lines = new StringBuilder();
        long reportedDrops = 0;
        while (await _pending.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // Everything waiting goes into the file in one write per day.
            var batch = 0;
            string? day = null;
            DateTimeOffset dayTime = default;
            lines.Clear();
            var dropped = Interlocked.Read(ref _dropped);
            if (dropped > reportedDrops && _pending.Reader.TryPeek(out var first))
            {
                lines.Append(first.Time.ToString("O", CultureInfo.InvariantCulture)).Append("\t\t\tLog\t")
                    .Append((dropped - reportedDrops).ToString(CultureInfo.InvariantCulture)).Append(" lines were not written: the disk did not keep up\n");
                reportedDrops = dropped;
            }

            while (batch < 1000 && _pending.Reader.TryPeek(out var next) && (day is null || string.Equals(FileName(next.Time), day, StringComparison.Ordinal)))
            {
                _pending.Reader.TryRead(out var entry);
                day ??= FileName(entry!.Time);
                dayTime = entry!.Time;
                lines.Append(entry.Time.ToString("O", CultureInfo.InvariantCulture)).Append('\t').Append(entry.Server).Append('\t')
                    .Append(entry.Target).Append('\t').Append(entry.Kind).Append('\t').Append(entry.Result).Append('\n');
                batch++;
            }

            if (_folder is not null && day is not null)
            {
                try
                {
                    var text = lines.ToString();
                    _sincePrune += Encoding.UTF8.GetByteCount(text);
                    if (!string.Equals(day, _day, StringComparison.Ordinal) || _sincePrune >= Math.Min(PruneEveryBytes, _maxBytes))
                    {
                        _day = day;
                        _sincePrune = 0;
                        Prune(dayTime, day);
                    }

                    await File.AppendAllTextAsync(Path.Combine(_folder, day), text, Encoding.UTF8).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The log never stops a connection; the in-memory part stays.
                }
            }

            Interlocked.Add(ref _written, batch);
        }
    }

    // Files older than 30 days go; then the oldest until all together fit in the size cap. The file of the day being
    // written is emptied last of all when it alone is over the cap.
    private void Prune(DateTimeOffset now, string current)
    {
        var oldest = now.AddDays(-KeepDays).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var files = new List<FileInfo>();
        foreach (var file in new DirectoryInfo(_folder!).EnumerateFiles(FilePrefix + "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file.Name);
            var date = name.Length > FilePrefix.Length ? name[FilePrefix.Length..] : "";
            if (date.Length == 8 && string.CompareOrdinal(date, oldest) < 0)
                Delete(file);
            else
                files.Add(file);
        }

        var total = files.Sum(f => f.Length);
        foreach (var file in files.OrderBy(f => string.Equals(f.Name, current, StringComparison.Ordinal) ? 1 : 0).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            if (total <= _maxBytes)
                break;
            total -= file.Length;
            Delete(file);
        }
    }

    private static void Delete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string OneLine(string? text) =>
        string.IsNullOrEmpty(text) ? "" : new string([.. text.Select(ch => char.IsControl(ch) ? ' ' : ch)]);
}
