namespace Oadm.Sdk.Plugins;

/// <summary>State of one step of a task. Mirrors the TaskStepState proto enum.</summary>
public enum TaskStepState
{
    /// <summary>Planned with <see cref="ITaskExecutionContext.PlanSteps"/>, not started yet.</summary>
    Pending = 0,
    Running = 1,
    Done = 2,

    /// <summary>Finished with a warning (<see cref="ITaskStep.Warn"/>); the task ends "Done with warnings".</summary>
    Warning = 3,

    /// <summary>Did not apply (<see cref="ITaskStep.Skip"/>) or never ran because the task ended before it.</summary>
    Skipped = 4,
    Failed = 5,
}

/// <summary>Immutable view of one step. <see cref="Index"/> is the position in the task's step list (0-based).</summary>
public sealed record TaskStepInfo(
    int Index,
    string Name,
    TaskStepState State,
    string? Detail,
    int Progress,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? FinishedUtc)
{
    public bool IsFinished => State is TaskStepState.Done or TaskStepState.Warning or TaskStepState.Skipped or TaskStepState.Failed;
}

/// <summary>
/// One named step of a task, started with <see cref="ITaskExecutionContext.BeginStep"/>. Every device
/// request and every wait of a task plugin is its own step, so the user always sees what the task is
/// doing ("Upload firmware", "Wait for the device to come back").
/// <para>
/// End a step with exactly one of <see cref="Complete"/>, <see cref="Warn"/>, <see cref="Skip"/> or
/// <see cref="Fail"/>; later calls are ignored. <see cref="IDisposable.Dispose"/> without an explicit end
/// completes the step (Done). Beginning the next step completes a still running one the same way. When an
/// exception escapes the plugin, the step that was running (or the one just ended by Dispose while the
/// exception propagated out of its <c>using</c> block) is marked Failed with the exception message.
/// </para>
/// <code>
/// using (var step = ctx.BeginStep("Send restart"))
/// {
///     await ctx.Vapix.RestartAsync(ct);          // throws: the step ends Failed
/// }                                              // returns: the step ends Done
///
/// var info = await ctx.StepAsync("Read device info", async step =&gt; await ReadAsync(ct));
/// </code>
/// </summary>
public interface ITaskStep : IDisposable
{
    string Name { get; }

    TaskStepState State { get; }

    /// <summary>Progress of this step (0-100) and an optional detail shown next to it, e.g. "12 of 80 MB".</summary>
    void ReportProgress(int percent, string? detail = null);

    /// <summary>Ends the step as Done, with an optional result detail ("AXIS OS 12.11.77").</summary>
    void Complete(string? detail = null);

    /// <summary>
    /// Ends the step as Warning and reports <paramref name="message"/> as a task warning
    /// (<see cref="ITaskExecutionContext.ReportWarning"/>), so the task ends "Done with warnings".
    /// </summary>
    void Warn(string message);

    /// <summary>Ends the step as Skipped (it did not apply), e.g. "Host name: keep unchanged".</summary>
    void Skip(string reason);

    /// <summary>
    /// Ends the step as Failed with <paramref name="message"/>. This only records the failure: throw
    /// afterwards to fail the task (an exception escaping the step marks it Failed by itself).
    /// </summary>
    void Fail(string message);
}

/// <summary>Helpers for the common step patterns on <see cref="ITaskExecutionContext"/>.</summary>
public static class TaskStepExtensions
{
    /// <summary>
    /// Runs <paramref name="body"/> as one step: Done when it returns (unless it ended the step itself),
    /// Failed with the exception message when it throws ("Cancelled." on cancellation); the exception is rethrown.
    /// </summary>
    public static async Task<T> StepAsync<T>(this ITaskExecutionContext ctx, string name, Func<ITaskStep, Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(body);
        using var step = ctx.BeginStep(name);
        try
        {
            var result = await body(step).ConfigureAwait(false);
            step.Complete();
            return result;
        }
        catch (OperationCanceledException)
        {
            step.Fail("Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            step.Fail(ex.Message);
            throw;
        }
    }

    /// <inheritdoc cref="StepAsync{T}(ITaskExecutionContext, string, Func{ITaskStep, Task{T}})" />
    public static Task StepAsync(this ITaskExecutionContext ctx, string name, Func<ITaskStep, Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ctx.StepAsync<bool>(name, async step =>
        {
            await body(step).ConfigureAwait(false);
            return true;
        });
    }

    /// <summary>Marks a (planned) step as Skipped without running it: <c>ctx.BeginStep(name).Skip(reason)</c>.</summary>
    public static void SkipStep(this ITaskExecutionContext ctx, string name, string reason)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        using var step = ctx.BeginStep(name);
        step.Skip(reason);
    }
}

/// <summary>
/// The step list of one task: the state machine behind <see cref="ITaskExecutionContext.PlanSteps"/> and
/// <see cref="ITaskExecutionContext.BeginStep"/>. Used by the server and usable by test fakes, so plugin
/// tests see exactly the server's step semantics. Thread-safe; <see cref="Changed"/> is raised after every
/// change, outside the lock.
/// </summary>
public sealed class TaskStepList
{
    /// <summary>Steps beyond this count are not recorded (their handles still work but change nothing).</summary>
    public const int MaxSteps = 200;

