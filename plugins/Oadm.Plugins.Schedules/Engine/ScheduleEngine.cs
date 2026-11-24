using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Schedules.Engine;

/// <summary>Timings and limits of the engine; tests pass small ones with a fake clock.</summary>
public sealed record SchedulesOptions
{
    /// <summary>How often the engine looks for due schedules.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a run looks at device tasks that ended without running (cancelled while queued).</summary>
    public TimeSpan TaskPoll { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The parallel limit of the estimate: 16, the default of the server setting Parallel tasks per plugin, which decides how many
    /// device tasks run (the SDK does not tell a plugin the setting).
    /// </summary>
    public int EstimateParallel { get; init; } = 16;

    /// <summary>A start missed while the server was off runs at the next start of the server only within this time (and its window).</summary>
    public TimeSpan CatchUp { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How long the page's list and visibility use an owner's call before asking the host again (a run always asks).</summary>
    public TimeSpan OwnerCache { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The live counts of a running run reach the page at most this often.</summary>
    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The actions' waits for their tasks (Provisioning's node options).</summary>
    public ProvisioningOptions Nodes { get; init; } = new();
}

/// <summary>What the engine works with: the host's services (the plugin's own, never a user's) and its stores.</summary>
/// <param name="Tasks">The plugin's task runner (background, every device).</param>
/// <param name="Devices">Every managed device.</param>
/// <param name="Vapix">VAPIX of every device (the action VAPIX commands reads the API list).</param>
/// <param name="UserCalls">The owners' current calls; null on hosts without them (nothing runs then).</param>
/// <param name="Store">The schedules and runs.</param>
/// <param name="Vault">The actions' kept files.</param>
/// <param name="Rules">The event rules; null without.</param>
/// <param name="Left">Device tasks of earlier engines that still run (kept by the plugin across a restart).</param>
internal sealed record EngineDeps(
    ITaskRunner Tasks,
    IDeviceRepository Devices,
    IVapixClientFactory Vapix,
    IUserCalls? UserCalls,
    ScheduleStore Store,
    NodeVault Vault,
    IEventPublisher? Rules,
    LeftTasks? Left = null);

/// <summary>The payload of one device's task "Run &lt;schedule&gt;" (memory only): its run and the actions as they were at the start.</summary>
internal sealed record RunTaskPayload(Guid RunId, Guid ScheduleId, string Name, IReadOnlyList<FlowNode> Actions);

/// <summary>How a device's task ended for its run.</summary>
internal enum DeviceEnd
{
    Done = 0,
    Warning = 1,
    Failed = 2,
    NotStarted = 3,
}

/// <summary>One device's end: the failed step and its message, how long the task ran (null when it never ran).</summary>
internal sealed record DeviceReport(DeviceEnd End, string? Step = null, string? Message = null, TimeSpan? Ran = null)
{
    /// <summary>A step failed for a missing MyAxis sign-in of the owner (any step, not only the first failure).</summary>
    public bool SignIn { get; init; }
}

/// <summary>
/// The engine of Schedules (docs/spec/schedules.md): looks every <see cref="SchedulesOptions.Tick"/> for due schedules (rule 6,
/// "Missed times"), runs each as its owner (rule 1: <see cref="IUserCalls"/>, fail closed), never two runs of one schedule
/// (Skipped rows), resolves the devices within the owner's view in one pass, gives every Ok device one task
/// <see cref="SchedulesInfo.TaskPluginId"/> in one start per stage (window end and stop cancel the tasks still queued), rolls out
/// in stages, keeps run rows and failed devices, fires the event "Schedule run ended" once per run. All time through
/// <see cref="TimeProvider"/>, in the server's local time.
/// </summary>
internal sealed partial class ScheduleEngine : IAsyncDisposable
{
    public const string OwnerName = "Schedules";
    public const string AlreadyRunning = "A run of this schedule is still running.";
    public const string NoUserCalls = "This OADM server cannot run schedules as their owner.";
    public const string OwnerGone = "The owner's account no longer exists or is turned off.";
    public const string RunGone = "The run of this schedule has ended. Nothing was changed.";
    public const string SkippedText = "The run before was still running.";
    public const string MissedText = "The server was off at the time.";
    public const string OffText = "Schedules was turned off at the time.";

    private readonly EngineDeps _deps;
    private readonly ActionCatalog _catalog;
    private readonly SchedulesOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, RunState> _running = [];
    private readonly Dictionary<Guid, RunState> _byRun = [];
    private readonly CancellationTokenSource _life = new();
    private readonly CancellationTokenSource _ticks = new();
    private bool _stopping;
    private readonly SemaphoreSlim _tick = new(1, 1);
    private readonly Dictionary<string, (ICallContext? Call, DateTimeOffset At)> _owners = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loop;

    public ScheduleEngine(EngineDeps deps, ActionCatalog catalog, SchedulesOptions? options = null, TimeProvider? time = null, ILogger? logger = null)
    {
        _deps = deps ?? throw new ArgumentNullException(nameof(deps));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _options = options ?? new SchedulesOptions();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised with the schedule whose runs or state changed (the plugin publishes <see cref="SchedulesMethods.ChangedTopic"/>).</summary>
    public event Action<Guid>? Changed;

    public ScheduleStore Store => _deps.Store;

    public SchedulesOptions Options => _options;

    public TimeProvider Time => _time;

    /// <summary>The server's time zone (the schedules' times are local times).</summary>
    public TimeZoneInfo Zone => _time.LocalTimeZone;

    /// <summary>Starts the tick loop (the first tick at once: the catch-up of missed starts).</summary>
    /// <param name="catchUp">
    /// True at the first start in the process (the server started): a start missed while the server was off runs within
    /// <see cref="SchedulesOptions.CatchUp"/> and its window. False (the plugin was turned off and on again): the first look
    /// makes up nothing; a start missed while it was off is one Missed row (decision 2026-11-24).
    /// </param>
    public void Start(bool catchUp = true)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _loop = Task.Run(() => LoopAsync(catchUp, _ticks.Token));
        }
    }

    /// <summary>Whether a run of the schedule runs now.</summary>
    public bool IsRunning(Guid scheduleId)
    {
        lock (_gate)
        {
            return _running.ContainsKey(scheduleId);
        }
    }

    /// <summary>The running run of a schedule with its live counts; null when none runs.</summary>
    public ScheduleRun? LiveRun(Guid scheduleId)
    {
        RunState? state;
        lock (_gate)
        {
            state = _running.GetValueOrDefault(scheduleId);
        }

        return state?.Snapshot();
    }

    /// <summary>The failed devices of a running run so far; null when the run does not run.</summary>
    public IReadOnlyList<RunFailure>? LiveFailures(Guid runId)
    {
        RunState? state;
        lock (_gate)
        {
            state = _byRun.GetValueOrDefault(runId);
        }

        return state?.FailuresSnapshot();
    }

    /// <summary>
    /// The owner's current call, or why a run of <paramref name="stored"/> cannot run as the owner (rule 1). <paramref name="fresh"/>
    /// (a run's start): asked of the host now; else the answer of the last <see cref="SchedulesOptions.OwnerCache"/> (the page's
    /// list and visibility never read the users per schedule and refresh).
    /// </summary>
    public async Task<(ICallContext? Call, string? Problem)> OwnerAsync(StoredSchedule stored, CancellationToken ct, bool fresh = false)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (_deps.UserCalls is null)
        {
            return (null, NoUserCalls);
        }

        if (string.IsNullOrWhiteSpace(stored.Schedule.Owner) || await CallOfAsync(stored.Schedule.Owner, fresh, ct).ConfigureAwait(false) is not { } call)
        {
            return (null, OwnerGone);
        }

        return ActionCatalog.Missing(call, stored.Schedule.Actions ?? []) is { } missing
            ? (call, $"The owner no longer has the right {missing.Name}.")
            : (call, null);
    }

