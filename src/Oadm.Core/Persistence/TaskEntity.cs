using Oadm.Core.Tasks;

namespace Oadm.Core.Persistence;

/// <summary>
/// Row of the Tasks table. Only <see cref="EfTaskStore"/> touches it; the rest of the code works
/// with the immutable <see cref="Tasks.TaskRecord"/> snapshots of the task engine.
/// </summary>
public sealed class TaskEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PluginId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TaskState Status { get; set; } = TaskState.Queued;

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

    public List<TaskDeviceResultEntity> Results { get; set; } = [];
}

/// <summary>
/// Per-device outcome of a task (row of TaskDeviceResults). Key (TaskId, DeviceId). No FK to
/// Devices so history survives device removal.
/// </summary>
public sealed class TaskDeviceResultEntity
{
    public Guid TaskId { get; set; }
    public Guid DeviceId { get; set; }
    public TaskState Status { get; set; } = TaskState.Queued;
    public string? Message { get; set; }

    /// <summary>0-100.</summary>
    public int Progress { get; set; }
}
