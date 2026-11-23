using System.Text.Json;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Schedules.Engine;

/// <summary>A schedule as stored: the schedule, the device scope of its owner and the start it last handled.</summary>
public sealed record StoredSchedule
{
    public Schedule Schedule { get; init; } = new();

    /// <summary>The owner's <see cref="ICallContext.DeviceScopeKey"/> when they saved it: users of another group never see it.</summary>
    public string OwnerScopeKey { get; init; } = ICallContext.AllDevicesScopeKey;

    /// <summary>The last start that ran, was skipped or missed (or the save, the turn on): only a later start is due (rule 6).</summary>
    public DateTimeOffset? Handled { get; init; }

    /// <summary>The last run's action Upgrade AXIS OS online failed for a missing MyAxis sign-in of the owner (the page's warning).</summary>
    public bool SignInMissing { get; init; }
}

/// <summary>A run as stored: the row and how long one device took on average (the next estimate).</summary>
public sealed record StoredRun
{
    public ScheduleRun Run { get; init; } = new();

    /// <summary>The average time of a device's task in this run; null when no device ran.</summary>
    public TimeSpan? DeviceTime { get; init; }
}

/// <summary>
/// The schedules (plugin setting <c>schedules</c>, at most 200), the runs of each schedule (records <c>runs</c>, key the
/// schedule id, the last 50 newest first) and the failed devices of each run (records <c>failures</c>, key the run id, at most
/// 1,000). Everything in memory too; writes one at a time.
/// </summary>
public sealed class ScheduleStore(IPluginSettings settings, IPluginRecords records) : IDisposable
{
    public const string SchedulesKey = "schedules";
    public const string RunsCollection = "runs";
    public const string FailuresCollection = "failures";

    private static JsonSerializerOptions Json => SchedulesJson.Options;

    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Lock _gate = new();
    private Dictionary<Guid, StoredSchedule> _schedules = [];
    private readonly Dictionary<Guid, List<StoredRun>> _runs = [];

