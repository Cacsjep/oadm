using System.Globalization;
using System.Text;
using System.Threading.Channels;

using Oadm.SiteNode.Admin;

namespace Oadm.SiteNode.Logging;

/// <summary>
/// The node's own record of the connections through it (docs/sites.md, "Connection log"): time, server, target, short or
/// permanent, result. Never contents. The newest 10,000 in memory, every line also in a daily file
/// (logs/connections-yyyyMMdd.log) kept 30 days. Writes to the file happen on a background loop.
/// </summary>
public sealed class ConnectionLog : IAsyncDisposable
{
    public const int DefaultCapacity = 10_000;
    public const int KeepDays = 30;
    public const string FilePrefix = "connections-";

    private readonly ConnectionLogEntry[] _ring;
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly string? _folder;
    private readonly Channel<ConnectionLogEntry> _pending = Channel.CreateUnbounded<ConnectionLogEntry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private int _next;
    private int _count;
    private long _written;
    private long _added;
    private string _day = "";

    /// <param name="folder">The logs folder; null keeps the log in memory only.</param>
    public ConnectionLog(string? folder, TimeProvider time, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _ring = new ConnectionLogEntry[capacity];
        _time = time;
        _folder = folder;
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

    public void Add(string server, string target, string kind, string result)
    {
        var entry = new ConnectionLogEntry(_time.GetUtcNow(), OneLine(server), OneLine(target), OneLine(kind), OneLine(result));
        lock (_lock)
        {
            _ring[_next] = entry;
            _next = (_next + 1) % _ring.Length;
            _count = Math.Min(_count + 1, _ring.Length);
            _added++;
        }

        _pending.Writer.TryWrite(entry);
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ConnectionLogEntry> Newest(int count)
    {
        lock (_lock)
        {
            var n = Math.Clamp(count, 0, _count);
            var result = new ConnectionLogEntry[n];
            for (var i = 0; i < n; i++)
                result[i] = _ring[((_next - 1 - i) % _ring.Length + _ring.Length) % _ring.Length];
            return result;
        }
    }

    /// <summary>Waits until every line added so far is in the file (tests, shutdown).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        long target;
        lock (_lock)
            target = _added;
        while (Interlocked.Read(ref _written) < target && !_writer.IsCompleted)
            await Task.Delay(10, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    /// <summary>The file of a day: logs/connections-20261019.log.</summary>
    public static string FileName(DateTimeOffset day) => FilePrefix + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";

    private async Task WriteLoopAsync()
    {
        var lines = new StringBuilder();
        while (await _pending.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // Everything waiting goes into the file in one write per day.
            var batch = 0;
            string? day = null;
            DateTimeOffset dayTime = default;
            lines.Clear();
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
                    if (!string.Equals(day, _day, StringComparison.Ordinal))
                    {
                        _day = day;
                        Prune(dayTime);
                    }

                    await File.AppendAllTextAsync(Path.Combine(_folder, day), lines.ToString(), Encoding.UTF8).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The log never stops a connection; the in-memory part stays.
                }
            }

            Interlocked.Add(ref _written, batch);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var oldest = now.AddDays(-KeepDays).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        foreach (var file in Directory.EnumerateFiles(_folder!, FilePrefix + "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var date = name.Length > FilePrefix.Length ? name[FilePrefix.Length..] : "";
            if (date.Length == 8 && string.CompareOrdinal(date, oldest) < 0)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static string OneLine(string? text) =>
        string.IsNullOrEmpty(text) ? "" : new string([.. text.Select(ch => char.IsControl(ch) ? ' ' : ch)]);
}
