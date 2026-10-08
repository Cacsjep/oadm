using System.Net;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.DhcpServer.Leases;
using Oadm.Plugins.DhcpServer.Protocol;
using Oadm.Plugins.Network;

namespace Oadm.Plugins.DhcpServer.Serving;

#pragma warning disable CA1873 // Log arguments are short MAC/IP texts, formatted only behind IsEnabled checks.

/// <summary>What the server hands out on the selected interface right now.</summary>
/// <param name="Network">Interface, subnet, router, DNS, domain.</param>
/// <param name="Pool">Dynamic range (server, router and DNS addresses excluded).</param>
public sealed record DhcpScope(DhcpNetworkInfo Network, AddressPool Pool)
{
    public uint ServerAddress => Network.ServerAddress;

    public static DhcpScope Create(DhcpNetworkInfo network, uint start, uint end)
    {
        ArgumentNullException.ThrowIfNull(network);
        var excluded = new HashSet<uint> { network.ServerAddress };
        foreach (var text in network.Dns.Append(network.Router))
        {
            if (Ip4.TryParse(text, out var a))
            {
                excluded.Add(a);
            }
        }

        return new DhcpScope(network, new AddressPool(start, end, excluded));
    }
}

/// <summary>A reply and where it goes.</summary>
public sealed record DhcpReply(DhcpMessage Message, IPEndPoint Destination);

/// <summary>Timings and limits of <see cref="DhcpEngine"/>.</summary>
public sealed record DhcpEngineOptions
{
    /// <summary>A pending offer ends after this time without a request.</summary>
    public TimeSpan OfferTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>An address that answered the probe or was declined by a client is skipped this long.</summary>
    public TimeSpan ConflictHold { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The in-use probe of an address gives up after this time (no answer = free).</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Addresses tried per request when the probe finds them in use.</summary>
    public int ProbeAttempts { get; init; } = 3;

    /// <summary>In-use probes running at the same time; requests beyond get no answer (the client asks again).</summary>
    public int MaxConcurrentProbes { get; init; } = 32;

    /// <summary>Port replies go to (68; tests use other ports).</summary>
    public int ClientPort { get; init; } = 68;

    public TimeSpan LeaseTime { get; init; } = DhcpServerPluginInfo.LeaseTime;
}

/// <summary>
/// The RFC 2131 server state machine: DISCOVER -> OFFER (after the in-use probe of a new address), REQUEST -> ACK / NAK
/// (selecting, init-reboot, renewing, rebinding), DECLINE (address marked as a conflict), RELEASE, INFORM -> ACK.
/// Thread safe; the lease decisions are made atomically in <see cref="LeaseStore"/>, the probe runs outside its lock.
/// Messages relayed by a DHCP relay agent are not served in v1 (logged).
/// </summary>
public sealed partial class DhcpEngine : IDisposable
{
    private readonly LeaseStore _store;
    private readonly IAddressProbe _probe;
    private readonly DhcpEngineOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _probes;
    private long _lastExhausted;

    public DhcpEngine(LeaseStore store, IAddressProbe probe, DhcpEngineOptions? options = null, TimeProvider? time = null, ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _options = options ?? new DhcpEngineOptions();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _probes = new SemaphoreSlim(_options.MaxConcurrentProbes, _options.MaxConcurrentProbes);
    }

    /// <summary>What is handed out; null = nothing is answered.</summary>
    public DhcpScope? Scope { get; set; }

    /// <summary>MAC addresses never answered (the server's own check for other DHCP servers).</summary>
    public Func<ulong, bool> IgnoreMac { get; set; } = _ => false;

    /// <summary>
    /// Called after every ACK of a lease (new or renewed) with the MAC and the address, on the receive path: must return at
    /// once (the automatic add queues the work). A throwing handler is logged and never stops the reply.
    /// </summary>
    public Action<ulong, uint>? Leased { get; set; }

    /// <summary>The pool had no free address within <paramref name="window"/>.</summary>
    public bool ExhaustedWithin(TimeSpan window)
    {
        var last = Interlocked.Read(ref _lastExhausted);
        return last != 0 && _time.GetElapsedTime(last) <= window;
    }

    /// <summary>Most pending offers: half the pool (at least 16), so a flood of new clients cannot take the whole pool.</summary>
    public static int MaxPendingOffers(AddressPool pool) => (int)Math.Clamp((pool?.Size ?? 0) / 2, 16, 32_768);

    public void Dispose() => _probes.Dispose();

    public async ValueTask<DhcpReply?> HandleAsync(DhcpMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = Scope;
        var mac = request.Mac;
        if (scope is null || request.Op != DhcpMessage.BootRequest || !MacAddress.IsUnicast(mac) || IgnoreMac(mac))
        {
            return null;
        }

        if (request.RelayAddress != 0)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                LogRelayed(MacAddress.Format(mac), Ip4.Format(request.RelayAddress));
            }

