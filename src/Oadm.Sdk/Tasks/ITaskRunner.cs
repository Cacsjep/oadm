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
}
