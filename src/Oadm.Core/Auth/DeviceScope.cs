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

    /// <summary>The key of a stored scope without devices (an object whose group was deleted): matches no user's key.</summary>
    public const string NoDevicesKey = "none";

    /// <summary>
    /// The scope as <c>ICallContext.DeviceScopeKey</c> names it: "all", the group's id, or <see cref="NoDevicesKey"/> (no
    /// device). A user without devices never has that key: <c>DeviceView.ScopeKey</c> gives each such user a key of their
    /// own (<see cref="NoDevicesKeyOf"/>), so two users who lost their groups never share state kept per scope.
    /// </summary>
    public string ScopeKey => AllDevices ? Oadm.Sdk.Plugins.ICallContext.AllDevicesScopeKey : GroupId?.ToString() ?? NoDevicesKey;

    /// <summary>
    /// The device scope key of a user without devices: "none:" and the user's id, never equal to another user's key, to a
    /// group's or to <see cref="NoDevicesKey"/> (docs/spec/device-scope.md, Code (foundation)).
    /// </summary>
    public static string NoDevicesKeyOf(Guid userId) => NoDevicesKey + ":" + userId.ToString("N");

    /// <summary>Neither all devices nor a group: no device at all.</summary>
    public bool IsNone => !AllDevices && GroupId is null;
}
