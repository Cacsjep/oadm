namespace Oadm.Core.Devices;

/// <summary>What device groups read of a device: its id, serial, site, tags and who added it.</summary>
/// <param name="Id">The device id.</param>
/// <param name="Serial">The normalized serial.</param>
/// <param name="SiteId">The site; null = the server's own network ("This server").</param>
/// <param name="Tags">The tag names.</param>
/// <param name="AddedByAllDevices">A user with all devices (or the system) added it.</param>
/// <param name="AddedByGroupId">The group of the user who added it, else null.</param>
public sealed record DeviceScopeRow(Guid Id, string Serial, Guid? SiteId, IReadOnlyList<string> Tags, bool AddedByAllDevices, Guid? AddedByGroupId)
{
    /// <summary>The row of <paramref name="device"/>.</summary>
    public static DeviceScopeRow Of(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new(device.Id, device.Serial, device.SiteId, device.Tags, device.AddedByAllDevices, device.AddedByGroupId);
    }
}
