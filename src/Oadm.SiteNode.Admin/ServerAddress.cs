using System.Net;

namespace Oadm.SiteNode.Admin;

/// <summary>The server address typed in the app or on the command line.</summary>
public static class ServerAddress
{
    /// <summary>"oadm.example.com", "10.0.0.5:5082", "[fd00::5]:5081", "https://host:5081/" -> host and port; null for an empty or invalid one.</summary>
    public static (string Host, int Port)? Parse(string? text, int defaultPort)
    {
        var value = text?.Trim() ?? "";
        if (value.Length == 0)
            return null;
        if (value.Contains("://", StringComparison.Ordinal))
            value = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..];
        value = value.TrimEnd('/');
        // IPAddress.TryParse accepts "[fd00::5]:5090" and drops the port: end points first.
        if ((value.StartsWith('[') || value.Count(c => c == ':') == 1) && IPEndPoint.TryParse(value, out var endpoint))
            return (endpoint.Address.ToString(), endpoint.Port == 0 ? defaultPort : endpoint.Port);
        if (IPAddress.TryParse(value, out var bare))
            return (bare.ToString(), defaultPort);
        var colon = value.LastIndexOf(':');
        var host = colon > 0 ? value[..colon] : value;
        var port = defaultPort;
        if (colon > 0 && (!int.TryParse(value[(colon + 1)..], out port) || port is <= 0 or > 65535))
            return null;
        return Uri.CheckHostName(host) == UriHostNameType.Dns ? (host, port) : null;
    }
}
