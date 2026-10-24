namespace Oadm.Sdk.Devices;

/// <summary>
/// Tag names a core plugin protects (SDK 0.4.0, <see cref="Plugins.ICorePluginContext.ProtectedTags"/>): only users with
/// all devices may add them to or remove them from devices, like the tags a device group uses (docs/spec/tags.md). For a
/// plugin whose settings act on devices by tag, e.g. the VLAN rules of the 802.1X server: a user limited to a device group
/// tags the devices of the group but never gives one of them a protected tag. Rename and delete of a tag need all devices
/// anyway.
/// </summary>
public interface IProtectedTags
{
    /// <summary>
    /// Sets this plugin's protected names (case-insensitive; replaces the earlier set, empty protects nothing) and the
    /// refusal a user limited to a device group gets for one of them (<paramref name="refusal"/> gets the tag's name; one
    /// plain sentence plus "Nothing was changed."). The host checks them inside the tag write, so keep
    /// <paramref name="refusal"/> cheap. The host forgets the set when the plugin stops.
    /// </summary>
    void Protect(IReadOnlyCollection<string> names, Func<string, string> refusal);
}
