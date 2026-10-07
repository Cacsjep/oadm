namespace Oadm.Core.Persistence;

/// <summary>State of a task or of one device within a task.</summary>
public enum TaskRecordStatus
{
    Queued = 1,
    Running = 2,
    Done = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>Persisted task (row of the Tasks table). Named to avoid clashing with System.Threading.Tasks.Task.</summary>
public sealed class TaskRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PluginId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TaskRecordStatus Status { get; set; } = TaskRecordStatus.Queued;

    /// <summary>Client machine/user name that started the task.</summary>
    public string Owner { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }

    /// <summary>0-100.</summary>
    public int Progress { get; set; }

    public string? PayloadJson { get; set; }

    /// <summary>Unused in Goal 1.</summary>
    public DateTime? ScheduledUtc { get; set; }

    public List<TaskDeviceResult> Results { get; set; } = [];

    public static bool IsTerminal(TaskRecordStatus status) =>
        status is TaskRecordStatus.Done or TaskRecordStatus.Failed or TaskRecordStatus.Cancelled;
}

/// <summary>Per-device outcome of a task. Key (TaskId, DeviceId). No FK to Devices so history survives device removal.</summary>
public sealed class TaskDeviceResult
{
    public Guid TaskId { get; set; }
    public Guid DeviceId { get; set; }
    public TaskRecordStatus Status { get; set; } = TaskRecordStatus.Queued;
    public string? Message { get; set; }

    /// <summary>0-100.</summary>
    public int Progress { get; set; }
}
