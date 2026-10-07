using System.Net;

using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Network;

/// <summary>
/// Read-only query "checkAddresses" of both network plugins: lists the addresses of all managed devices and probes
/// candidate IPv4 and IPv6 addresses from the server (where devices are reached) with <see cref="IAddressProbe"/>:
/// an ICMP echo and a plain TCP connect to port 80 and 443, closed right away. Nothing else is sent.
/// </summary>
public static class AddressCheck
{
    public const string QueryMethod = "checkAddresses";

    private const int Parallelism = 32;

    public static async Task<string> QueryAsync(ITaskQueryContext ctx, string? payloadJson, IAddressProbe probe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(probe);
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
            await Parallel.ForEachAsync(
                Enumerable.Range(0, addresses.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
                async (i, token) =>
                {
                    var result = await probe.ProbeAsync(addresses[i], token).ConfigureAwait(false);
                    probed[i] = new AddressStatus(addresses[i], InUse: result.InUse, AnswersPing: result.AnswersPing);
                })
                .ConfigureAwait(false);
        }
        else
        {
            for (var i = 0; i < addresses.Count; i++)
            {
                probed[i] = new AddressStatus(addresses[i]);
            }
        }

        return new AddressCheckResponse(managed, probed).ToJson();
    }

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
