namespace Oadm.Core.Tasks;

public sealed class TaskEngineOptions
{
    /// <summary>How many devices of one task run at the same time. Default 8.</summary>
    public int MaxParallelDevicesPerTask { get; set; } = 8;

    /// <summary>Buffered changes per change-feed subscriber before the oldest are dropped.</summary>
    public int ChangeFeedCapacity { get; set; } = 4096;
}
