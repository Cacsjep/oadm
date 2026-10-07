using System.Net;

namespace Oadm.Core.Discovery.Mdns;

/// <summary>A resolved DNS-SD service instance found by <see cref="MdnsBrowser"/>.</summary>
/// <param name="InstanceName">Full instance name, e.g. "AXIS P3265-V - B8A44F123456._axis-video._tcp.local".</param>
/// <param name="HostName">SRV target without trailing ".local", e.g. "axis-b8a44f123456". Empty if unknown.</param>
/// <param name="Port">SRV port.</param>
/// <param name="Addresses">IPv4 addresses of the host (A records, or the responder address as fallback).</param>
/// <param name="Txt">TXT record key/value pairs, keys case-insensitive.</param>
/// <param name="Serial">MAC/serial as upper hex without separators, or null if it could not be determined.</param>
/// <param name="LocalAddress">Local interface address the response arrived on, null if unknown.</param>
public sealed record MdnsServiceInstance(
    string InstanceName,
    string HostName,
    ushort Port,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyDictionary<string, string> Txt,
    string? Serial,
    IPAddress? LocalAddress)
{
    /// <summary>The instance label without the service suffix, e.g. "AXIS P3265-V - B8A44F123456".</summary>
    public string DisplayName
    {
        get
        {
            var idx = InstanceName.IndexOf("._", StringComparison.Ordinal);
            return idx > 0 ? InstanceName[..idx] : InstanceName;
        }
    }
}
