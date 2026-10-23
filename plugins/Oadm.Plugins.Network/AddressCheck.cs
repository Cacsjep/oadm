using System.Net;

using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Network;

/// <summary>
/// Read-only query "checkAddresses" of both network plugins: lists the addresses of all managed devices and probes
/// candidate IPv4 and IPv6 addresses from the server (where devices are reached) with <see cref="IAddressProbe"/>:
/// an ICMP echo and a plain TCP connect to port 80 and 443, closed right away. Nothing else is sent. The probes run on the
/// route of the query's device (its site's node, or the server's own network); an address outside the networks a site
/// reaches is not probed and comes back <see cref="AddressStatus.OutsideSite"/>.
/// </summary>
public static class AddressCheck
{
    public const string QueryMethod = "checkAddresses";

    /// <summary>The task's refusal of an address another managed device has (never names that device).</summary>
    public const string UsedByOtherDevice = "Another device managed by OADM uses this address.";

    private const int Parallelism = 32;

    /// <param name="probe">The probe; null = on the route of the query's device (<see cref="ITaskQueryContext.Network"/>).</param>
    public static Task<string> QueryAsync(ITaskQueryContext ctx, string? payloadJson, IAddressProbe? probe, CancellationToken ct) =>
        QueryAsync(ctx, null, payloadJson, probe, ct);

    /// <summary>
    /// The query for <paramref name="device"/>'s dialog: the managed devices listed are those on its route only (docs/spec/sites.md,
    /// "Overlapping networks": 192.168.0.20 at another site is another network and no conflict). Null = every device.
    /// </summary>
    /// <param name="probe">The probe; null = on the route of the query's device (<see cref="ITaskQueryContext.Network"/>).</param>
    public static Task<string> QueryAsync(ITaskQueryContext ctx, Sdk.Devices.IDeviceInfo? device, string? payloadJson, IAddressProbe? probe, CancellationToken ct) =>
        QueryAsync(ctx, device, payloadJson, probe, outsideView: false, mayProbe: true, ct);

    /// <summary>
    /// The query of a user's dialog. <paramref name="outsideView"/> (a user with a device group, whose
    /// <see cref="ITaskQueryContext.Devices"/> are the group's): a candidate that a device outside the group has comes back
    /// in use (<see cref="AddressStatus.UsedByDevice"/>), without naming that device (docs/spec/device-scope.md).
    /// <paramref name="mayProbe"/> false (a group without the route of the device, <see cref="ICallContext.CoversRoute"/>):
    /// nothing is probed, the answer comes from the managed devices only, as for a request without probes.
    /// </summary>
    public static async Task<string> QueryAsync(ITaskQueryContext ctx, Sdk.Devices.IDeviceInfo? device, string? payloadJson, IAddressProbe? probe, bool outsideView, bool mayProbe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var request = AddressCheckRequest.Parse(payloadJson);
        if (request.Addresses.Count > AddressCheckRequest.MaxProbes)
        {
            throw new ArgumentException($"At most {AddressCheckRequest.MaxProbes} addresses can be checked at once.");
        }

        var managed = new List<AddressStatus>();
        if (ctx.Devices is { } devices)
        {
            foreach (var other in await devices.ListAsync(ct).ConfigureAwait(false))
            {
                if (device is not null && other.SiteId != device.SiteId)
                {
                    continue; // another route: the same address is another network
                }

                var label = string.IsNullOrEmpty(other.Model) ? other.Serial : $"{other.Model} {other.Serial}";
                managed.Add(new AddressStatus(other.Address, other.Id, label));
            }
        }

        var addresses = request.Addresses
            .Select(a => a?.Trim())
            .Where(IsIpLiteral)
            .Select(a => a!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var probed = new AddressStatus[addresses.Count];
        if (request.Probe && mayProbe)
        {
            var used = probe ?? NetworkAddressProbe.For(ctx.Network);
            await Parallel.ForEachAsync(
                Enumerable.Range(0, addresses.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
                async (i, token) =>
                {
                    if (IsOutside(ctx.Network, addresses[i]))
                    {
                        probed[i] = new AddressStatus(addresses[i], OutsideSite: true);
                        return;
                    }

                    var result = await used.ProbeAsync(addresses[i], token).ConfigureAwait(false);
                    probed[i] = new AddressStatus(addresses[i], InUse: result.InUse, AnswersPing: result.AnswersPing);
                })
                .ConfigureAwait(false);
        }
        else
        {
            for (var i = 0; i < addresses.Count; i++)
            {
                probed[i] = new AddressStatus(addresses[i], OutsideSite: IsOutside(ctx.Network, addresses[i]));
            }
        }

        if (outsideView && addresses.Count > 0)
        {
            var named = managed.Select(m => ParseAddress(m.Address)).OfType<IPAddress>().ToHashSet();
            var used = await ctx.ManagedAddressesAsync(addresses, ct).ConfigureAwait(false);
            for (var i = 0; i < addresses.Count; i++)
            {
                if (used.Contains(addresses[i]) && ParseAddress(addresses[i]) is { } ip && !named.Contains(ip))
                {
                    probed[i] = probed[i] with { InUse = true, UsedByDevice = true };
                }
            }
        }

        return new AddressCheckResponse(managed, probed).ToJson();
    }

    /// <summary>True when <paramref name="address"/> is outside the networks <paramref name="network"/> reaches (a site).</summary>
    public static bool IsOutside(Sdk.Sites.IDeviceNetwork? network, string address) =>
        network is not null && IPAddress.TryParse(address.Trim().Trim('[', ']'), out var ip) && !network.Reaches(ip);

    private static IPAddress? ParseAddress(string? text) =>
        IPAddress.TryParse(text?.Trim().Trim('[', ']'), out var ip) ? ip : null;

    /// <summary>An IPv4 address (dotted quad) or an IPv6 address without zone or prefix.</summary>
    public static bool IsIpLiteral(string? text) => Ipv4.TryParse(text, out _) || PayloadValidator.TryParseIpv6(text, out _);

    /// <summary>
    /// True when <paramref name="candidate"/> is one of <paramref name="own"/> (the device's own current addresses):
    /// an answer there is the device itself, not a conflict.
    /// </summary>
    public static bool IsOwnAddress(string candidate, IEnumerable<string?> own)
    {
        ArgumentNullException.ThrowIfNull(own);
        if (!IPAddress.TryParse(candidate.Trim().Trim('[', ']'), out var ip))
        {
            return false;
        }

        foreach (var text in own)
        {
            var value = text?.Split('/')[0].Trim().Trim('[', ']');
            if (value is not null && IPAddress.TryParse(value, out var other) && other.Equals(ip))
            {
                return true;
            }
        }

        return false;
    }
}
