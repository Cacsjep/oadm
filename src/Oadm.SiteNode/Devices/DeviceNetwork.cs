using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Oadm.SiteNode.Devices;

/// <summary>Opens a TCP connection to a device (tests map addresses to loopback ports).</summary>
public interface IDeviceConnector
{
    /// <summary>A connected socket; a <see cref="SocketException"/> with the device's error otherwise.</summary>
    Task<Socket> ConnectAsync(IPEndPoint target, CancellationToken ct);
}

public sealed class SystemDeviceConnector : IDeviceConnector
{
    public static readonly SystemDeviceConnector Instance = new();

    public async Task<Socket> ConnectAsync(IPEndPoint target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(target, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>DNS on the node's side.</summary>
public interface INodeDns
{
    /// <summary>The addresses of a name; empty when the node's DNS does not know it (never throws but for cancellation).</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken ct);

    /// <summary>The name of an address (reverse lookup), unverified; null when none.</summary>
    Task<string?> ReverseAsync(IPAddress address, CancellationToken ct);
}

public sealed class SystemNodeDns : INodeDns
{
    public static readonly SystemNodeDns Instance = new();

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken ct)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(name, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            return [];
        }
    }

    public async Task<string?> ReverseAsync(IPAddress address, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        try
        {
            return (await Dns.GetHostEntryAsync(address.ToString(), ct).ConfigureAwait(false)).HostName;
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Ping through the OS, never a raw socket (Windows IcmpSendEcho; Linux an ICMP datagram socket or the ping program).</summary>
public interface IPinger
{
    /// <summary>The round trip, or null without an answer; throws <see cref="NotSupportedException"/> when the OS cannot ping for this user.</summary>
    Task<TimeSpan?> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken ct);
}

public sealed class SystemPinger : IPinger
{
    public static readonly SystemPinger Instance = new();

    public async Task<TimeSpan?> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(address, timeout, cancellationToken: ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? TimeSpan.FromMilliseconds(reply.RoundtripTime) : null;
        }
        catch (Exception e) when (e is PingException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            throw new NotSupportedException("Ping is not available.", e);
        }
    }
}

/// <summary>DNS names with the rules of the server's SystemDnsNameLookup ("Use DNS names instead of IP addresses").</summary>
public static class DnsNameRules
{
    /// <summary>A full DNS name ("axis-b8a44f631339.site.example"), lower case without the trailing dot; null for an IP, a single label, an mDNS or a reverse zone name.</summary>
    public static string? Normalize(string? name)
    {
        var host = name?.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(host) || IPAddress.TryParse(host, out _) || !host.Contains('.', StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".arpa", StringComparison.Ordinal)
            || Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            return null;
        }

        return host;
    }

    /// <summary>The verified name of an address: a full name that resolves back to the same address, or null. Bounded by <paramref name="timeout"/>.</summary>
    public static async Task<string?> ReverseLookupAsync(INodeDns dns, IPAddress address, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dns);
        ArgumentNullException.ThrowIfNull(address);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            if (Normalize(await dns.ReverseAsync(address, limit.Token).ConfigureAwait(false)) is not { } name)
                return null;
            var back = await dns.ResolveAsync(name, limit.Token).ConfigureAwait(false);
            return back.Any(a => a.Equals(address)) ? name : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
