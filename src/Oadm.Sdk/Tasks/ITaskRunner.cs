namespace Oadm.Sdk.Tasks;

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
}
