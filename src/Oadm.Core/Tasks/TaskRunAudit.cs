using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Auth;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;

namespace Oadm.Core.Tasks;

/// <summary>
/// Counts how the tasks of one run end (docs/spec/security.md "Task outcome"): the engine hands it each task's final
/// snapshot (<see cref="Ended"/>, O(1)), it completes once every task of the run has ended. Keeps only the counts and the
/// first <see cref="NamedFailures"/> failed tasks, so a run on 100,000 devices costs nothing per task beyond a lock.
/// </summary>
public sealed class TaskBatchWatch
{
    /// <summary>How many failed tasks the outcome names.</summary>
    public const int NamedFailures = 3;

    private readonly Lock _sync = new();
    private readonly Action<TaskRecord>? _each;
    private readonly List<TaskRecord> _failures = [];
    private readonly TaskCompletionSource<TaskBatchOutcome> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _expected = -1;
    private int _ended;
    private int _doneCount;
    private int _warnings;
    private int _failed;
    private int _cancelled;

    /// <param name="eachEnded">Called with each task's final snapshot as it ends (must be fast and must not throw).</param>
    public TaskBatchWatch(Action<TaskRecord>? eachEnded = null) => _each = eachEnded;

    /// <summary>Completes when every task of the run has ended.</summary>
    public Task<TaskBatchOutcome> Completion => _done.Task;

    /// <summary>The number of tasks of the run: set by the engine before the first task of the run starts.</summary>
    public void Expect(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        lock (_sync)
        {
            _expected = count;
            CompleteIfDoneLocked();
        }
    }

    /// <summary>A task of the run has ended (its final snapshot): called by the engine once per task.</summary>
    public void Ended(TaskRecord final)
    {
        ArgumentNullException.ThrowIfNull(final);
        lock (_sync)
        {
            _ended++;
            switch (final.State)
            {
                case TaskState.Done:
                    _doneCount++;
                    break;
                case TaskState.DoneWithWarnings:
                    _warnings++;
                    break;
                case TaskState.Cancelled:
                    _cancelled++;
                    break;
                default:
                    _failed++;
                    if (_failures.Count < NamedFailures)
                    {
                        _failures.Add(final);
                    }

                    break;
            }

            CompleteIfDoneLocked();
        }

        _each?.Invoke(final);
    }

    private void CompleteIfDoneLocked()
    {
        if (_expected > 0 && _ended >= _expected)
        {
            _done.TrySetResult(new TaskBatchOutcome(_ended, _doneCount, _warnings, _failed, _cancelled, [.. _failures]));
        }
    }
}

/// <summary>How the tasks of one run ended.</summary>
/// <param name="Total">Tasks of the run.</param>
/// <param name="Done">Done.</param>
/// <param name="DoneWithWarnings">Done with warnings.</param>
/// <param name="Failed">Failed.</param>
/// <param name="Cancelled">Cancelled.</param>
/// <param name="Failures">The first failed tasks (at most <see cref="TaskBatchWatch.NamedFailures"/>), in the order they ended.</param>
public sealed record TaskBatchOutcome(int Total, int Done, int DoneWithWarnings, int Failed, int Cancelled, IReadOnlyList<TaskRecord> Failures);

/// <summary>
/// The audit entry "Started task" of a run with its outcome (docs/spec/security.md "Task outcome"): one entry when the run
/// starts ("3 devices"), its detail replaced once when every task of the run has ended ("3 devices: 2 done, 1 failed
/// (10.0.0.12, ACCC8E012345: ...)"). With <see cref="TaskAudit.EachTask"/> one entry per task ("10.0.0.12 (ACCC8E012345)"),
/// each updated when its task ends. Never an entry per step. A run the server stops keeps the detail it had.
/// </summary>
public sealed partial class TaskRunAudit(AuditLog log, IDeviceRepository devices, ILogger<TaskRunAudit>? logger = null)
{
    /// <summary>Longest task message an outcome quotes.</summary>
    public const int MaxMessageLength = 120;

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private int _pending;

    /// <summary>Outcome writes not finished yet (tests wait for zero).</summary>
    public int PendingWrites => Volatile.Read(ref _pending);