    public const int MaxNameLength = 200;

    public const int MaxDetailLength = 1000;

    /// <summary>
    /// Name of the step <see cref="Close"/> appends when a task succeeds (Done or Done with warnings), so the
    /// current step of a finished task reads "Completed". Failed and cancelled tasks get none.
    /// </summary>
    public const string CompletedStepName = "Completed";

    private readonly Lock _sync = new();
    private readonly List<Slot> _steps = [];
    private readonly TimeProvider _time;
    private readonly Action<string>? _onWarning;

    /// <summary>Step ended by Dispose (not explicitly) and nothing happened since: an escaping exception fails it.</summary>
    private Slot? _implicitlyEnded;
    private bool _closed;

    /// <param name="timeProvider">Clock for the step times; the system clock when null.</param>
    /// <param name="onWarning">Called with the message of <see cref="ITaskStep.Warn"/> (the task warning).</param>
    public TaskStepList(TimeProvider? timeProvider = null, Action<string>? onWarning = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _onWarning = onWarning;
    }

    /// <summary>Raised after every change of a step.</summary>
    public event EventHandler? Changed;

    /// <summary>Number of steps.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _steps.Count;
            }
        }
    }

    /// <summary>
    /// Index of the running step; else of the last step that ended (the failed one of a failed task);
    /// -1 when no step started yet.
    /// </summary>
    public int CurrentIndex
    {
        get
        {
            return CurrentIndexOf(Snapshot());
        }
    }

    /// <summary>
    /// Overall progress derived from the steps (0-100): every finished step counts as one, the running
    /// one with its own progress. Null without steps.
    /// </summary>
    public int? Progress
    {
        get
        {
            lock (_sync)
            {
                if (_steps.Count == 0)
                {
                    return null;
                }

                var sum = _steps.Sum(s => s.State switch
                {
                    TaskStepState.Pending => 0.0,
                    TaskStepState.Running => s.Progress / 100.0,
                    _ => 1.0,
                });
                return (int)Math.Floor(sum * 100 / _steps.Count);
            }
        }
    }

    /// <summary>Appends pending steps; names already pending are not added twice.</summary>
    public void Plan(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var changed = false;
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            foreach (var raw in names)
            {
                var name = CleanName(raw);
                if (_steps.Count >= MaxSteps || _steps.Exists(s => s.State == TaskStepState.Pending && s.Name == name))
                {
                    continue;
                }

                _steps.Add(new Slot(name));
                changed = true;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>
    /// Starts a step. A still running step is completed first. A pending (planned) step with this name is
    /// activated and moved before the other pending steps; otherwise a new step is inserted there.
    /// </summary>
    public ITaskStep Begin(string name)
    {
        var clean = CleanName(name);
        Slot? slot;
        lock (_sync)
        {
            if (_closed)
            {
                return new Handle(this, null, clean);
            }

            var now = _time.GetUtcNow();
            if (_steps.Find(s => s.State == TaskStepState.Running) is { } running)
            {
                EndLocked(running, TaskStepState.Done, null, now);
            }

            var firstPending = _steps.FindIndex(s => s.State == TaskStepState.Pending);
            slot = _steps.Find(s => s.State == TaskStepState.Pending && s.Name == clean);
            if (slot is not null)
            {
                _steps.Remove(slot);
            }
            else if (_steps.Count < MaxSteps)
            {
                slot = new Slot(clean);
            }

            if (slot is not null)
            {
                _steps.Insert(firstPending < 0 ? _steps.Count : Math.Min(firstPending, _steps.Count), slot);
                slot.State = TaskStepState.Running;
                slot.StartedUtc = now;
                slot.Progress = 0;
                slot.Detail = null;
            }

            _implicitlyEnded = null;
        }

        RaiseChanged();
        return new Handle(this, slot, clean);
    }

    public IReadOnlyList<TaskStepInfo> Snapshot()
    {
        lock (_sync)
        {
            return [.. _steps.Select((s, i) => new TaskStepInfo(i, s.Name, s.State, s.Detail, s.Progress, s.StartedUtc, s.FinishedUtc))];
        }
    }

    /// <summary>
    /// For hosts, when the plugin threw: the running step, else the step just ended by Dispose, becomes
    /// Failed with <paramref name="message"/>. Returns false when no step was affected.
    /// </summary>
    public bool FailCurrent(string message)
    {
        lock (_sync)
        {
            var target = _steps.Find(s => s.State == TaskStepState.Running)
                ?? (_implicitlyEnded is { State: TaskStepState.Done } ended ? ended : null);
            _implicitlyEnded = null;
            if (target is null)
            {
                return false;
            }

            target.State = TaskStepState.Failed;
            target.Detail = CleanDetail(message);
            target.FinishedUtc = _time.GetUtcNow();
        }

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// For hosts, when the task ends: a running step ends Done (<paramref name="succeeded"/>) or Failed,
    /// pending steps become Skipped with <paramref name="skipReason"/>. On success a final step
    /// <see cref="CompletedStepName"/> (Done) is appended, also beyond <see cref="MaxSteps"/>. Later changes
    /// are ignored.
    /// </summary>
    public void Close(bool succeeded, string skipReason)
    {
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            var now = _time.GetUtcNow();
            foreach (var slot in _steps)
            {
                if (slot.State == TaskStepState.Running)
                {
                    EndLocked(slot, succeeded ? TaskStepState.Done : TaskStepState.Failed, null, now);
                }
                else if (slot.State == TaskStepState.Pending)
                {
                    slot.State = TaskStepState.Skipped;
                    slot.Detail = CleanDetail(skipReason);
                }
            }

            if (succeeded)
            {
                _steps.Add(new Slot(CompletedStepName)
                {
                    State = TaskStepState.Done,
                    Progress = 100,
                    StartedUtc = now,
                    FinishedUtc = now,
                });
            }

            _implicitlyEnded = null;
        }

        RaiseChanged();
    }

    /// <summary>
    /// The running step's index; else the last step that started (the failed one of a failed task), else
    /// the last one that ended; -1 when there is none.
    /// </summary>
    public static int CurrentIndexOf(IReadOnlyList<TaskStepInfo> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var lastStarted = -1;
        var lastEnded = -1;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].State == TaskStepState.Running)
            {
                return i;
            }

            if (steps[i].StartedUtc is not null)
            {
                lastStarted = i;
            }

            if (steps[i].State != TaskStepState.Pending)
            {
                lastEnded = i;
            }
        }

        return lastStarted >= 0 ? lastStarted : lastEnded;
    }

    private static void EndLocked(Slot slot, TaskStepState state, string? detail, DateTimeOffset now)
    {
        slot.State = state;
        if (detail is not null)
        {
            slot.Detail = CleanDetail(detail);
        }

        if (state is TaskStepState.Done or TaskStepState.Warning)
        {
            slot.Progress = 100;
        }

        slot.FinishedUtc = now;
    }

    private void End(Slot? slot, TaskStepState state, string? detail, bool isExplicit)
    {
        if (slot is null)
        {
            return;
        }

        lock (_sync)
        {
            if (slot.State != TaskStepState.Running)
            {
                return;
            }

            EndLocked(slot, state, detail, _time.GetUtcNow());
            _implicitlyEnded = isExplicit ? null : slot;
        }

        RaiseChanged();
    }

    private void ApplyProgress(Slot? slot, int percent, string? detail)
    {
        if (slot is null)
        {
            return;
        }

        lock (_sync)
        {
            if (slot.State != TaskStepState.Running)
            {
                return;
            }

            slot.Progress = Math.Clamp(percent, 0, 100);
            if (detail is not null)
            {
                slot.Detail = CleanDetail(detail);
            }
        }

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static string CleanName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    private static string? CleanDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var trimmed = detail.Trim();
        return trimmed.Length > MaxDetailLength ? trimmed[..MaxDetailLength] : trimmed;
    }

    private sealed class Slot(string name)
    {
        public string Name { get; } = name;

        public TaskStepState State { get; set; } = TaskStepState.Pending;

        public string? Detail { get; set; }

        public int Progress { get; set; }

        public DateTimeOffset? StartedUtc { get; set; }

        public DateTimeOffset? FinishedUtc { get; set; }
    }

    /// <summary>Handle given to the plugin. A null slot (list closed or full) records nothing.</summary>
    private sealed class Handle(TaskStepList owner, Slot? slot, string name) : ITaskStep
    {
        private TaskStepState _detachedState = TaskStepState.Running;

        public string Name { get; } = name;

        public TaskStepState State
        {
            get
            {
                if (slot is null)
                {
                    return _detachedState;
                }

                lock (owner._sync)
                {
                    return slot.State;
                }
            }
        }

        public void ReportProgress(int percent, string? detail = null) => owner.ApplyProgress(slot, percent, detail);

        public void Complete(string? detail = null) => EndExplicit(TaskStepState.Done, detail);

        public void Warn(string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            EndExplicit(TaskStepState.Warning, message);
            owner._onWarning?.Invoke(message);
        }

        public void Skip(string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            EndExplicit(TaskStepState.Skipped, reason);
        }

        public void Fail(string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            EndExplicit(TaskStepState.Failed, message);
        }

        public void Dispose()
        {
            if (slot is null)
            {
                if (_detachedState == TaskStepState.Running)
                {
                    _detachedState = TaskStepState.Done;
                }

                return;
            }

            owner.End(slot, TaskStepState.Done, null, isExplicit: false);
        }

        private void EndExplicit(TaskStepState state, string? detail)
        {
            if (slot is null)
            {
                if (_detachedState == TaskStepState.Running)
                {
                    _detachedState = state;
                }

                return;
            }

            owner.End(slot, state, detail, isExplicit: true);
        }
    }
}
