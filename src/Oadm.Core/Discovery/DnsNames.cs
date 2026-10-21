using System.Net;
using System.Net.Sockets;

using Oadm.Core.Sites;

namespace Oadm.Core.Discovery;

/// <summary>The DNS name (FQDN) of a device's IP address, for "Use DNS names instead of IP addresses" on the add page.</summary>
public interface IDnsNameLookup
{
    /// <summary>The verified FQDN of <paramref name="address"/>, or null when DNS has none (never throws).</summary>
    Task<string?> LookupAsync(IPAddress address, CancellationToken ct);

    /// <summary>
    /// The verified FQDN of <paramref name="address"/> on <paramref name="route"/>: the DNS of the server's own network or
    /// of the site. Null when DNS has none (never throws). Default: the server's own network only, nothing through a site.
    /// </summary>
    Task<string?> LookupAsync(IPAddress address, DeviceRoute route, CancellationToken ct) =>
        route.IsLocal ? LookupAsync(address, ct) : Task.FromResult<string?>(null);
}

/// <summary>
/// The server's DNS: the name of the IP (reverse lookup), accepted only when it is a full name (with a domain, not an mDNS
/// ".local" name) and resolves back to the same IP, so the device is reached at the same address by its name. Lower case,
/// without a trailing dot. Each lookup is bounded by <see cref="Timeout"/>. Both lookups go to the DNS of the device's route
/// (<see cref="RoutedDns"/>).
/// </summary>
public sealed class SystemDnsNameLookup(RoutedDns? dns = null) : IDnsNameLookup
{
    public static readonly SystemDnsNameLookup Instance = new();

    private readonly RoutedDns _dns = dns ?? RoutedDns.Local;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    public Task<string?> LookupAsync(IPAddress address, CancellationToken ct) => LookupAsync(address, DeviceRoute.Local, ct);

    public async Task<string?> LookupAsync(IPAddress address, DeviceRoute route, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            if (Normalize(await _dns.ReverseLookupAsync(route, address, timeout.Token).ConfigureAwait(false)) is not { } name)
            {
                return null;
            }

            var back = await _dns.ResolveAsync(route, name, timeout.Token).ConfigureAwait(false);
            return back.Any(a => a.Equals(address)) ? name : null;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or SiteException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>A full DNS name ("axis-b8a44f631339.site.example"), or null for an IP, a single label, an mDNS or a reverse zone name.</summary>
    public static string? Normalize(string? name)
    {
        var host = name?.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(host) || IPAddress.TryParse(host, out _) || !host.Contains('.', StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".arpa", StringComparison.Ordinal) || Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            return null;
        }

        return host;
    }
}