    /// <summary>Every stored schedule.</summary>
    public IReadOnlyList<StoredSchedule> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _schedules.Values];
            }
        }
    }

    public StoredSchedule? Find(Guid id)
    {
        lock (_gate)
        {
            return _schedules.GetValueOrDefault(id);
        }
    }

    /// <summary>The runs of a schedule, newest first.</summary>
    public IReadOnlyList<StoredRun> RunsOf(Guid scheduleId)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(scheduleId, out var runs) ? [.. runs] : [];
        }
    }

    /// <summary>The last ended run of a schedule (not Missed or Skipped), for the estimate.</summary>
    public StoredRun? LastEnded(Guid scheduleId) =>
        RunsOf(scheduleId).FirstOrDefault(r => r.Run.Result is not (RunResult.Running or RunResult.Missed or RunResult.Skipped) && r.DeviceTime is not null);

    /// <summary>Reads the schedules and runs; a run still Running (the server stopped during it) ends Failed with the reason.</summary>
    public async Task<int> LoadAsync(DateTimeOffset now, CancellationToken ct)
    {
        var list = PluginJson.TryParse<List<StoredSchedule>>(await settings.GetAsync(SchedulesKey, ct).ConfigureAwait(false), Json) ?? [];
        var runs = await records.ListAsync(RunsCollection, ct).ConfigureAwait(false);
        var interrupted = new List<KeyValuePair<string, string?>>();
        lock (_gate)
        {
            _schedules = list.Where(s => s.Schedule.Id != Guid.Empty).GroupBy(s => s.Schedule.Id).ToDictionary(g => g.Key, g => g.First());
            _runs.Clear();
            foreach (var (key, json) in runs)
            {
                if (!Guid.TryParse(key, out var id) || PluginJson.TryParse<List<StoredRun>>(json, Json) is not { } rows)
                {
                    continue;
                }

                var changed = false;
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Run.Result == RunResult.Running)
                    {
                        rows[i] = rows[i] with { Run = rows[i].Run with { Result = RunResult.Failed, Ended = now, Reason = ServerStopped } };
                        changed = true;
                    }
                }

                _runs[id] = rows;
                if (changed)
                {
                    interrupted.Add(new(key, JsonSerializer.Serialize(rows, Json)));
                }
            }
        }

        if (interrupted.Count > 0)
        {
            await records.SetManyAsync(RunsCollection, interrupted, ct).ConfigureAwait(false);
        }

        return interrupted.Count;
    }

    public const string ServerStopped = "The server stopped during the run.";

    /// <summary>Adds or replaces a schedule.</summary>
    public Task SaveAsync(StoredSchedule schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return ChangeSchedulesAsync(map => map[schedule.Schedule.Id] = schedule, ct);
    }

    /// <summary>Changes a stored schedule; false when it is gone.</summary>
    public async Task<bool> UpdateAsync(Guid id, Func<StoredSchedule, StoredSchedule> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        var found = false;
        await ChangeSchedulesAsync(map =>
        {
            if (map.TryGetValue(id, out var current))
            {
                map[id] = change(current);
                found = true;
            }
        }, ct).ConfigureAwait(false);
        return found;
    }

    /// <summary>
    /// Changes a stored schedule when <paramref name="change"/> answers a new value for the one stored now (read under the write
    /// lock); null: nothing is written. Answers the new value, or null.
    /// </summary>
    public async Task<StoredSchedule?> UpdateIfAsync(Guid id, Func<StoredSchedule, StoredSchedule?> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<Guid, StoredSchedule> next;
            StoredSchedule? changed;
            lock (_gate)
            {
                if (!_schedules.TryGetValue(id, out var current) || (changed = change(current)) is null)
                {
                    return null;
                }

                next = new Dictionary<Guid, StoredSchedule>(_schedules) { [id] = changed };
            }

            await settings.SetAsync(SchedulesKey, JsonSerializer.Serialize(next.Values.OrderBy(s => s.Schedule.Name, StringComparer.OrdinalIgnoreCase).ToList(), Json), ct).ConfigureAwait(false);
            lock (_gate)
            {
                _schedules = next;
            }

            return changed;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Deletes a schedule with its runs and their failed devices; false when it is gone.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var found = false;
        await ChangeSchedulesAsync(map => found = map.Remove(id), ct).ConfigureAwait(false);
        if (!found)
        {
            return false;
        }

        List<StoredRun>? runs;
        lock (_gate)
        {
            _runs.Remove(id, out runs);
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await records.SetManyAsync(RunsCollection, [new(Key(id), null)], ct).ConfigureAwait(false);
            if (runs is { Count: > 0 })
            {
                await records.SetManyAsync(FailuresCollection, [.. runs.Select(r => new KeyValuePair<string, string?>(Key(r.Run.Id), null))], ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _write.Release();
        }

        return true;
    }

    /// <summary>
    /// Writes a run of a schedule (added first, or replacing the row with its id); the runs beyond the last 50 go with their failed
    /// devices. Nothing is written for a schedule that was deleted meanwhile.
    /// </summary>
    public async Task SaveRunAsync(StoredRun run, IReadOnlyList<RunFailure>? failures, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string json;
            List<StoredRun> dropped;
            List<StoredRun>? before;
            lock (_gate)
            {
                if (!_schedules.ContainsKey(run.Run.ScheduleId))
                {
                    return;
                }

                before = _runs.TryGetValue(run.Run.ScheduleId, out var old) ? [.. old] : null;
                if (!_runs.TryGetValue(run.Run.ScheduleId, out var list))
                {
                    _runs[run.Run.ScheduleId] = list = [];
                }

                var index = list.FindIndex(r => r.Run.Id == run.Run.Id);
                if (index >= 0)
                {
                    list[index] = run;
                }
                else
                {
                    list.Insert(0, run);
                }

                dropped = list.Count > SchedulesInfo.KeptRuns ? list.GetRange(SchedulesInfo.KeptRuns, list.Count - SchedulesInfo.KeptRuns) : [];
                if (dropped.Count > 0)
                {
                    list.RemoveRange(SchedulesInfo.KeptRuns, dropped.Count);
                }

                json = JsonSerializer.Serialize(list, Json);
            }

            try
            {
                await records.SetManyAsync(RunsCollection, [new(Key(run.Run.ScheduleId), json)], ct).ConfigureAwait(false);
            }
            catch
            {
                // Not written: the rows in memory stay as stored.
                lock (_gate)
                {
                    if (before is null)
                    {
                        _runs.Remove(run.Run.ScheduleId);
                    }
                    else
                    {
                        _runs[run.Run.ScheduleId] = before;
                    }
                }

                throw;
            }

            var failureRows = dropped.Select(r => new KeyValuePair<string, string?>(Key(r.Run.Id), null)).ToList();
            if (failures is not null)
            {
                failureRows.Add(new(Key(run.Run.Id), failures.Count == 0 ? null : JsonSerializer.Serialize(failures.Take(SchedulesInfo.MaxFailuresPerRun), Json)));
            }

            if (failureRows.Count > 0)
            {
                await records.SetManyAsync(FailuresCollection, failureRows, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>The failed devices of an ended run (at most 1,000).</summary>
    public async Task<IReadOnlyList<RunFailure>> FailuresAsync(Guid runId, CancellationToken ct) =>
        PluginJson.TryParse<List<RunFailure>>(await records.GetAsync(FailuresCollection, Key(runId), ct).ConfigureAwait(false), Json) ?? [];

    public void Dispose() => _write.Dispose();

    private static string Key(Guid id) => id.ToString("N");

    private async Task ChangeSchedulesAsync(Action<Dictionary<Guid, StoredSchedule>> change, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<Guid, StoredSchedule> next;
            lock (_gate)
            {
                next = new Dictionary<Guid, StoredSchedule>(_schedules);
            }

            change(next);
            await settings.SetAsync(SchedulesKey, JsonSerializer.Serialize(next.Values.OrderBy(s => s.Schedule.Name, StringComparer.OrdinalIgnoreCase).ToList(), Json), ct).ConfigureAwait(false);
            lock (_gate)
            {
                _schedules = next;
            }
        }
        finally
        {
            _write.Release();
        }
    }
}
