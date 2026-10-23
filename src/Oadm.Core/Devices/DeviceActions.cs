using Oadm.Core.DeviceGroups;
using Oadm.Core.Security;
using Oadm.Core.Vapix;

namespace Oadm.Core.Devices;

/// <summary>
/// What the device list's gRPC calls do with devices (remove, refresh, log in, credentials, first password, the web
/// interface relay), only on <see cref="ViewedDevices"/>: devices a <see cref="DeviceView"/> handed out for the caller, never
/// raw ids (docs/spec/device-scope.md, Tests: Capability type). The services behind it keep their raw-id forms for system
/// work (polling, task runs, adding devices).
/// </summary>
public sealed class DeviceActions(
    DeviceRepository devices,
    CredentialStore credentials,
    VapixClientFactory clients,
    DevicePollingService polling,
    DeviceLoginService logins,
    DeviceFirstPasswordService firstPasswords,
    DeviceStatusNotes? notes = null)
{
    /// <summary>The ids of every device with stored credentials (one read; the feed asks it per batch).</summary>
    public Task<IReadOnlySet<Guid>> DeviceIdsWithCredentialsAsync(CancellationToken ct) => credentials.ListDeviceIdsWithCredentialsAsync(ct);

    /// <summary>True when credentials are stored for <paramref name="device"/> (a device the caller's feed already carries).</summary>
    public Task<bool> HasCredentialsAsync(Device device, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        return credentials.HasCredentialsAsync(device.Id, ct);
    }

    /// <summary>Why <paramref name="device"/> has its status (kept in server memory, <see cref="DeviceStatusNotes"/>); null without one.</summary>
    public string? StatusNote(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return notes?.Get(device.Id);
    }

    /// <summary>Removes the devices in one transaction; returns the ids that existed.</summary>
    public Task<IReadOnlyList<Guid>> RemoveAsync(ViewedDevices viewed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewed);
        return devices.RemoveManyAsync(viewed.Ids, ct);
    }

    /// <summary>Queues a full refresh of the devices; results arrive through the device feed.</summary>
    public void QueueRefresh(ViewedDevices viewed)
    {
        ArgumentNullException.ThrowIfNull(viewed);
        polling.QueueRefresh(viewed.Ids);
    }

    /// <summary>Stores one credential for every device (one transaction), drops their cached clients and queues their refresh.</summary>
    /// <exception cref="KeyNotFoundException">A device was removed meanwhile; nothing was stored.</exception>
    public async Task SetCredentialsAsync(ViewedDevices viewed, string userName, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewed);
        await credentials.SetManyAsync(viewed.Ids, userName, password, ct).ConfigureAwait(false);
        foreach (var id in viewed.Ids)
        {
            clients.Invalidate(id);
        }

        polling.QueueRefresh(viewed.Ids);
    }

    /// <inheritdoc cref="DeviceLoginService.LogInAsync"/>
    public Task<IReadOnlyList<DeviceLoginResult>> LogInAsync(ViewedDevices viewed, string userName, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewed);
        return logins.LogInAsync(viewed.Ids, userName, password, ct);
    }

    /// <inheritdoc cref="DeviceLoginService.SharedUserNameAsync"/>
    public Task<string?> SharedUserNameAsync(ViewedDevices viewed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewed);
        return logins.SharedUserNameAsync(viewed.Ids, ct);
    }

    /// <inheritdoc cref="DeviceFirstPasswordService.SetAsync"/>
    public Task<IReadOnlyList<DeviceFirstPasswordResult>> SetFirstPasswordAsync(ViewedDevices viewed, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewed);
        return firstPasswords.SetAsync(viewed.Ids, password, ct);
    }

    /// <inheritdoc cref="DeviceFirstPasswordService.ReadPoliciesAsync(ViewedDevices, CancellationToken)"/>
    public Task<IReadOnlyList<DevicePassphrasePolicy>> ReadPoliciesAsync(ViewedDevices viewed, CancellationToken ct) =>
        firstPasswords.ReadPoliciesAsync(viewed, ct);

    /// <summary>The logged-in VAPIX client of one viewed device (the web interface relay).</summary>
    public Task<VapixClient> GetClientAsync(ViewedDevices one, CancellationToken ct) => clients.GetClientAsync(Single(one), ct);

    /// <summary>The one device of <paramref name="one"/> (the calls that name a single device).</summary>
    internal static Device Single(ViewedDevices one)
    {
        ArgumentNullException.ThrowIfNull(one);
        return one.Count == 1 ? one[0] : throw new ArgumentException("Exactly one device is needed.", nameof(one));
    }
}
