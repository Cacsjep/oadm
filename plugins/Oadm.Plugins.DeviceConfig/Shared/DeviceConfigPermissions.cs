namespace Oadm.Plugins.DeviceConfig;

/// <summary>
/// The permission keys of Import configuration ("task:oadm.device-config.import:&lt;key&gt;"); the dialog hides the connection
/// settings area when the role lacks it.
/// </summary>
public static class DeviceConfigPermissions
{
    public const string Import = "import";
    public const string Connection = "connection";
}