            return null;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        switch (request.MessageType)
        {
            case DhcpMessageType.Discover:
                return await DiscoverAsync(request, scope, mac, now, ct).ConfigureAwait(false);
            case DhcpMessageType.Request:
                return Request(request, scope, mac, now);
            case DhcpMessageType.Decline:
                if ((request.ServerId is null || request.ServerId == scope.ServerAddress) && request.RequestedAddress is { } declined)
                {
                    _store.Decline(mac, declined, now + _options.ConflictHold);
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        LogDeclined(MacAddress.Format(mac), Ip4.Format(declined));
                    }
                }

                return null;
            case DhcpMessageType.Release:
                if (request.ServerId is null || request.ServerId == scope.ServerAddress)
                {
                    _store.Release(mac, request.ClientAddress);
                }

                return null;
            case DhcpMessageType.Inform:
                var inform = Reply(request, scope, DhcpMessageType.Ack, 0, withLease: false);
                inform.ClientAddress = request.ClientAddress;
                return new DhcpReply(inform, Destination(request, inform));
            default:
                return null;
        }
    }

    private async ValueTask<DhcpReply?> DiscoverAsync(DhcpMessage request, DhcpScope scope, ulong mac, DateTime now, CancellationToken ct)
    {
        var maxPending = MaxPendingOffers(scope.Pool);
        for (var attempt = 0; attempt < _options.ProbeAttempts; attempt++)
        {
            var address = _store.ReserveOffer(mac, request.RequestedAddress, scope.Pool, now, now + _options.OfferTimeout, maxPending, request.HostName, out var needsProbe);
            if (address is not { } offered)
            {
                if (_store.PendingOffers < maxPending)
                {
                    if (!ExhaustedWithin(TimeSpan.FromMinutes(1)))
                    {
                        if (_logger.IsEnabled(LogLevel.Warning))
                        {
                            LogExhausted(MacAddress.Format(mac)); // once a minute
                        }
                    }

                    Interlocked.Exchange(ref _lastExhausted, _time.GetTimestamp());
                }
                else
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        LogTooManyOffers(MacAddress.Format(mac));
                    }
                }

                return null;
            }

            if (needsProbe)
            {
                if (!await _probes.WaitAsync(0, ct).ConfigureAwait(false))
                {
                    _store.WithdrawOffer(mac);
                    return null; // busy: the client asks again in a few seconds
                }

                bool inUse;
                try
                {
                    inUse = await ProbeAsync(offered, ct).ConfigureAwait(false);
                }
                finally
                {
                    _probes.Release();
                }

                if (inUse)
                {
                    _store.RejectOffer(mac, offered, now + _options.ConflictHold);
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        LogConflict(Ip4.Format(offered));
                    }
                    continue;
                }
            }

            var offer = Reply(request, scope, DhcpMessageType.Offer, offered, withLease: true);
            return new DhcpReply(offer, Destination(request, offer));
        }

        return null;
    }

    private DhcpReply? Request(DhcpMessage request, DhcpScope scope, ulong mac, DateTime now)
    {
        var expires = now + _options.LeaseTime;
        if (request.ServerId is { } serverId)
        {
            // SELECTING: the client chose a server.
            if (serverId != scope.ServerAddress)
            {
                _store.WithdrawOffer(mac);
                return null;
            }

            var wanted = request.RequestedAddress ?? request.ClientAddress;
            return _store.TryBind(mac, wanted, scope.Pool, now, expires, request.HostName, allowNew: false)
                ? Ack(request, scope, wanted, mac)
                : Nak(request, scope, "The offered address is no longer available.");
        }

        if (request.RequestedAddress is { } requested)
        {
            // INIT-REBOOT: the client wants to keep the address it had.
            if (!scope.Network.Contains(requested))
            {
                return Nak(request, scope, "Wrong network.");
            }

            if (_store.TryBind(mac, requested, scope.Pool, now, expires, request.HostName, allowNew: false))
            {
                return Ack(request, scope, requested, mac);
            }

            return _store.Knows(mac) || _store.ConflictsWith(mac, requested)
                ? Nak(request, scope, "The address is not leased to this client.")
                : null; // RFC 2131 4.3.2: no record of this client: remain silent
        }

        if (request.ClientAddress != 0)
        {
            // RENEWING / REBINDING: extend the lease.
            if (scope.Network.Contains(request.ClientAddress)
                && _store.TryBind(mac, request.ClientAddress, scope.Pool, now, expires, request.HostName, allowNew: false))
            {
                return Ack(request, scope, request.ClientAddress, mac);
            }

            return Nak(request, scope, "The lease is not known.");
        }

        return null;
    }

    private DhcpReply Ack(DhcpMessage request, DhcpScope scope, uint address, ulong mac)
    {
        var ack = Reply(request, scope, DhcpMessageType.Ack, address, withLease: true);
        ack.ClientAddress = request.ClientAddress;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            LogLeased(MacAddress.Format(mac), Ip4.Format(address));
        }

        if (Leased is { } leased)
        {
            try
            {
                leased(mac, address);
            }
#pragma warning disable CA1031 // The reply goes out whatever the listener does.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogLeasedHandlerFailed(ex);
            }
        }

        return new DhcpReply(ack, Destination(request, ack));
    }

    private DhcpReply Nak(DhcpMessage request, DhcpScope scope, string reason)
    {
        var nak = new DhcpMessage
        {
            Op = DhcpMessage.BootReply,
            HardwareType = request.HardwareType,
            HardwareLength = request.HardwareLength,
            TransactionId = request.TransactionId,
            Flags = request.Flags,
            ClientHardware = (byte[])request.ClientHardware.Clone(),
            MessageType = DhcpMessageType.Nak,
            ServerId = scope.ServerAddress,
            Message = reason,
        };
        if (_logger.IsEnabled(LogLevel.Information))
        {
            LogNak(MacAddress.Format(request.Mac), reason);
        }

        return new DhcpReply(nak, new IPEndPoint(IPAddress.Broadcast, _options.ClientPort));
    }

    private DhcpMessage Reply(DhcpMessage request, DhcpScope scope, DhcpMessageType type, uint address, bool withLease)
    {
        var network = scope.Network;
        var seconds = (uint)_options.LeaseTime.TotalSeconds;
        var reply = new DhcpMessage
        {
            Op = DhcpMessage.BootReply,
            HardwareType = request.HardwareType,
            HardwareLength = request.HardwareLength,
            TransactionId = request.TransactionId,
            Flags = request.Flags,
            YourAddress = address,
            ClientHardware = (byte[])request.ClientHardware.Clone(),
            MessageType = type,
            ServerId = scope.ServerAddress,
            LeaseTime = withLease ? seconds : null,
            RenewalTime = withLease ? seconds / 2 : null,
            RebindingTime = withLease ? (uint)(seconds * 0.875) : null,
            HostName = request.HostName,
        };
        if (request.Requests(DhcpOption.SubnetMask))
        {
            reply.SubnetMask = Oadm.Plugins.Network.Model.Ipv4.Mask(network.PrefixLength);
        }

        if (request.Requests(DhcpOption.Router) && network.Router is { } router && Ip4.TryParse(router, out var r))
        {
            reply.Routers = [r];
        }

        if (request.Requests(DhcpOption.DnsServers) && network.Dns.Count > 0)
        {
            reply.DnsServers = [.. network.Dns.Select(d => Ip4.TryParse(d, out var a) ? a : 0u).Where(a => a != 0)];
        }

        if (request.Requests(DhcpOption.DomainName))
        {
            reply.DomainName = network.Domain;
        }

        return reply;
    }

    /// <summary>
    /// RFC 2131 4.1: to ciaddr when the client has an address, else broadcast. A unicast to a client without an address
    /// (broadcast flag clear) would need an ARP entry the server cannot add portably, so it is broadcast too.
    /// </summary>
    private IPEndPoint Destination(DhcpMessage request, DhcpMessage reply)
    {
        if (request.ClientAddress != 0 && reply.MessageType != DhcpMessageType.Nak)
        {
            return new IPEndPoint(Ip4.ToAddress(request.ClientAddress), _options.ClientPort);
        }

        return new IPEndPoint(IPAddress.Broadcast, _options.ClientPort);
    }

    private async Task<bool> ProbeAsync(uint address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ProbeTimeout);
        try
        {
            return (await _probe.ProbeAsync(Ip4.Format(address), timeout.Token).ConfigureAwait(false)).InUse;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // no answer in time: free
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DHCP: {Mac} got {Address}")]
    private partial void LogLeased(string mac, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DHCP: the lease listener failed")]
    private partial void LogLeasedHandlerFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "DHCP: NAK to {Mac}: {Reason}")]
    private partial void LogNak(string mac, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DHCP: {Mac} declined {Address} (in use by another device); skipped for a while")]
    private partial void LogDeclined(string mac, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DHCP: {Address} answers the in-use probe; skipped for a while")]
    private partial void LogConflict(string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DHCP: address pool exhausted, no offer for {Mac}")]
    private partial void LogExhausted(string mac);

    [LoggerMessage(Level = LogLevel.Debug, Message = "DHCP: too many pending offers, no offer for {Mac}")]
    private partial void LogTooManyOffers(string mac);

    [LoggerMessage(Level = LogLevel.Debug, Message = "DHCP: ignored relayed message from {Mac} via relay {Relay} (relays are not supported)")]
    private partial void LogRelayed(string mac, string relay);
}
