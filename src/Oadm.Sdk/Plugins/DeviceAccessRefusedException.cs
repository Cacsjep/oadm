namespace Oadm.Sdk.Plugins;

/// <summary>
/// The host refused device access during the call of a user with a device group (SDK 0.4.0, OADM 4.5): the plugin used
/// <c>ctx.Devices</c>, <c>ctx.Vapix</c>, <c>ctx.Tasks</c>, <c>ctx.EventStreams</c> or <c>ctx.System</c> instead of the
/// call (<see cref="ICallContext"/>), the method is one only users with all devices may call
/// (<see cref="ICorePlugin.RequiresAllDevices"/>), or the plugin was built for an SDK before the call context. The host
/// answers PERMISSION_DENIED with <see cref="Exception.Message"/>; nothing reached a device.
/// </summary>
public sealed class DeviceAccessRefusedException : Exception
{
    /// <summary>The refusal of a method or a device access only users with all devices may use.</summary>
    public const string AllDevicesOnly = "Only users with all devices can do this.";

    /// <summary>The refusal of a plugin built for an SDK before 0.4.0, for a user with a device group.</summary>
    public const string PluginNeedsAllDevices = "Only users with all devices can use this plugin.";

    /// <summary>With <see cref="AllDevicesOnly"/>.</summary>
    public DeviceAccessRefusedException()
        : base(AllDevicesOnly)
    {
    }

    /// <summary>With an own message for the user.</summary>
    public DeviceAccessRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>With an own message and the cause.</summary>
    public DeviceAccessRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
