using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tasks;

/// <summary>
/// The <see cref="IAxisDownloads"/> of the tasks of one run (docs/spec/myaxis.md "For plugins"): the user's sign-in is read
/// once per run and kept for <see cref="KeepFor"/> (then read again, so a sign-in during a long run counts), not once per
/// device. A failed read is logged and the run's tasks get no downloads: a plugin that does not use them never fails for
/// it. Thread safe.
/// </summary>
internal sealed partial class RunAxisDownloads(MyAxis.IAxisDownloadSource source, TimeProvider time, ILogger logger, CancellationToken stopping)
{
    /// <summary>How long one read of the sign-in serves the tasks of a run.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(Guid Batch, Guid User), Entry> _runs = new();

    /// <summary>The downloads of the run <paramref name="batchId"/> started by <paramref name="userId"/>; null when the read failed.</summary>
    public Task<IAxisDownloads?> ForAsync(Guid batchId, Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var (key, old) in _runs)
        {
            if (now - old.At >= KeepFor)
            {
                _runs.TryRemove(new KeyValuePair<(Guid, Guid), Entry>(key, old));
            }
        }

        var entry = _runs.GetOrAdd((batchId, userId), static (key, state) =>
            new Entry(new Lazy<Task<IAxisDownloads?>>(() => state.Self.ReadAsync(key.User)), state.Now), (Self: this, Now: now));
        return entry.Value.Value.WaitAsync(ct);
    }

    /// <summary>One read for the run, cancelled only when the server stops (not by the device task that started it).</summary>
    private async Task<IAxisDownloads?> ReadAsync(Guid userId)
    {
        try
        {
            return await source.ForUserAsync(userId, stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogReadFailed(ex, userId);
            return null;
        }
    }

    private sealed record Entry(Lazy<Task<IAxisDownloads?>> Value, DateTimeOffset At);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MyAxis: the sign-in of user {UserId} could not be read; the run's tasks get no downloads from axis.com")]
    private partial void LogReadFailed(Exception ex, Guid userId);
}
