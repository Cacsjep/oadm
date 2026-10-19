using System.Net;
using System.Net.Sockets;

using Google.Protobuf;

using Grpc.Core;

using Microsoft.AspNetCore.Http;

using Oadm.Contracts.SiteNode;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Devices;
using Oadm.SiteNode.Discovery;
using Oadm.SiteNode.Links;
using Oadm.SiteNode.Networks;
using Oadm.SiteNode.Relays;
using Oadm.SiteNode.State;
using Oadm.SiteNode.Feed;

namespace Oadm.SiteNode.Service;

/// <summary>
/// <c>SiteNodeService</c> (site_node.proto), hosted by the node on every link. Hello answers anyone; PairStart and Bind
/// only while not bound; everything else only the bound server's identity on a link that is not refused (one session at a
/// time). A refusal is FAILED_PRECONDITION with the plain text, a wrong pairing code PERMISSION_DENIED, too many wrong
/// codes RESOURCE_EXHAUSTED.
/// </summary>
public sealed class SiteNodeGrpcService(
    BindingState binding,
    NodeStore store,
    PortPolicy ports,
    NetworkScope networks,
    Dialer dialer,
    RelayHost relays,
    NodeStatusFeed feed,
    INodeDns dns,
    IPinger pinger,
    IDeviceConnector connector,
    OutboundConnector outbound,
    LinkRegistry links,
    AxisBrowser browser,
    RangeScanner rangeScanner,
    NodeOptions options) : SiteNodeService.SiteNodeServiceBase
{
    public const int ProtocolVersion = 1;

    /// <summary>What this node can do (Hello).</summary>
    public static readonly IReadOnlyList<string> Features = ["dial", "browse", "range-scan", "resolve", "ping", "local-address", "relay.udp", "relay.tcp"];

    /// <summary>The Watch stream sends a change of the connection counts alone at most this often.</summary>
    public static readonly TimeSpan CountsInterval = TimeSpan.FromSeconds(1);

    public override Task<HelloReply> Hello(HelloRequest request, ServerCallContext context)
    {
        var link = LinkOf(context);
        var config = store.Snapshot();
        var state = binding.Binding;
        var isServer = state == NodeBinding.Bound && binding.IsBoundTo(link.ServerFingerprintHex) && links.RefusalFor(link) is null;
        var pairing = state == NodeBinding.Bound && !isServer && binding.IsPairingOpenFor(link.ServerFingerprintHex);
        var reply = new HelloReply
        {
            Version = options.Version,
            ProtocolVersion = ProtocolVersion,
            State = pairing ? NodeState.UnboundOpen : state switch
            {
                NodeBinding.Bound => NodeState.Bound,
                NodeBinding.Closed => NodeState.UnboundClosed,
                _ => NodeState.UnboundOpen,
            },
        };

        // Strangers learn nothing about a bound node beyond its state and version (who owns it, its id, its name, its mode).
        if (state != NodeBinding.Bound || isServer || pairing)
        {
            reply.NodeId = config.Id;
            reply.Mode = config.Mode == NodeLinkMode.Wait ? NodeMode.Wait : NodeMode.Connect;
            reply.HostName = options.HostName;
            reply.LinkRole = !link.Outbound ? LinkRole.Unspecified : link.Worker ? LinkRole.Worker : LinkRole.Control;
            reply.Features.Add(Features);
            reply.NewIdentityAccepted = pairing;
        }

        if (isServer && binding.Bound is { } bound)
        {
            reply.BoundServerName = bound.Name;
            reply.SiteName = bound.SiteName;
        }

        return Task.FromResult(reply);
    }

    public override Task<PairStartReply> PairStart(PairStartRequest request, ServerCallContext context) =>
        Run(() =>
        {
            var (round1, round2) = binding.StartPairing(LinkOf(context), request.ServerRound1);
            return new PairStartReply { NodeRound1 = round1, NodeRound2 = round2 };
        });

    public override Task<BindReply> Bind(BindRequest request, ServerCallContext context) =>
        Run(() =>
        {
            var link = LinkOf(context);
            var proof = binding.Bind(link, request.SiteName, request.ServerName, request.ServerRound2, request.Proof.ToByteArray());
            link.MarkServed(); // the session that bound is the node's one server session
            return new BindReply { Proof = ByteString.CopyFrom(proof) };
        });

    public override Task<UnbindReply> Unbind(UnbindRequest request, ServerCallContext context) =>
        Run(() =>
        {
            RequireServer(context);
            binding.Unbind();
            return new UnbindReply();
        });

    public override Task<RenameReply> Rename(RenameRequest request, ServerCallContext context) =>
        Run(() =>
        {
            RequireServer(context);
            binding.Rename(request.SiteName);
            return new RenameReply();
        });

    public override Task<RotateIdentityReply> RotateIdentity(RotateIdentityRequest request, ServerCallContext context) =>
        Run(() =>
        {
            var link = RequireServer(context);
            binding.RotateIdentity(link, request.NewCertificate.ToByteArray(), request.Signature.ToByteArray());
            return new RotateIdentityReply();
        });

    public override Task<SetServerAddressesReply> SetServerAddresses(ServerAddresses request, ServerCallContext context) =>
        Run(() =>
        {
            RequireServer(context);
            var addresses = request.Addresses.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();
            store.Update(c =>
            {
                c.ServerAddresses = new ServerAddressList
                {
                    DnsName = (request.DnsName ?? "").Trim(),
                    Addresses = addresses,
                    Port = request.Port is > 0 and <= 65535 ? request.Port : NodeConfig.DefaultPort,
                };
            });
            return new SetServerAddressesReply();
        });

    public override Task<RequestPortsReply> RequestPorts(RequestPortsRequest request, ServerCallContext context) =>
        Run(() =>
        {
            RequireServer(context);
            var asked = request.Ports
                .Where(p => p.Direction != PortDirection.Unspecified && p.Protocol != Protocol.Unspecified)
                .Select(p => new PortEntry(
                    p.Direction == PortDirection.Listen ? PortUse.Listen : PortUse.Connect,
                    p.Protocol == Protocol.Udp ? PortProtocol.Udp : PortProtocol.Tcp,
                    p.Port,
                    p.Reason ?? ""));
            ports.Request(asked);
            return new RequestPortsReply();
        });

    public override async Task<OpenConnectionsReply> OpenConnections(OpenConnectionsRequest request, ServerCallContext context)
    {
        try
        {
            RequireServer(context);
            return new OpenConnectionsReply { Open = await outbound.OpenWorkersAsync(request.Count, context.CancellationToken).ConfigureAwait(false) };
        }
        catch (NodeRefusalException e)
        {
            throw e.ToRpc();
        }
    }

    public override async Task Watch(WatchRequest request, IServerStreamWriter<NodeStatus> responseStream, ServerCallContext context)
    {
        Guard(context);
        var ct = context.CancellationToken;
        using var changed = new SemaphoreSlim(0);
        var other = 0;
        void OnChanged() => changed.Release();
        void OnOther() => Interlocked.Exchange(ref other, 1);
        feed.Changed += OnChanged;
        feed.Counts += OnChanged;
        store.Changed += OnOther;
        binding.Changed += OnOther;
        relays.Changed += OnOther;
        try
        {
            var sentAt = options.Time.GetTimestamp();
            await responseStream.WriteAsync(feed.Status(), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                await changed.WaitAsync(ct).ConfigureAwait(false);
                if (Interlocked.Exchange(ref other, 0) == 0)
                {
                    // Only connection counts changed: at most once a second.
                    var wait = CountsInterval - options.Time.GetElapsedTime(sentAt);
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, options.Time, ct).ConfigureAwait(false);
                }

                while (changed.CurrentCount > 0)
                    await changed.WaitAsync(ct).ConfigureAwait(false);
                sentAt = options.Time.GetTimestamp();
                await responseStream.WriteAsync(feed.Status(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            feed.Changed -= OnChanged;
            feed.Counts -= OnChanged;
            store.Changed -= OnOther;
            binding.Changed -= OnOther;
            relays.Changed -= OnOther;
        }
    }

    public override async Task Dial(IAsyncStreamReader<DialMessage> requestStream, IServerStreamWriter<DialMessage> responseStream, ServerCallContext context)
    {
        var link = Guard(context);
        var ct = context.CancellationToken;
        if (!await requestStream.MoveNext(ct).ConfigureAwait(false) || requestStream.Current.KindCase != DialMessage.KindOneofCase.Open)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The first message must open the connection."));
        var (result, connection) = await dialer.OpenAsync(requestStream.Current.Open, ServerName(link), ct).ConfigureAwait(false);
        await responseStream.WriteAsync(new DialMessage { Result = result }, ct).ConfigureAwait(false);
        if (connection is null)
            return;
        using (connection)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var socket = connection.Socket;
            var up = Task.Run(() => UpAsync(requestStream, socket, stop.Token), CancellationToken.None);
            var down = Task.Run(() => DownAsync(socket, responseStream, stop.Token), CancellationToken.None);
            var first = await Task.WhenAny(up, down).ConfigureAwait(false);
            if (first.IsFaulted || first.IsCanceled)
                await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(up, down).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                // The socket closes with the connection; the server sees the end of the call.
            }
        }
    }

    public override Task Relay(IAsyncStreamReader<RelayMessage> requestStream, IServerStreamWriter<RelayMessage> responseStream, ServerCallContext context)
    {
        var link = Guard(context);
        return relays.RunAsync(requestStream, responseStream, ServerName(link), context.CancellationToken);
    }

    public override async Task<ResolveReply> Resolve(ResolveRequest request, ServerCallContext context)
    {
        Guard(context);
        var reply = new ResolveReply();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 253)
            return reply;
        var found = await dns.ResolveAsync(request.Name.Trim(), context.CancellationToken).ConfigureAwait(false);
        reply.Addresses.Add(found.Select(a => a.ToString()));
        return reply;
    }

    public override async Task<ReverseLookupReply> ReverseLookup(ReverseLookupRequest request, ServerCallContext context)
    {
        Guard(context);
        var address = Address(request.Address);
        var name = await DnsNameRules.ReverseLookupAsync(dns, address, TimeSpan.FromSeconds(3), context.CancellationToken).ConfigureAwait(false);
        return new ReverseLookupReply { Name = name ?? "" };
    }

    public override async Task<PingReply> Ping(PingRequest request, ServerCallContext context)
    {
        Guard(context);
        var address = Reached(request.Address);
        var timeout = Timeout(request.TimeoutMs, 1000);
        try
        {
            var roundTrip = await pinger.PingAsync(address, timeout, context.CancellationToken).ConfigureAwait(false);
            return new PingReply { Answered = roundTrip is not null, RoundTripMs = (int)(roundTrip?.TotalMilliseconds ?? 0) };
        }
        catch (NotSupportedException)
        {
            // No ping for this user: a device that answers on 80 or 443 counts as answering.
            var start = options.Time.GetTimestamp();
            foreach (var port in new[] { 80, 443 })
            {
                if (await ProbeAsync(address, port, timeout, context.CancellationToken).ConfigureAwait(false))
                    return new PingReply { Answered = true, RoundTripMs = (int)options.Time.GetElapsedTime(start).TotalMilliseconds };
            }

            return new PingReply();
        }
    }

    public override async Task<ProbeTcpReply> ProbeTcp(ProbeTcpRequest request, ServerCallContext context)
    {
        Guard(context);
        var address = Reached(request.Address);
        if (!ports.Allows(PortUse.Connect, PortProtocol.Tcp, request.Port))
            throw new RpcException(new Status(StatusCode.FailedPrecondition, AdminTexts.PortNotAllowed(PortProtocol.Tcp, request.Port)));
        return new ProbeTcpReply { Open = await ProbeAsync(address, request.Port, Timeout(request.TimeoutMs, 1000), context.CancellationToken).ConfigureAwait(false) };
    }

    public override Task<LocalAddressForReply> LocalAddressFor(LocalAddressForRequest request, ServerCallContext context)
    {
        Guard(context);
        var address = Address(request.Address);
        return Task.FromResult(new LocalAddressForReply { Address = networks.LocalAddressFor(address)?.ToString() ?? "" });
    }

    public override async Task Browse(BrowseRequest request, IServerStreamWriter<BrowseResult> responseStream, ServerCallContext context)
    {
        Guard(context);
        var duration = TimeSpan.FromSeconds(Math.Clamp(request.DurationSeconds <= 0 ? 30 : request.DurationSeconds, 1, 300));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        stop.CancelAfter(duration);
        try
        {
            await foreach (var found in browser.BrowseAsync(stop.Token).ConfigureAwait(false))
                await responseStream.WriteAsync(found, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
        }
    }

    public override async Task RangeScan(RangeScanRequest request, IServerStreamWriter<RangeScanResult> responseStream, ServerCallContext context)
    {
        Guard(context);
        if (!RangeScanner.TryParse(request.From, request.To, out var first, out var last, out var error))
            throw new RpcException(new Status(StatusCode.InvalidArgument, error));
        var timeout = Timeout(request.TimeoutMs, 1500);
        await foreach (var result in rangeScanner.ScanAsync(first, last, request.Parallelism, timeout, context.CancellationToken).ConfigureAwait(false))
            await responseStream.WriteAsync(result, context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>The link of a call (set by the connection middleware from <see cref="LinkEndPoint"/>).</summary>
    public static SiteLink LinkOf(ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetHttpContext().Features.Get<SiteLink>()
            ?? throw new RpcException(new Status(StatusCode.Internal, "The call has no site link."));
    }

    private SiteLink Guard(ServerCallContext context)
    {
        try
        {
            return RequireServer(context);
        }
        catch (NodeRefusalException e)
        {
            throw e.ToRpc();
        }
    }

    // Everything but Hello and the binding calls: the bound server, in its one active session (checked on every call
    // against the binding of that moment, so a bind, a rotation or another session applies at once).
    private SiteLink RequireServer(ServerCallContext context)
    {
        var link = LinkOf(context);
        if (binding.Binding != NodeBinding.Bound)
            throw new NodeRefusalException(binding.Binding == NodeBinding.Closed ? AdminTexts.BindingClosed : AdminTexts.NotBoundToCaller);
        if (!binding.IsBoundTo(link.ServerFingerprintHex))
            throw new NodeRefusalException(AdminTexts.BoundToAnotherServer);
        if (links.RefusalFor(link) is { } refusal)
            throw new NodeRefusalException(refusal);
        link.MarkServed();
        return link;
    }

    private string ServerName(SiteLink link) =>
        binding.Bound is { } bound && binding.IsBoundTo(link.ServerFingerprintHex) && bound.Name.Length > 0 ? bound.Name : link.RemoteHost;

    private static IPAddress Address(string text) =>
        IPAddress.TryParse(text?.Trim().Trim('[', ']'), out var address)
            ? address
            : throw new RpcException(new Status(StatusCode.InvalidArgument, "Not an IP address."));

    private IPAddress Reached(string text)
    {
        var address = Address(text);
        return networks.Reaches(address) ? address : throw new RpcException(new Status(StatusCode.FailedPrecondition, AdminTexts.OutsideNetworks));
    }

    private static TimeSpan Timeout(int milliseconds, int fallback) =>
        TimeSpan.FromMilliseconds(Math.Clamp(milliseconds <= 0 ? fallback : milliseconds, 50, 60_000));

    private async Task<bool> ProbeAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = new CancellationTokenSource(timeout, options.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        try
        {
            using var socket = await connector.ConnectAsync(new IPEndPoint(address, port), linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return true; // a host sent a reset: something is at this address
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<T> Run<T>(Func<T> call)
    {
        await Task.Yield();
        try
        {
            return call();
        }
        catch (NodeRefusalException e)
        {
            throw e.ToRpc();
        }
    }

    private static async Task UpAsync(IAsyncStreamReader<DialMessage> requests, Socket socket, CancellationToken ct)
    {
        while (await requests.MoveNext(ct).ConfigureAwait(false))
        {
            var message = requests.Current;
            if (message.KindCase == DialMessage.KindOneofCase.Data && !message.Data.IsEmpty)
            {
                await socket.SendAsync(message.Data.Memory, SocketFlags.None, ct).ConfigureAwait(false);
            }
            else if (message.KindCase == DialMessage.KindOneofCase.End)
            {
                socket.Shutdown(SocketShutdown.Send);
                return;
            }
        }

        // The server ended its stream without a half-close: the same for the device.
        socket.Shutdown(SocketShutdown.Send);
    }

    private static async Task DownAsync(Socket socket, IServerStreamWriter<DialMessage> responses, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            if (read == 0)
            {
                await responses.WriteAsync(new DialMessage { End = true }, ct).ConfigureAwait(false);
                return;
            }

            await responses.WriteAsync(new DialMessage { Data = ByteString.CopyFrom(buffer, 0, read) }, ct).ConfigureAwait(false);
        }
    }
}
