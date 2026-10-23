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

    /// <param name="devices">Every managed device.</param>
    /// <param name="siteId">The route: devices of this site (null = the server's own network).</param>
    /// <param name="except">A device whose own address does not count (the task's device); null = none.</param>
    /// <param name="addresses">The addresses asked about, compared as IP addresses; the answer holds them as given.</param>
    public static IReadOnlySet<string> Match(IEnumerable<IDeviceInfo> devices, Guid? siteId, Guid? except, IReadOnlyCollection<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(devices);
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