    /// <summary>
    /// Starts the run through <paramref name="run"/> (which hands the watch to the engine), then writes the entry; a run that
    /// throws writes none. The user: <see cref="TaskAudit.UserName"/> (without an address), else the current caller, else "system".
    /// Once the run has started, its entry is written even when the caller gives up (no token: the tasks run anyway).
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RunAsync(TaskAudit audit, IReadOnlyList<Guid> deviceIds, Func<TaskBatchWatch, Task<IReadOnlyList<Guid>>> run)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(deviceIds);
        ArgumentNullException.ThrowIfNull(run);
        var caller = CallerContext.Current;
        var user = audit.UserName ?? caller?.AuditUserName ?? caller?.UserName ?? "system";
        var address = audit.UserName is null ? caller?.ClientAddress ?? string.Empty : string.Empty; // a named user is no caller's address
        if (!audit.EachTask)
        {
            var watch = new TaskBatchWatch();
            var ids = await run(watch).ConfigureAwait(false);
            var entry = await log.AddAsync(user, address, AuditActions.TaskRun, audit.Target, DeviceCount(ids.Count), CancellationToken.None).ConfigureAwait(false);
            if (entry is { } id)
            {
                Track(FinishRunAsync(id, watch));
            }

            return ids;
        }

        var each = new EachTaskEntries(this);
        var tasks = await run(new TaskBatchWatch(each.Ended)).ConfigureAwait(false);
        var distinct = deviceIds.Distinct().ToArray();
        var byDevice = await DetailsAsync(distinct, CancellationToken.None).ConfigureAwait(false);
        var details = distinct.Select(d => byDevice[d]).ToArray();
        var entries = await log.AddManyAsync(user, address, AuditActions.TaskRun, audit.Target, details, CancellationToken.None).ConfigureAwait(false);
        var map = new Dictionary<Guid, (long Id, string Detail)>(tasks.Count);
        for (var i = 0; entries is not null && i < Math.Min(tasks.Count, entries.Count); i++)
        {
            map[distinct[i]] = (entries[i], details[i]);
        }

