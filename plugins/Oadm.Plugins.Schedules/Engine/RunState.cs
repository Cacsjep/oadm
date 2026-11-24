using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Schedules.Engine;

/// <summary>
/// One running run: its row with the live counts, the failed devices (at most 1,000), the reports of the device tasks, whether it
/// is closed (window end, stop: no task starts any more) and the wake-up of its loop. Thread safe: the device tasks report from
/// their own threads.
/// </summary>
internal sealed class RunState(StoredSchedule schedule, ScheduleRun row, DateTimeOffset due)
{
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _entered = [];
    private readonly Dictionary<Guid, DeviceReport> _reports = [];
    private readonly List<RunFailure> _failures = [];
    private ScheduleRun _row = row;
    private bool _closed;
    private bool _woken;
    private TaskCompletionSource? _wake;
    private long _ranTicks;
    private int _ran;
    private DateTimeOffset _published = DateTimeOffset.MinValue;

    /// <summary>The schedule as it was when the run started.</summary>
    public StoredSchedule Schedule { get; } = schedule;

    /// <summary>The start this run is for (its window end follows from it).</summary>
    public DateTimeOffset Due { get; } = due;

    /// <summary>The owner's call (set once the owner was checked): their task runner for Upgrade AXIS OS online.</summary>
    public ICallContext? Owner { get; set; }

    /// <summary>The run's loop.</summary>
    public Task? Loop { get; set; }

    /// <summary>The device tasks started and not ended (the run's loop only).</summary>
    public Dictionary<Guid, IDeviceInfo> Inflight { get; } = [];

    /// <summary>Devices whose task of this schedule from before the plugin restarted still runs: they get no second one.</summary>
    public IReadOnlySet<Guid> Busy { get; set; } = new HashSet<Guid>();

    /// <summary>Why the run was asked to stop (the schedule was deleted, the plugin stops); null: it was not.</summary>
    public string? StopReason
    {
        get
        {
            lock (_gate)
            {
                return _stopReason;
            }
        }
    }

    /// <summary>The plugin stops: the run ends at once, its running tasks run to their end without it.</summary>
    public bool Abandon
    {
        get
        {
            lock (_gate)
            {
                return _abandon;
            }
        }
    }

    private string? _stopReason;
    private bool _abandon;

    /// <summary>Asks the run's loop to close: no further task or stage starts, the queued tasks are cancelled.</summary>
    public void RequestStop(string reason, bool abandon)
    {
        lock (_gate)
        {
            _stopReason ??= reason;
            _abandon |= abandon;
        }

        Wake();
    }

    /// <summary>A device failed with "Sign in to MyAxis." (the page's warning).</summary>
    public bool SignInFailed { get; private set; }

    public ScheduleRun Row
    {
        get
        {
            lock (_gate)
            {
                return _row;
            }
        }
    }

    public bool Closed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    /// <summary>The average time of a device's task; null when none ran.</summary>
    public TimeSpan? DeviceTime
    {
        get
        {
            lock (_gate)
            {
                return _ran == 0 ? null : TimeSpan.FromTicks(_ranTicks / _ran);
            }
        }
    }

    public ScheduleRun Snapshot() => Row;

    private int _fired;

    /// <summary>True only the first time: the run's event goes out once.</summary>
    public bool MarkFired() => Interlocked.Exchange(ref _fired, 1) == 0;

    public IReadOnlyList<RunFailure> FailuresSnapshot()
    {
        lock (_gate)
        {
            return [.. _failures];
        }
    }

