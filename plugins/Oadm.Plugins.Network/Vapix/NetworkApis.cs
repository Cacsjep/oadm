using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Network.Vapix;

/// <summary>
/// VAPIX APIs this plugin uses and the minimum version per method (see README decision table).
/// The developer documentation has no per-method version history; minimums are the earliest
/// apiVersion Axis uses in the official examples for each method or parameter.
/// </summary>
public static class NetworkApis
{
    public const string NetworkSettings = "network-settings";
    public const string ParamCgi = "param-cgi";

    public const string NetworkSettingsPath = "axis-cgi/network_settings.cgi";
    public const string ParamPath = "axis-cgi/param.cgi";

    /// <summary>getNetworkInfo, setIPv4AddressConfiguration (mode, static address, staticDefaultRouter), setResolverConfiguration, setHostnameConfiguration.</summary>
    public const string NetworkSettingsBase = "1.0";

    /// <summary>setIPv6AddressConfiguration (enabled).</summary>
    public const string SetIpv6 = "1.6";

    /// <summary>Legacy param.cgi (Network.*); also the only way to set the IPv6 address mode, static address and gateway.</summary>
    public const string ParamCgiBase = "1.0";

    /// <summary>The device offers something this plugin can configure (used by CanRun on the cached list).</summary>
    public static bool CanConfigure(IEnumerable<DeviceApi> apis)
    {
        var list = apis as IReadOnlyCollection<DeviceApi> ?? apis.ToList();
        return list.Supports(NetworkSettings, NetworkSettingsBase) || list.Supports(ParamCgi, ParamCgiBase);
    }

    public static bool UseJsonApi(IEnumerable<DeviceApi> apis) => apis.Supports(NetworkSettings, NetworkSettingsBase);
}