        each.Attach(map);
        return tasks;
    }

    /// <summary>"1 device" / "3 devices": the detail while the run is active.</summary>
    public static string DeviceCount(int count) =>
        count == 1 ? "1 device" : string.Create(CultureInfo.InvariantCulture, $"{count:N0} devices");

    /// <summary>
    /// The detail of an ended run: "3 devices: 2 done, 1 failed (10.0.0.12, ACCC8E012345: The password is wrong)"; at most
    /// <see cref="TaskBatchWatch.NamedFailures"/> failures named, then "and N more".
    /// </summary>
    public static string RunOutcome(TaskBatchOutcome outcome, IReadOnlyDictionary<Guid, string> deviceLabels)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(deviceLabels);
        var one = outcome.Total == 1;
        var parts = new List<string>(4);
        Add(outcome.Done, "done");
        Add(outcome.DoneWithWarnings, "done with warnings");
        Add(outcome.Failed, "failed");
        Add(outcome.Cancelled, "cancelled");
        var text = DeviceCount(outcome.Total) + ": " + string.Join(", ", parts);
        if (outcome.Failures.Count == 0)
        {
            return text;
        }

        var named = outcome.Failures.Select(f =>
        {
            var device = f.DeviceId;
            var label = deviceLabels.TryGetValue(device, out var l) ? l : RemovedDevice(device);
            // Without its closing period: the failures are joined with "; " inside the brackets.
            return MessageOf(f) is { } message ? label + ": " + message.TrimEnd('.') : label;
        }).ToList();
        if (outcome.Failed > named.Count)
        {
            named.Add(string.Create(CultureInfo.InvariantCulture, $"and {outcome.Failed - named.Count:N0} more"));
        }

        return text + " (" + string.Join("; ", named) + ")";

        void Add(int count, string word)
        {
            if (count > 0)
            {
                parts.Add(one ? word : string.Create(CultureInfo.InvariantCulture, $"{count:N0} {word}"));
            }
        }
    }

    /// <summary>
    /// The detail of one ended task (<see cref="TaskAudit.EachTask"/>): "10.0.0.12 (ACCC8E012345): done", "...: done with
    /// warnings: message", "...: failed at Set the password: message", "...: cancelled".
    /// </summary>
    public static string EachOutcome(string deviceDetail, TaskRecord final)
    {
        ArgumentNullException.ThrowIfNull(final);
        var message = MessageOf(final);
        var outcome = final.State switch
        {
            TaskState.Done => "done",
            TaskState.DoneWithWarnings => message is null ? "done with warnings" : "done with warnings: " + message,
            TaskState.Cancelled => "cancelled",
            _ => (final.Steps.FirstOrDefault(s => s.State == TaskStepState.Failed)?.Name is { Length: > 0 } step ? "failed at " + step : "failed")
                + (message is null ? string.Empty : ": " + message),
        };
        return deviceDetail + ": " + outcome;
    }

    /// <summary>"10.0.0.12 (ACCC8E012345)": the device of one task's entry.</summary>
    public static string DeviceDetail(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return string.IsNullOrWhiteSpace(device.Serial) ? device.Address : device.Address + " (" + device.Serial + ")";
    }

    /// <summary>"10.0.0.12, ACCC8E012345": a failed device of a run's outcome.</summary>
    public static string DeviceLabel(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return string.IsNullOrWhiteSpace(device.Serial) ? device.Address : device.Address + ", " + device.Serial;
    }

    // The phrase of the client's DeviceTexts.Removed ("Removed device 1a2b3c4d"); the server has no client texts.
    private static string RemovedDevice(Guid deviceId) => "Removed device " + deviceId.ToString()[..8];

    private static string? MessageOf(TaskRecord final)
    {
        var message = final.Devices.Count > 0 ? final.Devices[0].Message : null;
        var clean = AuditLog.Clean(message);
        if (clean.Length == 0)
        {
            return null;
        }

        return clean.Length > MaxMessageLength ? clean[..(MaxMessageLength - 1)].TrimEnd() + "…" : clean;
    }

    private async Task FinishRunAsync(long entryId, TaskBatchWatch watch)
    {
        var outcome = await watch.Completion.ConfigureAwait(false);
        var failed = outcome.Failures.Select(f => f.DeviceId).Distinct().ToArray();
        var labels = new Dictionary<Guid, string>(failed.Length);
        foreach (var device in failed)
        {
            if (await devices.FindAsync(device, CancellationToken.None).ConfigureAwait(false) is { } info)
            {
                labels[device] = DeviceLabel(info);
            }
        }

        await log.SetDetailAsync(entryId, RunOutcome(outcome, labels), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The detail of each device's entry; one read of the device list for a large run, else one read per device.</summary>
    private async Task<Dictionary<Guid, string>> DetailsAsync(Guid[] deviceIds, CancellationToken ct)
    {
        var details = new Dictionary<Guid, string>(deviceIds.Length);
        if (deviceIds.Length > 64)
        {
            var wanted = deviceIds.ToHashSet();
            foreach (var device in await devices.ListAsync(ct).ConfigureAwait(false))
            {
                if (wanted.Contains(device.Id))
                {
                    details[device.Id] = DeviceDetail(device);
                }
            }
        }
        else
        {
            foreach (var id in deviceIds)
            {
                if (await devices.FindAsync(id, ct).ConfigureAwait(false) is { } info)
                {
                    details[id] = DeviceDetail(info);
                }
            }
        }

        foreach (var id in deviceIds)
        {
            details.TryAdd(id, RemovedDevice(id));
        }

        return details;
    }

    private Task SetDetailAsync(long entryId, string detail) => log.SetDetailAsync(entryId, detail, CancellationToken.None);

    private void Track(Task write)
    {
        Interlocked.Increment(ref _pending);
        _ = write.ContinueWith(
            t =>
            {
                if (t.Exception is { } ex)
                {
                    LogOutcomeFailed(ex.GetBaseException());
                }

                Interlocked.Decrement(ref _pending);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The outcome of a task run could not be written to the audit log")]
    private partial void LogOutcomeFailed(Exception ex);

    /// <summary>
    /// The entries of a run with one entry per task: a task that ends before its entry is written waits here until
    /// <see cref="Attach"/> hands the entries over.
    /// </summary>
    private sealed class EachTaskEntries(TaskRunAudit owner)
    {
        private readonly Lock _sync = new();
        private readonly List<TaskRecord> _early = [];
        private Dictionary<Guid, (long Id, string Detail)>? _entries;

        public void Ended(TaskRecord final)
        {
            lock (_sync)
            {
                if (_entries is null)
                {
                    _early.Add(final);
                    return;
                }
            }

            Write(final);
        }

        public void Attach(Dictionary<Guid, (long Id, string Detail)> entries)
        {
            TaskRecord[] early;
            lock (_sync)
            {
                _entries = entries;
                early = [.. _early];
                _early.Clear();
            }

            foreach (var final in early)
            {
                Write(final);
            }
        }

        private void Write(TaskRecord final)
        {
            var device = final.DeviceId;
            if (_entries is not null && _entries.TryGetValue(device, out var entry))
            {
                owner.Track(owner.SetDetailAsync(entry.Id, EachOutcome(entry.Detail, final)));
            }
        }
    }
}
