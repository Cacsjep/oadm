using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Oadm.Contracts.SiteNode;
using Oadm.SiteNode.Networks;

namespace Oadm.SiteNode.Discovery;

/// <summary>
/// Collects PTR, SRV, TXT and A records of _axis-video._tcp into devices (CLAUDE.md "# Sites" "Zero-conf": TXT macaddress, the
/// instance "&lt;name&gt; - &lt;SERIAL&gt;", SRV port 80 at axis-&lt;serial&gt;.local). Pure logic, unit tested with packets.
/// </summary>
public sealed class AxisAggregator(Func<IPAddress, bool> reaches)
{
    public const string Service = "_axis-video._tcp.local";

    private readonly Dictionary<string, DnsRecord> _srv = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _txt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<IPAddress>> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _responder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Feeds one answer; returns the devices that are new or changed, and the questions that would complete the others.</summary>
    public (IReadOnlyList<BrowseResult> Found, IReadOnlyList<DnsQuestion> FollowUp) Add(DnsPacket packet, IPAddress responder)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!packet.IsResponse)
            return ([], []);
        foreach (var record in packet.Records)
        {
            switch (record.Type)
            {
                case DnsType.DomainPointer when record.Name.Equals(Service, StringComparison.OrdinalIgnoreCase) && record.Ttl > 0:
                    _instances.Add(record.Target);
                    _responder[record.Target] = responder;
                    break;
                case DnsType.Srv when IsInstance(record.Name):
                    _instances.Add(record.Name);
                    _srv[record.Name] = record;
                    _responder[record.Name] = responder;
                    break;
                case DnsType.Txt when IsInstance(record.Name):
                    _instances.Add(record.Name);
                    _txt[record.Name] = record.Texts;
                    break;
                case DnsType.A when record.Address is not null:
                    if (!_hosts.TryGetValue(record.Name, out var addresses))
                        _hosts[record.Name] = addresses = [];
                    addresses.Add(record.Address);
                    break;
            }
        }

        var found = new List<BrowseResult>();
        var followUp = new List<DnsQuestion>();
        foreach (var instance in _instances)
        {
            if (!_srv.TryGetValue(instance, out var srv))
            {
                followUp.Add(new DnsQuestion(instance, DnsType.Srv));
                followUp.Add(new DnsQuestion(instance, DnsType.Txt));
                continue;
            }

            var addresses = _hosts.TryGetValue(srv.Target, out var known) ? known.ToList() : [];
            if (addresses.Count == 0 && _responder.TryGetValue(instance, out var from))
                addresses.Add(from);
            // A device announces its link-local address next to the real one: keep it only when it is the only one.
            var routable = addresses.Where(a => !IsLinkLocal(a)).ToList();
            if (routable.Count > 0)
                addresses = routable;
            addresses = [.. addresses.Where(reaches)];
            var serial = Serial(instance, _txt.GetValueOrDefault(instance));
            if (addresses.Count == 0 || serial is null)
                continue;
            var result = new BrowseResult
            {
                Serial = serial,
                InstanceName = Label(instance),
                Port = srv.Port,
                HostName = srv.Target,
            };
            result.Addresses.Add(addresses.Select(a => a.ToString()).Order(StringComparer.Ordinal));
            var key = string.Join(',', result.Addresses) + "|" + result.Port + "|" + result.HostName;
            if (_sent.TryGetValue(instance, out var sent) && string.Equals(sent, key, StringComparison.Ordinal))
                continue;
            _sent[instance] = key;
            found.Add(result);
        }

        return (found, followUp);
    }

    /// <summary>The MAC address (upper hex, 12) from TXT macaddress, else from the instance name's end.</summary>
    public static string? Serial(string instance, IReadOnlyList<string>? txt)
    {
        foreach (var entry in txt ?? [])
        {
            var equals = entry.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && entry[..equals].Equals("macaddress", StringComparison.OrdinalIgnoreCase) && Normalize(entry[(equals + 1)..]) is { } fromTxt)
                return fromTxt;
        }

        var label = Label(instance);
        var dash = label.LastIndexOf(" - ", StringComparison.Ordinal);
        return dash >= 0 ? Normalize(label[(dash + 3)..]) : null;
    }

    private static string? Normalize(string value)
    {
        var hex = new string([.. value.Where(Uri.IsHexDigit)]).ToUpperInvariant();
        return hex.Length == 12 && value.Trim().Length <= 17 ? hex : null;
    }

    private static string Label(string instance)
    {
        var index = instance.IndexOf("._", StringComparison.Ordinal);
        return index > 0 ? instance[..index] : instance;
    }

    private static bool IsInstance(string name) => name.EndsWith("." + Service, StringComparison.OrdinalIgnoreCase);

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 169 && bytes[1] == 254;
    }
}

/// <summary>
/// <c>Browse</c>: mDNS _axis-video._tcp on the reached cards, one socket per card address sending "legacy unicast" queries
/// (the answers come back to that socket, so it works next to the OS's own mDNS responder on port 5353).
/// </summary>
/// <summary><c>Browse</c> of the node: <see cref="AxisBrowser"/>, or a fake in tests (no multicast in unit tests).</summary>
public interface IAxisBrowser
{
    /// <summary>Every Axis device answering until <paramref name="ct"/> ends.</summary>
    IAsyncEnumerable<BrowseResult> BrowseAsync(CancellationToken ct);
}

public sealed class AxisBrowser(NetworkScope networks, TimeProvider time) : IAxisBrowser
{
    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    public async IAsyncEnumerable<BrowseResult> BrowseAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var aggregator = new AxisAggregator(networks.Reaches);
        var packets = Channel.CreateBounded<(byte[] Data, IPAddress From)>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest });
        var sockets = new List<Socket>();
        foreach (var local in networks.ReachedAddresses().Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(local.Address, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.Address.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                sockets.Add(socket);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loops = sockets.Select(s => ReceiveAsync(s, packets.Writer, stop.Token)).ToList();
        loops.Add(QueryAsync(sockets, stop.Token));
        try
        {
            await foreach (var (data, from) in packets.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (DnsWire.Parse(data) is not { } packet)
                    continue;
                var (found, followUp) = aggregator.Add(packet, from);
                if (followUp.Count > 0)
                    Send(sockets, DnsWire.Query(0, [.. followUp.Take(16)]));
                foreach (var result in found)
                    yield return result;
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            foreach (var socket in sockets)
                socket.Dispose();
            try
            {
                await Task.WhenAll(loops).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task QueryAsync(List<Socket> sockets, CancellationToken ct)
    {
        var query = DnsWire.Query(0, new DnsQuestion(AxisAggregator.Service, DnsType.DomainPointer));
        try
        {
            foreach (var delay in new[] { 0, 1, 2 })
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), time, ct).ConfigureAwait(false);
                Send(sockets, query);
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), time, ct).ConfigureAwait(false);
                Send(sockets, query);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Send(List<Socket> sockets, byte[] query)
    {
        foreach (var socket in sockets)
        {
            try
            {
                socket.SendTo(query, Group);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task ReceiveAsync(Socket socket, ChannelWriter<(byte[], IPAddress)> writer, CancellationToken ct)
    {
        var buffer = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
                writer.TryWrite((buffer.AsSpan(0, result.ReceivedBytes).ToArray(), ((IPEndPoint)result.RemoteEndPoint).Address));
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
        }
    }
}
