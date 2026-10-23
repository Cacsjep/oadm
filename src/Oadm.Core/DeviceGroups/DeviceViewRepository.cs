using Oadm.Sdk.Devices;

namespace Oadm.Core.DeviceGroups;

/// <summary>
/// The devices of one <see cref="DeviceView"/> as an SDK <see cref="IDeviceRepository"/>: what plugin code that reads
/// devices during a user's call gets (compliance and action choices), so it never lists devices outside the user's view.
/// </summary>
public sealed class DeviceViewRepository(DeviceView view) : IDeviceRepository
{
    public async Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) =>
        [.. await view.ListAsync(ct).ConfigureAwait(false)];

    public async Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) =>
        await view.FindAsync(id, ct).ConfigureAwait(false);

    /// <summary>The repository for a caller: the global one for users with all devices (as before), else the view's.</summary>
    public static IDeviceRepository For(DeviceView view, IDeviceRepository all)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.HasAllDevices ? all : new DeviceViewRepository(view);
    }
}