    /// <summary>The owner's current call (null: no account, disabled, or no host support), cached per owner name.</summary>
    public async Task<ICallContext?> CallOfAsync(string owner, bool fresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_deps.UserCalls is not { } calls)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!fresh && _owners.TryGetValue(owner, out var cached) && now - cached.At < _options.OwnerCache)
            {
                return cached.Call;
            }
        }

        var call = await calls.ForUserAsync(owner, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _owners[owner] = (call, now);
        }

        return call;
    }

    /// <summary>
    /// Whether <paramref name="caller"/> may see the schedule: users with all devices always; a user with a device group only when
    /// the owner's current view (cached, <see cref="SchedulesOptions.OwnerCache"/>) is the caller's (fail closed: no owner, not seen).
    /// </summary>
    public async Task<bool> CanSeeAsync(ICallContext caller, StoredSchedule stored, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(stored);
        if (caller.HasAllDevices)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(stored.Schedule.Owner)
            && await CallOfAsync(stored.Schedule.Owner, fresh: false, ct).ConfigureAwait(false) is { } owner
            && string.Equals(owner.DeviceScopeKey, caller.DeviceScopeKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Closes the running run of a schedule (deleted): the tasks still queued are cancelled, no further stage starts, running tasks
    /// run to their end; the run ends Stopped with <paramref name="reason"/>.
    /// </summary>
    public void StopRun(Guid scheduleId, string reason)
    {
        RunState? state;
        lock (_gate)
        {
            state = _running.GetValueOrDefault(scheduleId);
        }

        state?.RequestStop(reason, abandon: false);
    }

    /// <summary>Now in the server's local time (the offset of run rows, like the next run and Missed).</summary>
    private DateTimeOffset Now() => TimeZoneInfo.ConvertTime(_time.GetUtcNow(), Zone);

    /// <summary>
    /// One look for due schedules: the latest start after the one last handled. A run of it goes on: Skipped. The server was off and
    /// the start is older than <see cref="SchedulesOptions.CatchUp"/> or its window has closed: Missed. Else the run starts. Never more
    /// than one per schedule and look.
    /// </summary>
    public Task TickAsync(CancellationToken ct) => TickAsync(missedOnly: false, ct);

    /// <summary>A look; <paramref name="missedOnly"/>: every due start is a Missed row (the first look after the plugin was turned on again).</summary>
    public async Task TickAsync(bool missedOnly, CancellationToken ct)
    {
        await _tick.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await TickCoreAsync(missedOnly, ct).ConfigureAwait(false);
        }
        finally
        {
            _tick.Release();
        }
    }

    /// <summary>The actions of the runs that run now (their kept files stay).</summary>
    public IReadOnlyList<IReadOnlyList<FlowNode>> RunningActions()
    {
        lock (_gate)
        {
            return [.. _running.Values.Select(r => r.Schedule.Schedule.Actions ?? [])];
        }
    }

    private async Task TickCoreAsync(bool missedOnly, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        foreach (var stored in Store.All.Where(s => s.Schedule.On))
        {
            var since = stored.Handled ?? stored.Schedule.Saved ?? now;
            if (ScheduleTimes.LatestDue(stored.Schedule.When, since, now, Zone) is not { } due)
            {
                continue;
            }

            // Re-read under the store's lock: only a schedule still on, not saved again meanwhile, whose start was not handled yet.
            var current = await Store.UpdateIfAsync(
                stored.Schedule.Id,
                s => s.Schedule.On && s.Schedule.Saved == stored.Schedule.Saved && !(s.Handled >= due) ? s with { Handled = due } : null,
                ct).ConfigureAwait(false);
            if (current is null)
            {
                continue;
            }

            try
            {
                if (IsRunning(current.Schedule.Id))
                {
                    await RecordAsync(current, due, RunResult.Skipped, SkippedText, ct).ConfigureAwait(false);
                }
                else if (missedOnly)
                {
                    await RecordAsync(current, due, RunResult.Missed, OffText, ct).ConfigureAwait(false);
                }
                else if (now - due > _options.CatchUp || (ScheduleTimes.WindowEnd(current.Schedule.When, due, Zone) is { } end && now >= end))
                {
                    await RecordAsync(current, due, RunResult.Missed, MissedText, ct).ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        await BeginAsync(current, due, null, ct).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException) when (IsRunning(current.Schedule.Id))
                    {
                        await RecordAsync(current, due, RunResult.Skipped, SkippedText, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // One schedule that cannot start gets a Failed row; the look goes on with the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogRunFailed(ex, current.Schedule.Name);
                await RecordQuietlyAsync(current, due, RunResult.Failed, "The run could not start: " + ex.Message).ConfigureAwait(false);
            }
        }
    }

    private async Task RecordQuietlyAsync(StoredSchedule stored, DateTimeOffset due, RunResult result, string reason)
    {
        try
        {
            await RecordAsync(stored, due, result, reason, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The row of a failed start is best effort; it is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRunFailed(ex, stored.Schedule.Name);
        }
    }

    /// <summary>Run now: a run at once as the owner; refused while a run of it goes on.</summary>
    public Task<ScheduleRun> RunNowAsync(Guid scheduleId, string startedBy, CancellationToken ct)
    {
        var stored = Store.Find(scheduleId) ?? throw new KeyNotFoundException(SchedulesPlugin.ScheduleGone);
        return BeginAsync(stored, Now(), startedBy, ct);
    }

    /// <summary>The stage sizes of a schedule's run of <paramref name="devices"/> devices (empty without stages).</summary>
    public static IReadOnlyList<int> StagesOf(Schedule schedule, int devices) => schedule?.Stages == true ? Rollout.StageSizes(devices) : [];

    // ---- a run ----

    private async Task<ScheduleRun> BeginAsync(StoredSchedule stored, DateTimeOffset due, string? startedBy, CancellationToken ct)
    {
        var row = new ScheduleRun
        {
            Id = Guid.NewGuid(),
            ScheduleId = stored.Schedule.Id,
            Started = Now(),
            Result = RunResult.Running,
            StartedBy = startedBy,
        };
        var state = new RunState(stored, row, due);
        lock (_gate)
        {
            if (_stopping)
            {
                throw new InvalidOperationException(Stopping);
            }

            if (_running.ContainsKey(stored.Schedule.Id))
            {
                throw new InvalidOperationException(AlreadyRunning);
            }

            _running[stored.Schedule.Id] = state;
            _byRun[row.Id] = state;
        }

        try
        {
            await Store.SaveRunAsync(new StoredRun { Run = row }, null, ct).ConfigureAwait(false);
        }
        catch
        {
            Forget(state);
            throw;
        }

        LogStarted(stored.Schedule.Name, startedBy ?? "its time");
        Changed?.Invoke(stored.Schedule.Id);
        using (ExecutionContext.SuppressFlow())
        {
            state.Loop = Task.Run(() => RunGuardedAsync(state, _life.Token), CancellationToken.None);
        }

        return row;
    }

    private async Task RunGuardedAsync(RunState state, CancellationToken ct)
    {
        try
        {
            await RunAsync(state, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var running = LeaveRunning(state);
            await EndQuietlyAsync(state, RunResult.Stopped, running > 0 ? StillRunning(running) : ScheduleStore.ServerStopped).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A run that breaks ends Failed with the reason; the engine and the other runs go on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRunFailed(ex, state.Schedule.Schedule.Name);
            LeaveRunning(state);
            await EndQuietlyAsync(state, RunResult.Failed, "The run failed: " + ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The run ends without waiting for its tasks: the queued ones are cancelled, the running ones run on and go to
    /// <see cref="EngineDeps.Left"/> (a later run gives their devices no second task while they run). Both count as Not started,
    /// so the counts add up. Answers how many still run.
    /// </summary>
    private int LeaveRunning(RunState state)
    {
        var inflight = state.Inflight;
        if (inflight.Count == 0)
        {
            state.Close();
            return 0;
        }

        var waiting = state.CloseAndListWaiting(inflight.Keys).ToHashSet();
        foreach (var taskId in waiting)
        {
            _deps.Tasks.Cancel(taskId);
        }

        var running = inflight.Where(t => !waiting.Contains(t.Key)).Select(t => (t.Key, t.Value.Id)).ToList();
        if (running.Count > 0)
        {
            _deps.Left?.Add(state.Schedule.Schedule.Id, running);
        }

        var count = inflight.Count;
        inflight.Clear();
        state.Update(r => r with { NotStarted = r.NotStarted + count });
        return running.Count;
    }

    /// <summary>"Schedules stopped during the run. 2 devices were still running; they are counted as not started."</summary>
    public static string StillRunning(int running) =>
        running == 0 ? PluginStopped : $"{PluginStopped.TrimEnd('.')}. {Devices(running)} {(running == 1 ? "was" : "were")} still running; counted as not started.";

    /// <summary>"Schedules is stopping."</summary>
    public const string Stopping = "Schedules is stopping. Try again when it runs.";

    private async Task RunAsync(RunState state, CancellationToken ct)
    {
        var schedule = state.Schedule.Schedule;
        var (owner, problem) = await OwnerAsync(state.Schedule, ct, fresh: true).ConfigureAwait(false);
        if (owner is null || problem is not null)
        {
            await EndAsync(state, RunResult.Failed, problem, ct).ConfigureAwait(false);
            return;
        }

        state.Owner = owner;

        // The owner's current view decides who sees the schedule (and where the page's event goes).
        if (owner.DeviceScopeKey != state.Schedule.OwnerScopeKey)
        {
            await Store.UpdateIfAsync(schedule.Id, s => s.Schedule.Saved == schedule.Saved ? s with { OwnerScopeKey = owner.DeviceScopeKey } : null, ct).ConfigureAwait(false);
        }

        var choice = schedule.Devices ?? new ScheduleDevices();
        var resolved = DeviceChoice.Resolve(choice, await owner.Devices.ListAsync(ct).ConfigureAwait(false));
        if (resolved.ChosenOutside.Count > 0)
        {
            // A chosen device removed from OADM is simply no longer in it; one outside the owner's view stops the run (fail closed).
            var outside = 0;
            foreach (var id in resolved.ChosenOutside)
            {
                if (await _deps.Devices.FindAsync(id, ct).ConfigureAwait(false) is not null)
                {
                    outside++;
                }
            }

            if (outside > 0)
            {
                await EndAsync(state, RunResult.Failed, outside == 1
                    ? "The owner can no longer reach a chosen device."
                    : $"The owner can no longer reach {outside} chosen devices.", ct).ConfigureAwait(false);
                return;
            }
        }

        var devices = Ordered(resolved.Devices, schedule.Stages);
        IReadOnlyList<int> stages = schedule.Stages ? Rollout.StageSizes(devices.Count) : devices.Count > 0 ? [devices.Count] : [];
        state.Update(r => r with { Devices = devices.Count, StageCount = schedule.Stages ? stages.Count : null });
        var payload = JsonSerializer.Serialize(new RunTaskPayload(state.Row.Id, schedule.Id, schedule.Name, schedule.Actions ?? []), SchedulesJson.Options);

        // One start per stage (the whole run without stages): one audit entry each, with its outcome; the task engine's limit (the
        // server setting Parallel tasks per plugin) decides how many run, the others wait as queued.
        var audit = new TaskAudit(ScheduleTaskPlugin.TaskNameOf(schedule.Name)) { UserName = AuditUserOf(schedule) };
        // Run now has no window (decision 2026-11-24): the window end applies only to a run at its time.
        var windowEnd = state.Row.StartedBy is null ? ScheduleTimes.WindowEnd(schedule.When, state.Due, Zone) : null;
        using var window = windowEnd is { } end
            ? _time.CreateTimer(_ => state.Wake(), null, end - _time.GetUtcNow() is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Timeout.InfiniteTimeSpan)
            : null;

        var next = 0;
        string? stopped = null;
        var windowClosed = false;
        var inflight = state.Inflight;
        state.Busy = _deps.Left?.Busy(schedule.Id, _deps.Tasks) ?? new HashSet<Guid>();
        for (var stage = 0; stage < stages.Count && next < devices.Count && stopped is null && !windowClosed && !state.Closed && state.StopReason is null; stage++)
        {
            if (schedule.Stages)
            {
                state.Update(r => r with { Stage = stage + 1 });
                Changed?.Invoke(schedule.Id);
            }

            // The last stage takes every device left (the first may have taken more to find a device that is Ok).
            var size = stage == stages.Count - 1 ? devices.Count - next : Math.Min(stages[stage], devices.Count - next);
            var pending = new Queue<IDeviceInfo>(devices.Skip(next).Take(size));
            next += size;
            var full = false;
            while (true)
            {
                if (!state.Closed && state.StopReason is null)
                {
                    await StartStageAsync(state, pending, inflight, payload, audit, ct).ConfigureAwait(false);

                    // Roll out in stages tests one device first: the first stage goes on until a device that is Ok started.
                    while (schedule.Stages && stage == 0 && inflight.Count == 0 && Ended(state.Row) == 0 && next < devices.Count)
                    {
                        pending.Enqueue(devices[next++]);
                        await StartStageAsync(state, pending, inflight, payload, audit, ct).ConfigureAwait(false);
                    }
                }

                await HarvestAsync(state, inflight, full, ct).ConfigureAwait(false);
                if (!state.Closed && state.StopReason is { } stopReason)
                {
                    stopped = stopReason;
                    Close(state, inflight, pending);
                    await HarvestAsync(state, inflight, true, ct).ConfigureAwait(false);
                }

                if (!state.Closed && schedule.Stages && state.Row.Failed > 0)
                {
                    stopped = $"Stopped at stage {stage + 1}: {state.Row.Failed} failed";
                    Close(state, inflight, pending);
                    await HarvestAsync(state, inflight, true, ct).ConfigureAwait(false);
                }

                if (!state.Closed && windowEnd is { } closes && _time.GetUtcNow() >= closes)
                {
                    windowClosed = true;
                    Close(state, inflight, pending);
                    await HarvestAsync(state, inflight, true, ct).ConfigureAwait(false);
                }

                // The plugin stops: the run ends now; its running tasks run to their end without it.
                if (inflight.Count == 0 && (pending.Count == 0 || state.Closed) || (state.Abandon && state.Closed))
                {
                    break;
                }

                if (inflight.Count > 0 || pending.Count == 0)
                {
                    full = !await state.WaitAsync(_options.TaskPoll, _time, ct).ConfigureAwait(false);
                }
            }

            if (pending.Count > 0)
            {
                state.Update(r => r with { NotStarted = r.NotStarted + pending.Count });
            }

            // Never a second stage without a tested device.
            if (schedule.Stages && stage == 0 && stopped is null && !state.Closed && state.Row.Done + state.Row.Warnings == 0)
            {
                stopped = NoDeviceTested;
            }
        }

        if (next < devices.Count)
        {
            var left = devices.Count - next;
            state.Update(r => r with { NotStarted = r.NotStarted + left });
        }

        stopped ??= state.StopReason;

        if (state.Abandon && inflight.Count > 0)
        {
            var running = LeaveRunning(state);
            stopped = StillRunning(running);
        }

        var row = state.Row;
        var result = stopped is not null ? RunResult.Stopped
            : row.Failed > 0 ? RunResult.Failed
            : row.Warnings > 0 || row.NotStarted > 0 || row.NotReachable.Count > 0 ? RunResult.DoneWithWarnings
            : RunResult.Done;
        var reason = stopped is not null ? stopped.TrimEnd('.') + "."
            : windowClosed && row.NotStarted > 0 ? $"The window ended before {Devices(row.NotStarted)} started."
            : null;
        await EndAsync(state, result, reason, ct).ConfigureAwait(false);
    }

    /// <summary>The devices of a run whose task ended (done, warnings, failed).</summary>
    private static int Ended(ScheduleRun row) => row.Done + row.Warnings + row.Failed;

    /// <summary>A run with stages whose first stage had no device that is Ok, or whose device did not run.</summary>
    public const string NoDeviceTested = "Stopped at stage 1: no device could be tested.";

    /// <summary>The devices in order, with an Ok device first for the first stage.</summary>
    private static IReadOnlyList<IDeviceInfo> Ordered(IReadOnlyList<IDeviceInfo> devices, bool stages)
    {
        if (!stages || devices.Count == 0 || devices[0].Status == DeviceStatus.Ok)
        {
            return devices;
        }

        for (var i = 1; i < devices.Count; i++)
        {
            if (devices[i].Status == DeviceStatus.Ok)
            {
                var list = new List<IDeviceInfo>(devices.Count) { devices[i] };
                for (var j = 0; j < devices.Count; j++)
                {
                    if (j != i)
                    {
                        list.Add(devices[j]);
                    }
                }

                return list;
            }
        }

        return devices;
    }

    /// <summary>Starts the tasks of every device of the stage in one run; a device that is not Ok now is counted by its status.</summary>
    private async Task StartStageAsync(RunState state, Queue<IDeviceInfo> pending, Dictionary<Guid, IDeviceInfo> inflight, string payload, TaskAudit audit, CancellationToken ct)
    {
        var free = pending.Count;
        if (free <= 0 || pending.Count == 0)
        {
            return;
        }

        var batch = new List<IDeviceInfo>(Math.Min(free, pending.Count));
        Dictionary<string, int>? notReachable = null;
        var removed = 0;
        var busy = 0;
        while (batch.Count < free && pending.Count > 0)
        {
            var device = pending.Dequeue();
            var current = await _deps.Devices.FindAsync(device.Id, ct).ConfigureAwait(false);
            if (current is null)
            {
                removed++;
            }
            else if (state.Busy.Contains(current.Id))
            {
                busy++;
            }
            else if (current.Status != DeviceStatus.Ok)
            {
                notReachable ??= new Dictionary<string, int>(StringComparer.Ordinal);
                var status = DeviceMessages.StatusText(current.Status);
                notReachable[status] = notReachable.GetValueOrDefault(status) + 1;
            }
            else
            {
                batch.Add(current);
            }
        }

        if (notReachable is not null || removed > 0 || busy > 0)
        {
            state.Update(r =>
            {
                var counts = new Dictionary<string, int>(r.NotReachable, StringComparer.Ordinal);
                foreach (var (status, count) in notReachable ?? [])
                {
                    counts[status] = counts.GetValueOrDefault(status) + count;
                }

                return r with { NotReachable = counts, Devices = r.Devices - removed, NotStarted = r.NotStarted + busy };
            });
        }

        if (batch.Count == 0)
        {
            return;
        }

        IReadOnlyList<Guid> ids;
        try
        {
            ids = await _deps.Tasks.RunAsync(SchedulesInfo.TaskPluginId, [.. batch.Select(d => d.Id)], payload, OwnerName, audit, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException or DevicesNotAvailableException)
        {
            foreach (var device in batch)
            {
                state.Apply(device, new DeviceReport(DeviceEnd.Failed, null, ex.Message));
            }

            return;
        }

        for (var i = 0; i < batch.Count; i++)
        {
            if (i < ids.Count)
            {
                inflight[ids[i]] = batch[i];
            }
            else
            {
                state.Apply(batch[i], new DeviceReport(DeviceEnd.Failed, null, "The task did not start."));
            }
        }
    }

    /// <summary>
    /// Takes the ends of the device tasks: their own reports (cheap, every wake-up); with <paramref name="full"/> (a poll, a close)
    /// also the runner's outcome of every task that never reported (cancelled while queued, failed before it ran).
    /// </summary>
    private async Task HarvestAsync(RunState state, Dictionary<Guid, IDeviceInfo> inflight, bool full, CancellationToken ct)
    {
        if (inflight.Count == 0)
        {
            return;
        }

        var ended = false;
        foreach (var (taskId, report) in state.TakeReports(inflight))
        {
            inflight.Remove(taskId, out var device);
            state.Apply(device!, report);
            ended = true;
        }

        if (full)
        {
            foreach (var (taskId, device) in inflight.ToList())
            {
                var active = _deps.Tasks.IsActive(taskId);
                if (active == true)
                {
                    continue;
                }

                var outcome = await _deps.Tasks.GetOutcomeAsync(taskId, ct).ConfigureAwait(false);
                if (outcome is null && active is null)
                {
                    continue;
                }

                // A report may have come in between the two reads.
                var report = state.TakeReport(taskId) ?? outcome?.State switch
                {
                    TaskEndState.Done => new DeviceReport(DeviceEnd.Done),
                    TaskEndState.DoneWithWarnings => new DeviceReport(DeviceEnd.Warning, null, outcome.Message),
                    TaskEndState.Failed => new DeviceReport(DeviceEnd.Failed, null, NodeRules.MessageOf(outcome, "The task failed.")),
                    _ => new DeviceReport(DeviceEnd.NotStarted),
                };
                inflight.Remove(taskId);
                state.Apply(device, report);
                ended = true;
            }
        }

        if (ended && state.ShouldPublish(_time.GetUtcNow(), _options.PublishInterval))
        {
            Changed?.Invoke(state.Schedule.Schedule.Id);
        }
    }
    /// <summary>
    /// No new task starts: the devices still waiting for their turn and the tasks that did not start yet (cancelled) are Not
    /// started. Running tasks run to their end.
    /// </summary>
    private void Close(RunState state, Dictionary<Guid, IDeviceInfo> inflight, Queue<IDeviceInfo> pending)
    {
        foreach (var taskId in state.CloseAndListWaiting(inflight.Keys))
        {
            _deps.Tasks.Cancel(taskId);
        }

        var left = pending.Count;
        pending.Clear();
        if (left > 0)
        {
            state.Update(r => r with { NotStarted = r.NotStarted + left });
        }
    }

    private async Task EndQuietlyAsync(RunState state, RunResult result, string reason)
    {
        try
        {
            await EndAsync(state, result, reason, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The end of a broken run is best effort; it is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRunFailed(ex, state.Schedule.Schedule.Name);
            Forget(state);
        }
    }

    /// <summary>Ends a run: its row with the counts, the failed devices, the MyAxis warning, the event, the page.</summary>
    private async Task EndAsync(RunState state, RunResult result, string? reason, CancellationToken ct)
    {
        state.Close();
        state.Update(r => r with { Result = result, Reason = reason ?? r.Reason, Ended = Now() });
        var row = state.Row;
        var failures = state.FailuresSnapshot();
        try
        {
            await Store.SaveRunAsync(new StoredRun { Run = row, DeviceTime = state.DeviceTime }, failures, ct).ConfigureAwait(false);
            var schedule = state.Schedule.Schedule;
            if (schedule.Actions?.Any(a => a.Kind == NodeKind.UpgradeOnline) == true && (state.SignInFailed || row.Done + row.Warnings > 0))
            {
                await Store.UpdateAsync(schedule.Id, s => s with { SignInMissing = state.SignInFailed }, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (state.MarkFired())
            {
                Fire(state.Schedule.Schedule.Name, row, failures);
            }

            Forget(state);
        }

        LogEnded(state.Schedule.Schedule.Name, result, row.Done, row.Failed);
        Changed?.Invoke(row.ScheduleId);
    }

    /// <summary>A start that did not run: one row (Missed or Skipped) and its event.</summary>
    private async Task RecordAsync(StoredSchedule stored, DateTimeOffset due, RunResult result, string reason, CancellationToken ct)
    {
        var row = new ScheduleRun
        {
            Id = Guid.NewGuid(),
            ScheduleId = stored.Schedule.Id,
            Started = due,
            Ended = Now(),
            Result = result,
            Reason = reason,
        };
        await Store.SaveRunAsync(new StoredRun { Run = row }, null, ct).ConfigureAwait(false);
        Fire(stored.Schedule.Name, row, []);
        Changed?.Invoke(stored.Schedule.Id);
    }

    private void Forget(RunState state)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(state.Schedule.Schedule.Id, out var current) && ReferenceEquals(current, state))
            {
                _running.Remove(state.Schedule.Schedule.Id);
            }

            _byRun.Remove(state.Row.Id);
        }
    }

    /// <summary>The event "Schedule run ended" (once per run, also Missed and Skipped).</summary>
    private void Fire(string name, ScheduleRun row, IReadOnlyList<RunFailure> failures) =>
        _deps.Rules?.Fire(SchedulesInfo.RunEndedEvent, null, RunEndedEvent.Data(name, row, failures));

    /// <summary>"Schedule Firmware (anna)": the user the audit log names for a run's tasks.</summary>
    public static string AuditUserOf(Schedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return $"Schedule {schedule.Name} ({schedule.Owner})";
    }

    private static string Devices(int count) => count == 1 ? "1 device" : count.ToString("N0", CultureInfo.InvariantCulture) + " devices";

    // ---- the task of one device ----

    /// <summary>
    /// One device's task "Run &lt;schedule&gt;": one named step per action in order, like Provisioning. An action's Error ends the
    /// task when its Continue on error is off; a Warning always continues; Not needed is a success. The task fails when an action
    /// failed. A task that starts after its run closed (window end, stop) does nothing and counts as Not started.
    /// </summary>
    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = PluginJson.TryParse<RunTaskPayload>(payloadJson, SchedulesJson.Options) ?? throw new InvalidOperationException("The task has no schedule.");
        RunState? state;
        lock (_gate)
        {
            state = _byRun.GetValueOrDefault(payload.RunId);
        }

        if (state is null || state.Schedule.Schedule.Id != payload.ScheduleId)
        {
            throw new InvalidOperationException(RunGone);
        }

        if (!state.Enter(ctx.TaskId))
        {
            throw new OperationCanceledException(RunGone);
        }

        var started = _time.GetTimestamp();
        DeviceReport? report = null;
        try
        {
            report = await RunActionsAsync(ctx, device, payload, state, ct).ConfigureAwait(false);
        }
        finally
        {
            state.LeaveTask(ctx.TaskId);
            state.Report(ctx.TaskId, (report ?? new DeviceReport(DeviceEnd.Failed, null, "The task was cancelled.")) with { Ran = _time.GetElapsedTime(started) });
        }

        if (report.End == DeviceEnd.Failed)
        {
            throw new InvalidOperationException(report.Message);
        }
    }

    private async Task<DeviceReport> RunActionsAsync(ITaskExecutionContext ctx, IDeviceInfo device, RunTaskPayload payload, RunState state, CancellationToken ct)
    {
        // The actions of the run's schedule as it started, never the payload's (memory only, but checked when the schedule was saved).
        var actions = state.Schedule.Schedule.Actions ?? [];
        ctx.PlanSteps([.. actions.Select(StepNameOf)]);
        var host = new RunNodeHost(_deps.Tasks, state.Owner?.Tasks, _deps.Devices, _deps.Vapix, _deps.Vault, _time, _options.Nodes, "Schedule " + state.Schedule.Schedule.Name);
        var camera = new CameraInfo(device.Serial, device.Address, device.Model, device.Id);
        var profile = new Profile { Id = state.Schedule.Schedule.Id, Name = state.Schedule.Schedule.Name, Nodes = actions };
        var failures = new List<(string Step, string Message)>();
        var warned = false;
        var signIn = false;
        foreach (var action in actions)
        {
            var node = _catalog.Find(action.Kind);
            var stepName = StepNameOf(action);
            state.EnterStep(ctx.TaskId, stepName);
            if (state.ShouldPublish(_time.GetUtcNow(), _options.PublishInterval))
            {
                Changed?.Invoke(state.Schedule.Schedule.Id);
            }

            using var step = ctx.BeginStep(stepName);
            var result = node is null
                ? NodeResult.Error(ActionCatalog.NotAnAction)
                : await RunNodeAsync(node, new NodeRun(host, camera, profile, action), ct).ConfigureAwait(false);
            switch (result.State)
            {
                case NodeState.Success:
                    step.Complete(result.Message);
                    ctx.Log(TaskLogLevel.Info, $"{node?.Name}: {result.Message}");
                    break;
                case NodeState.NotNeeded:
                    step.Skip(result.Message);
                    ctx.Log(TaskLogLevel.Info, $"{node?.Name}: {result.Message}");
                    break;
                case NodeState.Warning:
                    step.Warn(result.Message);
                    warned = true;
                    break;
                default:
                    step.Fail(result.Message);
                    ctx.Log(TaskLogLevel.Error, $"{node?.Name ?? action.Kind.ToString()}: {result.Message}");
                    failures.Add((stepName, result.Message));
                    signIn |= action.Kind == NodeKind.UpgradeOnline && result.Message == UpgradeOnlineNode.SignIn;
                    break;
            }

            if (result.State == NodeState.Error && !action.ContinueOnError)
            {
                break;
            }
        }

        if (failures.Count > 0)
        {
            var (firstStep, first) = failures[0];
            return new DeviceReport(DeviceEnd.Failed, firstStep, failures.Count == 1 ? first : $"{failures.Count} steps failed. {first}") { SignIn = signIn };
        }

        return new DeviceReport(warned ? DeviceEnd.Warning : DeviceEnd.Done);
    }

    private async Task<NodeResult> RunNodeAsync(IProvisioningNode node, NodeRun run, CancellationToken ct)
    {
        try
        {
            return await node.RunAsync(run, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // An action's failure is this device's red step; the next actions decide by Continue on error.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogActionFailed(ex, node.Name, run.Camera.Serial);
            return NodeResult.Error($"{node.Name} failed: {ex.Message}");
        }
    }

    /// <summary>The step name of an action ("Restart the device").</summary>
    public string StepNameOf(FlowNode action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _catalog.Find(action.Kind)?.StepName ?? "Run " + action.Kind;
    }

    private async Task LoopAsync(bool catchUp, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.Tick, _time);
        var first = true;
        try
        {
            do
            {
                try
                {
                    await TickAsync(missedOnly: first && !catchUp, ct).ConfigureAwait(false);
                    first = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // A failed look is logged; the next one tries again.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogTickFailed(ex);
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
    }

    /// <summary>The reason of a run that ended because the plugin stopped.</summary>
    public const string PluginStopped = "Schedules stopped during the run.";

    /// <summary>
    /// Stops the engine: no tick any more; every running run closes (its queued tasks cancelled, no further stage) and ends
    /// Stopped at once; its running tasks run to their end and their devices get no second task from a later engine.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // First no new run: the tick loop ends and Run now is refused; then every run closes.
        lock (_gate)
        {
            _stopping = true;
        }

        if (!_ticks.IsCancellationRequested)
        {
            await _ticks.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is { } loop)
        {
            await loop.ConfigureAwait(false);
        }

        List<RunState> states;
        lock (_gate)
        {
            states = [.. _running.Values];
        }

        foreach (var state in states)
        {
            state.RequestStop(PluginStopped, abandon: true);
        }

        await Task.WhenAll(states.Select(s => s.Loop).OfType<Task>()).ConfigureAwait(false);
        if (!_life.IsCancellationRequested)
        {
            await _life.CancelAsync().ConfigureAwait(false);
        }

        _life.Dispose();
        _ticks.Dispose();
        _tick.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule {Name}: run started ({By})")]
    private partial void LogStarted(string name, string by);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule {Name}: run ended {Result}, {Done} done, {Failed} failed")]
    private partial void LogEnded(string name, RunResult result, int done, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schedule {Name}: the run failed")]
    private partial void LogRunFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schedule action {Action} failed on {Serial}")]
    private partial void LogActionFailed(Exception ex, string action, string serial);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The look for due schedules failed")]
    private partial void LogTickFailed(Exception ex);
}
