namespace Oadm.Sdk.Tasks;

public interface ITaskRunner
{
    /// <summary>Queues a task plugin for the given devices and returns the task id.</summary>
    Task<Guid> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct);
}
