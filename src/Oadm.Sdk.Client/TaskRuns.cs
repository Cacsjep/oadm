namespace Oadm.Sdk.Client;

/// <summary>Where a task of a run that a dialog started stands (the states of the tasks pane).</summary>
public enum TaskRunStatus
{
    /// <summary>Waiting for a free slot of its plugin (or not seen by the client yet).</summary>
    Queued = 0,

    /// <summary>Running on its device.</summary>
    Running = 1,

    /// <summary>Ended without problems.</summary>
    Done = 2,

    /// <summary>Ended, with a warning in <see cref="TaskRunState.Message"/>.</summary>
    DoneWithWarnings = 3,

    /// <summary>Ended with the error in <see cref="TaskRunState.Message"/>.</summary>
    Failed = 4,

    /// <summary>Cancelled by a user before it ended.</summary>
    Cancelled = 5,
}

/// <summary>One task (one device) of a run that a dialog started with <see cref="ITaskDialogContext.RunTaskAsync"/>.</summary>
/// <param name="DeviceId">The task's device.</param>
/// <param name="TaskId">The task's id on the server (the same task as in the tasks pane).</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Step">The step it is on, as the tasks pane shows it ("Step 3/6 · Wait for the https certificate"); empty without steps.</param>
/// <param name="Message">The error or warning of an ended task, else the latest progress message; may be empty.</param>
public sealed record TaskRunState(Guid DeviceId, string TaskId, TaskRunStatus Status, string Step, string Message)
{
    /// <summary>Queued or running: the task has not ended yet.</summary>
    public bool IsActive => Status is TaskRunStatus.Queued or TaskRunStatus.Running;
}
