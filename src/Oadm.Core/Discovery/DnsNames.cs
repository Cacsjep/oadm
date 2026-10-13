using System.Net;
using System.Net.Sockets;

namespace Oadm.Core.Discovery;

/// <summary>The DNS name (FQDN) of a device's IP address, for "Use DNS names instead of IP addresses" on the add page.</summary>
public interface IDnsNameLookup
{
    /// <summary>The verified FQDN of <paramref name="address"/>, or null when DNS has none (never throws).</summary>
    Task<string?> LookupAsync(IPAddress address, CancellationToken ct);
}

/// <summary>
/// The server's DNS: the name of the IP (reverse lookup), accepted only when it is a full name (with a domain, not an mDNS
/// ".local" name) and resolves back to the same IP, so the device is reached at the same address by its name. Lower case,
/// without a trailing dot. Each lookup is bounded by <see cref="Timeout"/>.
/// </summary>
public sealed class SystemDnsNameLookup : IDnsNameLookup
{
    public static readonly SystemDnsNameLookup Instance = new();

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    public async Task<string?> LookupAsync(IPAddress address, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token).ConfigureAwait(false);
            if (Normalize(entry.HostName) is not { } name)
            {
                return null;
            }

            var back = await Dns.GetHostAddressesAsync(name, timeout.Token).ConfigureAwait(false);
            return back.Any(a => a.Equals(address)) ? name : null;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException || ex is OperationCanceledException && !ct.IsCancellationRequested)
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