    public void Update(Func<ScheduleRun, ScheduleRun> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            _row = change(_row);
        }
    }

    /// <summary>A device task starts: false (and Not started) when the run closed meanwhile.</summary>
    public bool Enter(Guid taskId)
    {
        lock (_gate)
        {
            if (_closed)
            {
                _reports[taskId] = new DeviceReport(DeviceEnd.NotStarted);
            }
            else
            {
                _entered.Add(taskId);
                return true;
            }
        }

        Wake();
        return false;
    }

    /// <summary>A device task ended (its own report).</summary>
    public void Report(Guid taskId, DeviceReport report)
    {
        lock (_gate)
        {
            _reports[taskId] = report;
        }

        Wake();
    }

    public DeviceReport? TakeReport(Guid taskId)
    {
        lock (_gate)
        {
            return _reports.Remove(taskId, out var report) ? report : null;
        }
    }

    /// <summary>Takes the reports of the tasks of <paramref name="inflight"/> (reports of tasks not known yet stay).</summary>
    public IReadOnlyList<(Guid TaskId, DeviceReport Report)> TakeReports(IReadOnlyDictionary<Guid, IDeviceInfo> inflight)
    {
        ArgumentNullException.ThrowIfNull(inflight);
        lock (_gate)
        {
            if (_reports.Count == 0)
            {
                return [];
            }

            var taken = new List<(Guid, DeviceReport)>();
            foreach (var (taskId, report) in _reports)
            {
                if (inflight.ContainsKey(taskId))
                {
                    taken.Add((taskId, report));
                }
            }

            foreach (var (taskId, _) in taken)
            {
                _reports.Remove(taskId);
            }

            return taken;
        }
    }

    /// <summary>Closes the run and answers the tasks of <paramref name="inflight"/> that did not start (to cancel).</summary>
    public IReadOnlyList<Guid> CloseAndListWaiting(IEnumerable<Guid> inflight)
    {
        lock (_gate)
        {
            _closed = true;
            return [.. inflight.Where(id => !_entered.Contains(id) && !_reports.ContainsKey(id))];
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }

    /// <summary>Counts one device's end; a failure is kept (at most 1,000) with its device, step and message.</summary>
    public void Apply(IDeviceInfo device, DeviceReport report)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            _row = report.End switch
            {
                DeviceEnd.Done => _row with { Done = _row.Done + 1 },
                DeviceEnd.Warning => _row with { Warnings = _row.Warnings + 1 },
                DeviceEnd.Failed => _row with { Failed = _row.Failed + 1 },
                _ => _row with { NotStarted = _row.NotStarted + 1 },
            };
            if (report.Ran is { } ran && report.End != DeviceEnd.NotStarted)
            {
                _ranTicks += ran.Ticks;
                _ran++;
            }

            if (report.End == DeviceEnd.Failed)
            {
                var message = report.Message ?? "The task failed.";
                if (report.SignIn)
                {
                    SignInFailed = true;
                }

                if (_failures.Count < SchedulesInfo.MaxFailuresPerRun)
                {
                    _failures.Add(new RunFailure(device.Id, DeviceLabel(device), report.Step ?? string.Empty, message));
                }
            }
        }
    }

    /// <summary>True at most once per <paramref name="interval"/> (the page's live counts).</summary>
    public bool ShouldPublish(DateTimeOffset now, TimeSpan interval)
    {
        lock (_gate)
        {
            if (now - _published < interval)
            {
                return false;
            }

            _published = now;
            return true;
        }
    }

    /// <summary>Wakes the run's loop (a report, the window end).</summary>
    public void Wake()
    {
        TaskCompletionSource? wake;
        lock (_gate)
        {
            _woken = true;
            wake = _wake;
        }

        wake?.TrySetResult();
    }

    /// <summary>Waits for a wake-up or <paramref name="poll"/>, whichever comes first; at once when one came since the last wait. True: a wake-up came.</summary>
    public async Task<bool> WaitAsync(TimeSpan poll, TimeProvider time, CancellationToken ct)
    {
        Task wake;
        lock (_gate)
        {
            if (_woken)
            {
                _woken = false;
                return true;
            }

            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wake = _wake.Task;
        }

        bool woken;
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var delay = Task.Delay(poll, time, stop.Token);
            woken = await Task.WhenAny(wake, delay).ConfigureAwait(false) == wake;
            await stop.CancelAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            _woken = false;
            _wake = null;
        }

        ct.ThrowIfCancellationRequested();
        return woken;
    }

    /// <summary>"10.0.0.12 (ACCC8E012345)".</summary>
    public static string DeviceLabel(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return string.IsNullOrWhiteSpace(device.Serial) ? device.Address : $"{device.Address} ({device.Serial})";
    }
}
