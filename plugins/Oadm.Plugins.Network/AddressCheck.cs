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

    private const int Parallelism = 32;

    /// <param name="probe">The probe; null = on the route of the query's device (<see cref="ITaskQueryContext.Network"/>).</param>
    public static async Task<string> QueryAsync(ITaskQueryContext ctx, string? payloadJson, IAddressProbe? probe, CancellationToken ct)
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
            foreach (var device in await devices.ListAsync(ct).ConfigureAwait(false))
            {
                var label = string.IsNullOrEmpty(device.Model) ? device.Serial : $"{device.Model} {device.Serial}";
                managed.Add(new AddressStatus(device.Address, device.Id, label));
            }
        }

        var addresses = request.Addresses
            .Select(a => a?.Trim())
            .Where(IsIpLiteral)
            .Select(a => a!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var probed = new AddressStatus[addresses.Count];
        if (request.Probe)
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

        return new AddressCheckResponse(managed, probed).ToJson();
    }

    /// <summary>True when <paramref name="address"/> is outside the networks <paramref name="network"/> reaches (a site).</summary>
    public static bool IsOutside(Sdk.Sites.IDeviceNetwork? network, string address) =>
        network is not null && IPAddress.TryParse(address.Trim().Trim('[', ']'), out var ip) && !network.Reaches(ip);

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
