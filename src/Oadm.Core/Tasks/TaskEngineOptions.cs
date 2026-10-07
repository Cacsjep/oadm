namespace Oadm.Core.Tasks;

public sealed class TaskEngineOptions
{
    /// <summary>
    /// How many tasks (= devices) of one plugin run at the same time when no
    /// <see cref="MaxParallelTasksPerPluginSource"/> is set; further tasks wait in Queued. A plugin's
    /// <c>ITaskPlugin.MaxParallelDevices</c> can only lower it. Default 16 (the default of the server setting
    /// <c>Tasks.MaxParallelPerPlugin</c>).
    /// </summary>
    public int MaxParallelTasksPerPlugin { get; set; } = 16;

    /// <summary>
    /// Live source of the per-plugin limit (the server setting <c>Tasks.MaxParallelPerPlugin</c>); null uses
    /// <see cref="MaxParallelTasksPerPlugin"/>. Read whenever a queued task is scheduled, so a change applies
    /// to tasks that start afterwards; running tasks are never interrupted. Values are clamped to 1..256.
    /// After a change call <see cref="TaskEngine.RescheduleQueued"/> so a raised limit starts waiting tasks
    /// at once.
    /// </summary>
    public Func<int>? MaxParallelTasksPerPluginSource { get; set; }

    /// <summary>Unused since change-feed subscriptions coalesce per task instead of dropping changes; kept for compatibility.</summary>
    public int ChangeFeedCapacity { get; set; } = 4096;

    /// <summary>Log entries kept per task; later entries are dropped (the last kept one says so). Default 1000.</summary>
    public int MaxLogEntriesPerTask { get; set; } = 1000;

    /// <summary>Minimum time between two persisted progress messages of one device. Default 1 s.</summary>
    public TimeSpan ProgressPersistInterval { get; set; } = TimeSpan.FromSeconds(1);
}
