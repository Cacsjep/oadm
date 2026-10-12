using System.Globalization;

namespace Oadm.Sdk.Client;

/// <summary>The order of device lists: IPv4 addresses numerically, host names and other addresses after them.</summary>
public static class DeviceOrder
{
    /// <summary>A sort key: "010.000.000.048" for 10.0.0.48 (a port is ignored), "~camera.example.com" otherwise.</summary>
    public static string AddressKey(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var parts = address.Split(':')[0].Split('.');
        return parts.Length == 4 && parts.All(p => byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            ? string.Join('.', parts.Select(p => p.PadLeft(3, '0')))
            : "~" + address;
    }
}
