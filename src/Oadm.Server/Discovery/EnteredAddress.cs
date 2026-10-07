using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Oadm.Server.Discovery;

/// <summary>
/// An address typed into "Add manually": IP address or host name, optional port, optional scheme
/// ("10.0.0.48", "cam1.example.com", "10.0.0.48:8443", "https://cam1:8443", "[fe80::1]").
/// </summary>
/// <param name="Host">Host name or IP address (IPv6 without brackets).</param>
/// <param name="Port">Explicit port, null for the scheme default.</param>
/// <param name="Scheme">"http" or "https" when given, null to try HTTPS then HTTP.</param>
public sealed record EnteredAddress(string Host, int? Port, string? Scheme)
{
    /// <summary>The device address OADM stores: host (IPv6 in brackets when a port follows) plus ":port" when given.</summary>
    public string DeviceAddress
    {
        get
        {
            var ipv6 = IPAddress.TryParse(Host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
            if (Port is null)
            {
                return Host;
            }

            return (ipv6 ? "[" + Host + "]" : Host) + ":" + Port.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Parses user input. Returns false with a user facing <paramref name="error"/>.</summary>
    public static bool TryParse(string? input, out EnteredAddress? address, out string? error)
    {
        address = null;
        error = null;
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "Enter an IP address or host name.";
            return false;
        }

        if (IPAddress.TryParse(text.Trim('[', ']'), out var bare) && !text.Contains("://", StringComparison.Ordinal)
            && (bare.AddressFamily == AddressFamily.InterNetworkV6 || !text.Contains(':', StringComparison.Ordinal)))
        {
            address = new EnteredAddress(bare.ToString(), null, null);
            return true;
        }

        string? scheme = null;
        var withScheme = text;
        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator >= 0)
        {
            scheme = text[..separator].ToLowerInvariant();
            if (scheme != Uri.UriSchemeHttp && scheme != Uri.UriSchemeHttps)
            {
                error = "Only http:// and https:// addresses are supported.";
                return false;
            }
        }
        else
        {
            withScheme = "http://" + text;
        }

        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.PathAndQuery.Length > 1)
        {
            error = $"'{text}' is not a valid IP address or host name.";
            return false;
        }

        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.IdnHost;
        var explicitPort = HasExplicitPort(text) ? uri.Port : (int?)null;
        address = new EnteredAddress(host, explicitPort, scheme);
        return true;
    }

    /// <summary>True when the text names a port after the host ("host:8080", "[::1]:8443", "https://h:443").</summary>
    private static bool HasExplicitPort(string text)
    {
        var rest = text;
        var separator = rest.IndexOf("://", StringComparison.Ordinal);
        if (separator >= 0)
        {
            rest = rest[(separator + 3)..];
        }

        rest = rest.TrimEnd('/');
        var close = rest.LastIndexOf(']');
        var colon = rest.LastIndexOf(':');
        return colon > close && colon < rest.Length - 1;
    }
}
