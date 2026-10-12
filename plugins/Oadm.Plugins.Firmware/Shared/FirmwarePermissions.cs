namespace Oadm.Plugins.Firmware;

/// <summary>The permission keys of Upgrade firmware ("task:oadm.firmware:&lt;key&gt;"); the dialog hides what the role lacks.</summary>
public static class FirmwarePermissions
{
    /// <summary>The task plugin's id (<c>FirmwareTaskPlugin.PluginId</c>), shared with the dialog.</summary>
    public const string TaskPluginId = "oadm.firmware";

    public const string Upgrade = "upgrade";
    public const string FactoryDefault = "factoryDefault";
    public const string Downgrade = "downgrade";
}
