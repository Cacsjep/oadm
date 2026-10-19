using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Links;
using Oadm.SiteNode.Networks;
using Oadm.SiteNode.State;

namespace Oadm.SiteNode.Discovery;

/// <summary>
/// A waiting node (mode Wait) announces itself over mDNS so the Sites page's Search finds it (docs/sites.md, "States"):
/// service _oadm-site._tcp, TXT id, version and state (unbound, closed, bound; never the server's name). It answers
/// queries on every card (the server is usually on the office side) and announces itself after every change of state.
/// </summary>
public sealed partial class NodeAnnouncer(
    NodeStore store,
    BindingState binding,
    NetworkScope networks,
    InboundListener listener,
    NodeOptions options,
    ILogger<NodeAnnouncer> logger) : BackgroundService
{
    public const string Service = "_oadm-site._tcp.local";
    public const uint Ttl = 120;

    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    /// <summary>The answer to a query, or null when it does not ask for this node. Unicast for a legacy query (not from port 5353).</summary>
    public static byte[]? Answer(DnsPacket query, Announcement self)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(self);
        if (query.IsResponse)
            return null;
        var asked = query.Questions.Where(q =>
            (q.Name.Equals(Service, StringComparison.OrdinalIgnoreCase) && q.Type is DnsType.DomainPointer or DnsType.Any)
            || (q.Name.Equals(self.Instance, StringComparison.OrdinalIgnoreCase) && q.Type is DnsType.Srv or DnsType.Txt or DnsType.Any)
            || (q.Name.Equals(self.Host, StringComparison.OrdinalIgnoreCase) && q.Type is DnsType.A or DnsType.Aaaa or DnsType.Any)).ToList();
        return asked.Count == 0 ? null : DnsWire.Response(query.Id, asked, self.Records(Ttl));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Announce)
            return;
        var changed = new SemaphoreSlim(0);
        void OnChanged() => changed.Release();
        store.Changed += OnChanged;
        binding.Changed += OnChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (store.Read(c => c.Mode) != NodeLinkMode.Wait || listener.ActivePort == 0)
                {
                    using var poll = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    poll.CancelAfter(TimeSpan.FromSeconds(2)); // the listener's port comes a moment after the mode
                    await Quiet(changed.WaitAsync(poll.Token)).ConfigureAwait(false);
                    continue;
                }

                await AnnounceWhileWaitingAsync(changed, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            store.Changed -= OnChanged;
            binding.Changed -= OnChanged;
        }
    }

    private async Task AnnounceWhileWaitingAsync(SemaphoreSlim changed, CancellationToken ct)
    {
        Socket socket;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
            foreach (var address in networks.AllAddresses().Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group.Address, address.Address));
                }
                catch (SocketException)
                {
                }
            }
        }
        catch (SocketException e)
        {
            LogCannotAnnounce(logger, e.Message);
            await Task.Delay(TimeSpan.FromSeconds(30), options.Time, ct).ConfigureAwait(false);
            return;
        }

        using (socket)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var receiving = ReceiveAsync(socket, stop.Token);
            try
            {
                var last = "";
                while (store.Read(c => c.Mode) == NodeLinkMode.Wait && listener.ActivePort != 0 && !ct.IsCancellationRequested)
                {
                    var self = Current();
                    var key = string.Join('|', self.Records(Ttl).SelectMany(r => r.Texts).Append(self.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    if (!string.Equals(key, last, StringComparison.Ordinal))
                    {
                        last = key;
                        for (var i = 0; i < 2; i++)
                        {
                            Send(socket, DnsWire.Response(0, [], self.Records(Ttl)), Group);
                            await Task.Delay(TimeSpan.FromSeconds(1), options.Time, ct).ConfigureAwait(false);
                        }
                    }

                    await changed.WaitAsync(ct).ConfigureAwait(false);
                }

                Send(socket, DnsWire.Response(0, [], Current().Records(0)), Group); // goodbye
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                await Quiet(receiving).ConfigureAwait(false);
            }
        }
    }

    private async Task ReceiveAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
                if (DnsWire.Parse(buffer.AsSpan(0, result.ReceivedBytes)) is not { } query || Answer(query, Current()) is not { } answer)
                    continue;
                var from = (IPEndPoint)result.RemoteEndPoint;
                Send(socket, answer, from.Port == 5353 ? Group : from);
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

    private Announcement Current()
    {
        var config = store.Snapshot();
        var state = binding.Binding switch
        {
            NodeBinding.Bound => "bound",
            NodeBinding.Closed => "closed",
            _ => "unbound",
        };
        return new Announcement(config.Id, options.HostName, options.Version, state, listener.ActivePort,
            [.. networks.AllAddresses().Select(a => a.Address)]);
    }

    private static void Send(Socket socket, byte[] data, IPEndPoint to)
    {
        try
        {
            socket.SendTo(data, to);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }
    }

    private static async Task Quiet(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The site node cannot announce itself on the network: {Reason}")]
    private static partial void LogCannotAnnounce(ILogger logger, string reason);
}

/// <summary>What a waiting node announces.</summary>
public sealed record Announcement(string Id, string HostName, string Version, string State, int Port, IReadOnlyList<IPAddress> Addresses)
{
    public string Instance => $"{Label(HostName)} - {Id}.{NodeAnnouncer.Service}";

    public string Host => $"oadm-site-{Id}.local";

    public IReadOnlyList<DnsRecord> Records(uint ttl)
    {
        var records = new List<DnsRecord>
        {
            new(NodeAnnouncer.Service, DnsType.DomainPointer, ttl) { Target = Instance },
            new(Instance, DnsType.Srv, ttl) { Target = Host, Port = Port },
            new(Instance, DnsType.Txt, ttl) { Texts = ["id=" + Id, "version=" + Version, "state=" + State] },
        };
        foreach (var address in Addresses)
            records.Add(new DnsRecord(Host, address.AddressFamily == AddressFamily.InterNetwork ? DnsType.A : DnsType.Aaaa, ttl) { Address = address });
        return records;
    }

    private static string Label(string name)
    {
        var text = new string([.. name.Where(c => c is not '.' && !char.IsControl(c))]);
        return text.Length is 0 ? "OADM Site Node" : text.Length > 40 ? text[..40] : text;
    }
}
