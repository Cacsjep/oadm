using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Mapping;

/// <summary>The owner of a shared object (event rule, compliance check) on the wire: <c>DeviceOwner</c> of device_groups.proto.</summary>
internal static class OwnerMapping
{
    /// <summary>The owner with the group's name (empty for all devices and for a group that is gone).</summary>
    public static Proto.DeviceOwner ToProto(DeviceScope owner, OwnerScopes? owners) => new()
    {
        AllDevices = owner.AllDevices,
        GroupId = owner.EffectiveGroupId?.ToString() ?? string.Empty,
        GroupName = owners?.GroupName(owner.EffectiveGroupId) ?? string.Empty,
    };

    /// <summary>
    /// The owner a save asks for: null when unset (a new object belongs to the caller's devices, a changed one keeps its
    /// owner). INVALID_ARGUMENT for neither all devices nor a group id.
    /// </summary>
    public static DeviceScope? FromProto(Proto.DeviceOwner? owner)
    {
        if (owner is null)
        {
            return null;
        }

        if (owner.AllDevices)
        {
            return DeviceScope.All;
        }

        return Guid.TryParse(owner.GroupId, out var group) && group != Guid.Empty
            ? DeviceScope.Group(group)
            : throw GrpcGuard.InvalidArgument(DeviceGroupTexts.ChooseDevices);
    }
}
