namespace Oadm.Core.Tasks;

public sealed class TaskEngineOptions
{
    /// <summary>
    /// How many tasks (= devices) of one plugin run at the same time unless the plugin sets
    /// <c>ITaskPlugin.MaxParallelDevices</c>; further tasks wait in Queued. Default 8.
    /// </summary>
    public int MaxParallelTasksPerPlugin { get; set; } = 8;

    /// <summary>Buffered changes per change-feed subscriber before the oldest are dropped.</summary>
    public int ChangeFeedCapacity { get; set; } = 4096;

    /// <summary>Log entries kept per task; later entries are dropped (the last kept one says so). Default 1000.</summary>
    public int MaxLogEntriesPerTask { get; set; } = 1000;

    /// <summary>Minimum time between two persisted progress messages of one device. Default 1 s.</summary>
    public TimeSpan ProgressPersistInterval { get; set; } = TimeSpan.FromSeconds(1);
}
