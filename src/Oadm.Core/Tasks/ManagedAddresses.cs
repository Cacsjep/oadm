using System.Net;

using Oadm.Sdk.Devices;

namespace Oadm.Core.Tasks;

/// <summary>
/// The address-in-use check of <c>ITaskQueryContext.ManagedAddressesAsync</c> and <c>ITaskExecutionContext.ManagedAddressesAsync</c>:
/// which of the given addresses a managed device on one route uses. Answers only "in use", never which device.
/// </summary>
internal static class ManagedAddresses
{
    /// <summary>At most this many addresses per call (the dialog's address check sends at most 256 too).</summary>
    public const int MaxAddresses = 256;

    /// <summary>
    /// <see cref="Match"/> over the devices of <paramref name="devices"/>: the global <see cref="Devices.DeviceRepository"/>
    /// reads only id and address of the route's devices (one light query, never the rows); nothing is read when no address
    /// is an IP address. Devices stored by host name are not compared (docs/spec/device-scope.md, Address takeover accepted).
    /// </summary>
    public static async Task<IReadOnlySet<string>> MatchAsync(IDeviceRepository devices, Guid? siteId, Guid? except, IReadOnlyCollection<string> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ThrowIfTooMany(addresses);
        if (!addresses.Any(a => Parse(a) is not null))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        if (devices is Devices.DeviceRepository repository)
        {
            var route = await repository.ListAddressesOnRouteAsync(siteId, ct).ConfigureAwait(false);
            return Match(route.Select(r => (r.Id, (Guid?)siteId, (string?)r.Address)), siteId, except, addresses);
        }

        var all = await devices.ListAsync(ct).ConfigureAwait(false);
        return Match(all, siteId, except, addresses);
    }

    /// <param name="devices">Every managed device.</param>
    /// <param name="siteId">The route: devices of this site (null = the server's own network).</param>
    /// <param name="except">A device whose own address does not count (the task's device); null = none.</param>
    /// <param name="addresses">The addresses asked about, compared as IP addresses; the answer holds them as given.</param>
    public static IReadOnlySet<string> Match(IEnumerable<IDeviceInfo> devices, Guid? siteId, Guid? except, IReadOnlyCollection<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return Match(devices.Select(d => (d.Id, d.SiteId, (string?)d.Address)), siteId, except, addresses);
    }

    private static HashSet<string> Match(IEnumerable<(Guid Id, Guid? SiteId, string? Address)> devices, Guid? siteId, Guid? except, IReadOnlyCollection<string> addresses)
    {
        ThrowIfTooMany(addresses);
        var wanted = new Dictionary<IPAddress, List<string>>();
        foreach (var address in addresses)
        {
            if (Parse(address) is { } ip)
            {
                if (!wanted.TryGetValue(ip, out var texts))
                {
                    wanted[ip] = texts = [];
                }

                texts.Add(address);
            }
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return used;
        }

        foreach (var other in devices)
        {
            if (other.SiteId == siteId && other.Id != except && Parse(other.Address) is { } ip && wanted.TryGetValue(ip, out var texts))
            {
                used.UnionWith(texts);
            }
        }

        return used;
    }

    /// <summary>Refuses more than <see cref="MaxAddresses"/> addresses (before the device list is read).</summary>
    public static void ThrowIfTooMany(IReadOnlyCollection<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count > MaxAddresses)
        {
            throw new ArgumentException($"At most {MaxAddresses} addresses can be checked at once.", nameof(addresses));
        }
    }

    private static IPAddress? Parse(string? text) =>
        IPAddress.TryParse(text?.Trim().Trim('[', ']'), out var ip) ? ip : null;
}
