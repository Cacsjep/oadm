namespace Oadm.Core.Tasks;

/// <summary>
/// Lets running tasks change the credentials OADM stores for their device
/// (<c>ITaskExecutionContext.MarkCredentialsInvalid</c> / <c>UpdateCredentialsAsync</c>).
/// The server implementation encrypts them in the credential store and refreshes the device.
/// </summary>
public interface ITaskDeviceCredentials
{
    /// <summary>Deletes the stored credentials and queues a full refresh of the device.</summary>
    Task InvalidateAsync(Guid deviceId, CancellationToken ct);

    /// <summary>Stores (encrypts) new credentials for the device. Never logs them.</summary>
    Task UpdateAsync(Guid deviceId, string userName, string password, CancellationToken ct);
}
