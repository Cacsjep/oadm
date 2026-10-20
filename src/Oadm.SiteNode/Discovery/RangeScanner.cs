using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Oadm.Contracts.SiteNode;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Devices;
using Oadm.SiteNode.Logging;
using Oadm.SiteNode.Networks;
using Oadm.SiteNode.State;

namespace Oadm.SiteNode.Discovery;

/// <summary>
/// <c>RangeScan</c>: the first contact of a range scan on the node (docs/sites.md, "Discovery"), so thousands of short
/// connects stay at the site: every address of an IPv4 range on the reached networks with an open TCP 443 and / or 80
/// (only the ports on Connect to). Addresses outside the networks are counted as tried. Progress every 1 % of the range;
/// the last message has <c>finished</c>. One line in the connection log per scan. Every connect is a probe of the
/// <see cref="Dialer"/> (shared with ProbeTcp and the ping fallback), and only <see cref="DialLimits.Scans"/> scans run at once.
/// </summary>
public sealed class RangeScanner(PortPolicy ports, NetworkScope networks, Dialer dialer, ConnectionLog log)
{
    /// <summary>The largest range (a /16), as on the server.</summary>
    public const int MaxSize = 65536;
    public const int MaxParallelism = 256;

    private static readonly int[] ScanPorts = [443, 80];

    public static bool TryParse(string? from, string? to, out uint first, out uint last, out string error)
    {
        first = last = 0;
        if (!TryParseIpv4(from, out first) || !TryParseIpv4(to, out last))
        {
            error = "Enter two IPv4 addresses.";
            return false;
        }

        if (last < first)
        {
            error = "The end of the range is before its start.";
            return false;
        }

        if ((ulong)last - first + 1 > MaxSize)
        {
            error = $"A range has at most {MaxSize} addresses.";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>The scan; throws <see cref="NodeRefusalException"/> (RESOURCE_EXHAUSTED) while the most scans run.</summary>
    public async IAsyncEnumerable<RangeScanResult> ScanAsync(uint first, uint last, int parallelism, TimeSpan timeout, [EnumeratorCancellation] CancellationToken ct)
    {
        using var place = dialer.TryStartScan() ?? throw new NodeRefusalException(Grpc.Core.StatusCode.ResourceExhausted, AdminTexts.SiteBusy);
        var total = (int)(last - first + 1);
        int[] candidates = [.. ScanPorts.Where(p => ports.Allows(PortUse.Connect, PortProtocol.Tcp, p))];
        var results = Channel.CreateUnbounded<RangeScanResult>(new UnboundedChannelOptions { SingleReader = true });
        var tried = 0;
        var found = 0;
        var step = Math.Max(1, total / 100);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(async () =>
        {
            try
            {
                var addresses = Enumerable.Range(0, total).Select(i => Address(first + (uint)i));
                await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(parallelism <= 0 ? 32 : parallelism, 1, MaxParallelism), CancellationToken = stop.Token }, async (address, token) =>
                {
                    var open = new List<int>();
                    if (networks.Reaches(address))
                    {
                        foreach (var port in candidates)
                        {
                            if (await dialer.ProbeAsync(new IPEndPoint(address, port), timeout, token).ConfigureAwait(false) == ProbeResult.Open)
                                open.Add(port);
                        }
                    }

                    var done = Interlocked.Increment(ref tried);
                    if (open.Count > 0)
                    {
                        Interlocked.Increment(ref found);
                        var result = new RangeScanResult { Address = address.ToString(), Tried = done, Total = total };
                        result.OpenPorts.Add(open);
                        results.Writer.TryWrite(result);
                    }
                    else if (done % step == 0 && done < total)
                    {
                        results.Writer.TryWrite(new RangeScanResult { Tried = done, Total = total });
                    }
                }).ConfigureAwait(false);
                results.Writer.TryComplete();
            }
            catch (Exception e)
            {
                results.Writer.TryComplete(e);
            }
        }, CancellationToken.None);

        var complete = false;
        try
        {
            await foreach (var result in results.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                yield return result;
            complete = true;
            log.Add("", $"{Address(first)} - {Address(last)}", "Range scan", $"{found} answered");
            yield return new RangeScanResult { Finished = true, Tried = total, Total = total };
        }
        finally
        {
            if (!complete)
                await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static bool TryParseIpv4(string? text, out uint value)
    {
        value = 0;
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Split('.').Length != 4 || !IPAddress.TryParse(trimmed, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        value = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        return true;
    }

    private static IPAddress Address(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
