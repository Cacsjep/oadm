using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Tasks;

namespace Oadm.Server.Tasks;

/// <summary>
/// Credential changes requested by running tasks: stored encrypted in the <see cref="CredentialStore"/>,
/// followed by a full refresh. The cached VAPIX client is replaced lazily (its cache key includes the
/// credentials), so a client still used by the running task is not disposed under it.
/// </summary>
public sealed class TaskDeviceCredentials(CredentialStore credentials, DevicePollingService polling) : ITaskDeviceCredentials
{
    public async Task InvalidateAsync(Guid deviceId, CancellationToken ct)
    {
        await credentials.RemoveAsync(deviceId, ct).ConfigureAwait(false);
        polling.QueueRefresh([deviceId]);
    }

    public async Task UpdateAsync(Guid deviceId, string userName, string password, CancellationToken ct)
    {
        await credentials.SetAsync(deviceId, userName, password, ct).ConfigureAwait(false);
        polling.QueueRefresh([deviceId]);
    }
}
