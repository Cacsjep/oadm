namespace Oadm.Core.Auth;

/// <summary>
/// The devices of a user or of a shared object (docs/spec/device-scope.md, Model): an explicit choice, never "null = all
/// devices". <see cref="AllDevices"/>: every device. Otherwise the devices of <see cref="GroupId"/>; without a group (its
/// group was deleted) no device at all. The Administrator role always has all devices, whatever is stored.
/// </summary>
/// <param name="AllDevices">Every device.</param>
/// <param name="GroupId">The device group; ignored when <paramref name="AllDevices"/> is set.</param>
public readonly record struct DeviceScope(bool AllDevices, Guid? GroupId)
{
    /// <summary>Every device.</summary>
    public static DeviceScope All => new(true, null);

    /// <summary>No device (a user or an object whose group was deleted).</summary>
    public static DeviceScope None => new(false, null);

    /// <summary>The devices of one group.</summary>
    public static DeviceScope Group(Guid groupId) => new(false, groupId);

    /// <summary>The group when the scope is a group, else null.</summary>
    public Guid? EffectiveGroupId => AllDevices ? null : GroupId;

    /// <summary>
    /// The scope as <c>ICallContext.DeviceScopeKey</c> names it: "all", the group's id, or "none" (no device: matches no
    /// user's key, like <c>DeviceView.ScopeKey</c>).
    /// </summary>
    public string ScopeKey => AllDevices ? Oadm.Sdk.Plugins.ICallContext.AllDevicesScopeKey : GroupId?.ToString() ?? "none";

    /// <summary>Neither all devices nor a group: no device at all.</summary>
    public bool IsNone => !AllDevices && GroupId is null;
}
