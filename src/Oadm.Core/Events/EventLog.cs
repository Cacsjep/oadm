using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Persistence;

namespace Oadm.Core.Events;

/// <summary>One line of the event log as the engine writes it and clients read it.</summary>
public sealed record EventLogLine(
    long Id,
    DateTime TimeUtc,
    EventLogKind Kind,
    EventLogResult Result,
    string What,
    Guid? DeviceId,
    string Device,
    Guid? RuleId,
    string Rule,
    string Message);

/// <summary>
/// The event log (table EventLogEntries): <see cref="Write"/> only queues (never blocks the engine); a writer stores
/// batches every <see cref="FlushInterval"/> and pushes them to every watcher. A full queue drops the newest lines and
/// logs how many. Retention: <see cref="PruneAsync"/> (hourly, <c>Events.LogRetentionDays</c> / <c>Events.LogMaxEntries</c>).
/// </summary>
public sealed partial class EventLog : IAsyncDisposable
{
    public const int QueueCapacity = 20_000;
    public const int MaxBatch = 1_000;
    public const int ListLimit = 10_000;

    public static TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    private readonly IDbContextFactory<OadmDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<EventLogLine> _queue = Channel.CreateBounded<EventLogLine>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private readonly ConcurrentDictionary<Channel<IReadOnlyList<EventLogLine>>, byte> _watchers = new();
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;
    private readonly Task _writer;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private long _dropped;

    public EventLog(IDbContextFactory<OadmDbContext> dbFactory, TimeProvider? time = null, ILogger<EventLog>? logger = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Queues a line (the id and, when default, the time are set here).</summary>
    public void Write(EventLogKind kind, EventLogResult result, string what, Guid? deviceId = null, string? device = null, Guid? ruleId = null, string? rule = null, string? message = null)
    {
        var line = new EventLogLine(
            0,
            _time.GetUtcNow().UtcDateTime,
            kind,
            result,
            Cut(what, EventLogEntryEntity.MaxTextLength),
            deviceId,
            Cut(device, EventLogEntryEntity.MaxTextLength),
            ruleId,
            Cut(rule, EventLogEntryEntity.MaxTextLength),
            Cut(message, EventLogEntryEntity.MaxMessageLength));
        if (!_queue.Writer.TryWrite(line))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Writes every queued line now (tests, shutdown).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await _flushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (_queue.Reader.TryPeek(out _))
            {
                await WriteBatchAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>Newest first. <paramref name="limit"/> 0 or above <see cref="ListLimit"/> = <see cref="ListLimit"/>.</summary>
    public async Task<(IReadOnlyList<EventLogLine> Lines, int Total)> ListAsync(int limit, int offset, Guid? ruleId, CancellationToken ct)
    {
        limit = limit <= 0 || limit > ListLimit ? ListLimit : limit;
        offset = Math.Max(0, offset);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.EventLogEntries.AsNoTracking();
        if (ruleId is { } id)
        {
            query = query.Where(e => e.RuleId == id);
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query.OrderByDescending(e => e.Id).Skip(offset).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        return ([.. rows.Select(ToLine)], total);
    }

    /// <summary>Batches of new lines as they are stored, until <paramref name="ct"/> is cancelled. Slow watchers lose the oldest batches.</summary>
    public async IAsyncEnumerable<IReadOnlyList<EventLogLine>> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<IReadOnlyList<EventLogLine>>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _watchers[channel] = 0;
        try
        {
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var batch))
                {
                    yield return batch;
                }
            }
        }
        finally
        {
            _watchers.TryRemove(channel, out _);
        }
    }

    /// <summary>Deletes lines older than <paramref name="maxAge"/> and beyond the newest <paramref name="maxEntries"/>. Returns the count.</summary>
    public async Task<int> PruneAsync(TimeSpan? maxAge, int? maxEntries, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var deleted = 0;
        if (maxAge is { } age)
        {
            var cutoff = _time.GetUtcNow().UtcDateTime - age;
            deleted += await db.EventLogEntries.Where(e => e.TimeUtc < cutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        if (maxEntries is > 0)
        {
            var keepFrom = await db.EventLogEntries.OrderByDescending(e => e.Id).Skip(maxEntries.Value - 1).Select(e => (long?)e.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (keepFrom is { } first)
            {
                deleted += await db.EventLogEntries.Where(e => e.Id < first).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }
        }

        return deleted;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Shutdown: the database may be gone already.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _stop.Dispose();
        _flushLock.Dispose();
    }

    private async Task WriteLoopAsync()
    {
        var ct = _stop.Token;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await Task.Delay(FlushInterval, _time, ct).ConfigureAwait(false);
                await _flushLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await WriteBatchAsync(ct).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // The log must never stop the engine; the batch is lost and logged.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    LogWriteFailed(ex);
                }
                finally
                {
                    _flushLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task WriteBatchAsync(CancellationToken ct)
    {
        var lines = new List<EventLogLine>();
        while (lines.Count < MaxBatch && _queue.Reader.TryRead(out var line))
        {
            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _dropped, 0) is > 0 and var dropped)
        {
            LogDropped(dropped);
        }

        var rows = lines.Select(l => new EventLogEntryEntity
        {
            TimeUtc = l.TimeUtc,
            Kind = l.Kind,
            Result = l.Result,
            What = l.What,
            DeviceId = l.DeviceId,
            Device = l.Device,
            RuleId = l.RuleId,
            Rule = l.Rule,
            Message = l.Message,
        }).ToList();
        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            db.EventLogEntries.AddRange(rows);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        IReadOnlyList<EventLogLine> stored = [.. rows.Select(ToLine)];
        foreach (var watcher in _watchers.Keys)
        {
            watcher.Writer.TryWrite(stored);
        }
    }

    private static EventLogLine ToLine(EventLogEntryEntity e) =>
        new(e.Id, e.TimeUtc, e.Kind, e.Result, e.What, e.DeviceId, e.Device, e.RuleId, e.Rule, e.Message);

    private static string Cut(string? text, int max)
    {
        text ??= string.Empty;
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event log: {Count} lines dropped (queue full)")]
    private partial void LogDropped(long count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event log: a batch could not be written")]
    private partial void LogWriteFailed(Exception ex);
}
