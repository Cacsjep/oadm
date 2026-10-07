using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Oadm.Plugins.NtpServer.Upstream;

/// <summary>A configured upstream: host name or address with an optional port ("pool.ntp.org", "10.0.0.1:1123", "[fd00::1]:123").</summary>
public sealed record UpstreamHost(string Host, int Port)
{
    public const int DefaultPort = 123;

    /// <summary>Parses the field text; null with an error text for invalid input.</summary>
    public static UpstreamHost? TryParse(string? text, out string? error)
    {
        error = null;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = "Enter a host name or IP address.";
            return null;
        }

        if (value.Length > 253 || value.Any(char.IsWhiteSpace))
        {
            error = "Enter one host name or IP address.";
            return null;
        }

        var host = value;
        var port = DefaultPort;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                error = "The IPv6 address is missing \"]\".";
                return null;
            }

            host = value[1..close];
            var rest = value[(close + 1)..];
            if (rest.Length > 0 && (!rest.StartsWith(':') || !TryPort(rest[1..], out port)))
            {
                error = "The port must be a number from 1 to 65535.";
                return null;
            }

            if (!IPAddress.TryParse(host, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = "Enter a valid IPv6 address.";
                return null;
            }
        }
        else if (value.Count(c => c == ':') == 1)
        {
            var colon = value.IndexOf(':', StringComparison.Ordinal);
            host = value[..colon];
            if (!TryPort(value[(colon + 1)..], out port))
            {
                error = "The port must be a number from 1 to 65535.";
                return null;
            }
        }
        else if (value.Contains(':', StringComparison.Ordinal) && !IPAddress.TryParse(value, out _))
        {
            error = "Enter a valid IPv6 address.";
            return null;
        }

        if (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            error = "Enter a valid host name or IP address.";
            return null;
        }

        return new UpstreamHost(host, port);
    }

    public override string ToString() =>
        Port == DefaultPort ? Host
        : Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port.ToString(CultureInfo.InvariantCulture)}"
        : $"{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
}

/// <summary>Resolves host names (DNS). Injectable for tests (failures, hangs).</summary>
public interface IUpstreamResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class DnsUpstreamResolver : IUpstreamResolver
{
    public static DnsUpstreamResolver Instance { get; } = new();

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>
/// Resolution with a hard timeout and a TTL cache: a cached address is reused for <see cref="UpstreamOptions.DnsTtl"/>,
/// and kept (stale) when a later resolution fails, so a DNS outage does not stop a working upstream.
/// </summary>
public sealed class CachingUpstreamResolver(IUpstreamResolver inner, UpstreamOptions options, TimeProvider time)
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, (IPEndPoint Endpoint, long At)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IPEndPoint> ResolveAsync(UpstreamHost host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (IPAddress.TryParse(host.Host, out var literal))
        {
            return new IPEndPoint(literal, host.Port);
        }

        (IPEndPoint Endpoint, long At) cached;
        bool hasCached;
        lock (_sync)
        {
            hasCached = _cache.TryGetValue(host.ToString(), out cached);
        }

        if (hasCached && time.GetElapsedTime(cached.At) < options.DnsTtl)
        {
            return cached.Endpoint;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.DnsTimeout);
        try
        {
            // WaitAsync: a resolver that ignores the token (some OS resolvers do) still cannot hold the caller.
            var addresses = await inner.ResolveAsync(host.Host, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                ?? throw new UpstreamException(UpstreamFailure.DnsFailure, $"{host.Host} has no IP address");
            var endpoint = new IPEndPoint(address, host.Port);
            lock (_sync)
            {
                _cache[host.ToString()] = (endpoint, time.GetTimestamp());
            }

            return endpoint;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException && !ct.IsCancellationRequested)
        {
            if (hasCached)
            {
                return cached.Endpoint;
            }

            throw new UpstreamException(UpstreamFailure.DnsFailure,
                ex is OperationCanceledException
                    ? $"{host.Host} could not be resolved within {UpstreamClient.Seconds(options.DnsTimeout)}"
                    : $"{host.Host} could not be resolved", inner: ex);
        }
    }
}
