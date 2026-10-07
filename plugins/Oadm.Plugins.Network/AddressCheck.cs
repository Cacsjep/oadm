using System.Net;
using System.Net.Sockets;

using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Network;

/// <summary>
/// Read-only query "checkAddresses" of both network plugins: lists the addresses of all managed devices and probes
/// candidate addresses from the server (where devices are reached) with a plain TCP connect to port 80 and 443,
/// closed right away. No ICMP, no ARP, no HTTP request: nothing is sent to whatever answers.
/// </summary>
public static class AddressCheck
{
    public const string QueryMethod = "checkAddresses";

    /// <summary>Connect timeout per port. A free address times out; a host answers within milliseconds on a LAN.</summary>
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromMilliseconds(800);

    private const int Parallelism = 32;

    private static readonly int[] Ports = [80, 443];

    public static async Task<string> QueryAsync(ITaskQueryContext ctx, string? payloadJson, CancellationToken ct)
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
            .Where(a => Ipv4.TryParse(a, out _))
            .Select(a => a!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var probed = new AddressStatus[addresses.Count];
        if (request.Probe)
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, addresses.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
                async (i, token) => probed[i] = new AddressStatus(addresses[i], InUse: await AnswersAsync(addresses[i], token).ConfigureAwait(false)))
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

    /// <summary>True when a TCP connection to port 80 or 443 is accepted (or actively refused: a host is there).</summary>
    public static Task<bool> AnswersAsync(string address, CancellationToken ct) => AnswersAsync(address, Ports, ct);

    /// <summary>True when a TCP connection to one of <paramref name="ports"/> is accepted or actively refused.</summary>
    public static async Task<bool> AnswersAsync(string address, IReadOnlyList<int> ports, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ports);
        var ip = IPAddress.Parse(address);
        var attempts = ports.Select(port => ConnectAsync(ip, port, ct)).ToList();
        var results = await Task.WhenAll(attempts).ConfigureAwait(false);
        return results.Any(r => r);
    }

    private static async Task<bool> ConnectAsync(IPAddress ip, int port, CancellationToken ct)
    {
        using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return true; // a host sent a reset: the address is taken
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
