namespace Oadm.Sdk.Tasks;

/// <summary>Starts and cancels tasks from a core plugin (<see cref="Plugins.ICorePluginContext.Tasks"/>).</summary>
public interface ITaskRunner
{
    /// <summary>
    /// Runs a task plugin on the given devices: one task per device (all sharing one batch id), so one
    /// failing device never affects the others. Returns the task ids in device order.
    /// </summary>
    Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct);

    /// <summary>
    /// Requests cancellation of a task (queued: ends Cancelled before start; running: its token is cancelled).
    /// Returns false when the task is not active or the host cannot cancel.
    /// </summary>
    bool Cancel(Guid taskId) => false;

    /// <summary>
    /// Whether the task is still queued or running; null when the host cannot tell. A task cancelled while queued never
    /// starts, so a plugin waiting for its tasks asks here instead of waiting for them to report.
    /// </summary>
    bool? IsActive(Guid taskId) => null;

    /// <summary>
    /// Whether any of the tasks is still queued or running: true when one is, false when none is, null when the host cannot
    /// tell (SDK 0.4.0). On <see cref="Plugins.ICallContext.Tasks"/> of a user with a device group it answers for the tasks of
    /// the devices in the user's view, whichever user started them (<see cref="IsActive"/> answers only the user's own);
    /// a task on a device outside the view makes the answer null. The default asks <see cref="IsActive"/> per task.
    /// </summary>
    Task<bool?> AnyActiveAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        var unknown = false;
        foreach (var id in taskIds)
        {
            switch (IsActive(id))
            {
                case true:
                    return Task.FromResult<bool?>(true);
                case null:
                    unknown = true;
                    break;
            }
        }

        return Task.FromResult<bool?>(unknown ? null : false);
    }

    /// <summary>
    /// How the task ended and its message as the tasks pane shows it (SDK 0.7.0): the error of a failed task, the warning of
    /// one done with warnings. Null while it is queued or running, for an unknown task, and when the host cannot tell. On
    /// <see cref="Plugins.ICallContext.Tasks"/> of a user with a device group it answers like <see cref="AnyActiveAsync"/>:
    /// for the tasks of devices in the user's view, null for the others. Default (older hosts): null.
    /// </summary>
    Task<TaskOutcome?> GetOutcomeAsync(Guid taskId, CancellationToken ct) => Task.FromResult<TaskOutcome?>(null);
}

/// <summary>How a task ended (<see cref="ITaskRunner.GetOutcomeAsync"/>, SDK 0.7.0).</summary>
public enum TaskEndState
{
    Done = 0,

    /// <summary>Ended, with the warning in <see cref="TaskOutcome.Message"/>.</summary>
    DoneWithWarnings = 1,

    /// <summary>Ended with the error in <see cref="TaskOutcome.Message"/>.</summary>
    Failed = 2,

    Cancelled = 3,
}

/// <summary>The end of a task (<see cref="ITaskRunner.GetOutcomeAsync"/>, SDK 0.7.0).</summary>
/// <param name="DeviceId">The task's device.</param>
/// <param name="State">How it ended.</param>
/// <param name="Message">Its last message (error, warning or progress), as the tasks pane shows it; null without one.</param>
public sealed record TaskOutcome(Guid DeviceId, TaskEndState State, string? Message);
