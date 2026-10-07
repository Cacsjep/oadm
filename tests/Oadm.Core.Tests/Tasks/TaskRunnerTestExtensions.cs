using Oadm.Sdk.Tasks;

namespace Oadm.Core.Tests.Tasks;

internal static class TaskRunnerTestExtensions
{
    /// <summary>Runs on exactly one device and returns its task id (a task always targets one device).</summary>
    public static async Task<Guid> RunOneAsync(this ITaskRunner runner, string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
    {
        var ids = await runner.RunAsync(pluginId, deviceIds, payloadJson, owner, ct).ConfigureAwait(false);
        return Assert.Single(ids);
    }
}
