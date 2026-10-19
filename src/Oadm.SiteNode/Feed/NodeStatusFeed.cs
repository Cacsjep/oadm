using System.Reflection;

using Oadm.Contracts.SiteNode;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Devices;
using Oadm.SiteNode.Links;
using Oadm.SiteNode.Networks;
using Oadm.SiteNode.Relays;
using Oadm.SiteNode.State;

namespace Oadm.SiteNode.Feed;

/// <summary>The connection to the server as the app shows it, set by the outbound connector or the inbound listener.</summary>
public sealed class ConnectionState
{
    private readonly Lock _lock = new();
    private string _text = "";
    private bool _connected;

    public event Action? Changed;

    public string Text
    {
        get
        {
            lock (_lock)
                return _text;
        }
    }

    public bool Connected
    {
        get
        {
            lock (_lock)
                return _connected;
        }
    }

    public void Set(string text, bool connected)
    {
        lock (_lock)
        {
            if (string.Equals(_text, text, StringComparison.Ordinal) && _connected == connected)
                return;
            _text = text;
            _connected = connected;
        }

        Changed?.Invoke();
    }
}

/// <summary>
/// The node's state for the server (<c>Watch</c>, <see cref="NodeStatus"/>) and for the app (<see cref="NodeView"/>), and
/// one <see cref="Changed"/> for every source: node.json, the binding state, links, relays, the connection and the
/// connection counts.
/// </summary>
public sealed class NodeStatusFeed
{
    private readonly NodeStore _store;
    private readonly BindingState _state;
    private readonly NetworkScope _networks;
    private readonly Dialer _dialer;
    private readonly RelayHost _relays;
    private readonly ConnectionState _connection;
    private readonly NodeOptions _options;
    private long _version;

    public NodeStatusFeed(NodeStore store, BindingState state, NetworkScope networks, Dialer dialer, RelayHost relays, LinkRegistry links, ConnectionState connection, NodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(links);
        _store = store;
        _state = state;
        _networks = networks;
        _dialer = dialer;
        _relays = relays;
        _connection = connection;
        _options = options;
        store.Changed += Raise;
        state.Changed += Raise;
        relays.Changed += Raise;
        links.Changed += Raise;
        connection.Changed += Raise;
        dialer.CountsChanged += RaiseCounts;
    }

    /// <summary>Raised after a change of what the server or the app sees (counts too).</summary>
    public event Action? Changed;

    /// <summary>Raised after a change other than the connection counts (the server's Watch sends those at most once a second).</summary>
    public event Action? Counts;

    /// <summary>Grows with every change.</summary>
    public long Version => Interlocked.Read(ref _version);

    public static string ProgramVersion { get; } =
        typeof(NodeStatusFeed).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0-dev";

    public NodeStatus Status()
    {
        var config = _store.Snapshot();
        var status = new NodeStatus
        {
            State = _state.Binding switch
            {
                NodeBinding.Bound => Contracts.SiteNode.NodeState.Bound,
                NodeBinding.Closed => Contracts.SiteNode.NodeState.UnboundClosed,
                _ => Contracts.SiteNode.NodeState.UnboundOpen,
            },
            Mode = config.Mode == NodeLinkMode.Wait ? NodeMode.Wait : NodeMode.Connect,
            SiteName = config.BoundServers.FirstOrDefault()?.SiteName ?? "",
            AllCards = config.AllCards,
            ShortConnections = _dialer.ShortConnections,
            PermanentConnections = _dialer.PermanentConnections,
            Version = _options.Version,
        };
        foreach (var card in _networks.Cards())
            status.Cards.Add(new Contracts.SiteNode.NetworkCard { Id = card.Id, Name = card.Name, Reached = card.Reached, HasDefaultRouter = card.HasDefaultRouter, Addresses = { card.Addresses } });
        status.ExtraSubnets.Add(config.ExtraSubnets);
        status.Allowed.Add(config.Allowed.Select(Rule));
        status.Requested.Add(config.Requested.Select(Rule));
        foreach (var relay in _relays.Relays)
        {
            status.Relays.Add(new RelayInfo
            {
                Protocol = relay.Protocol == PortProtocol.Tcp ? Protocol.Tcp : Protocol.Udp,
                Port = relay.Port,
                Reason = relay.Reason,
                State = relay.State switch
                {
                    "Running" => RelayStateKind.RelayStateRunning,
                    "Port in use" => RelayStateKind.RelayStatePortInUse,
                    "No permission" => RelayStateKind.RelayStateNoPermission,
                    _ => RelayStateKind.RelayStateWaitingForApproval,
                },
                Message = relay.Message,
            });
        }

        return status;
    }

    public NodeView View()
    {
        var config = _store.Snapshot();
        var bound = config.BoundServers.FirstOrDefault();
        return new NodeView
        {
            NodeId = config.Id,
            Version = _options.Version,
            HostName = _options.HostName,
            Binding = _state.Binding,
            Mode = config.Mode,
            ServerAddress = config.ServerAddress,
            ListenPort = _options.ListenPort,
            BindingHoursLeft = Math.Round(_state.BindingLeft.TotalHours, 2),
            Bound = bound is null ? null : new BoundServerView(bound.Name, bound.Fingerprint, bound.BoundUtc, bound.SiteName),
            ConnectionText = _connection.Text,
            Connected = _connection.Connected,
            Cards = _networks.Cards(),
            AllCards = config.AllCards,
            ExtraSubnets = config.ExtraSubnets,
            Allowed = config.Allowed,
            Requested = config.Requested,
            Relays = _relays.Relays,
            ShortConnections = _dialer.ShortConnections,
            PermanentConnections = _dialer.PermanentConnections,
            NewServerIdentity = config.NewServerIdentity,
            WrongCodes = _state.WrongCodes,
            LockedUntil = _state.LockedUntil,
        };
    }

    private static PortRule Rule(PortEntry port) => new()
    {
        Direction = port.Use == PortUse.Listen ? PortDirection.Listen : PortDirection.Connect,
        Protocol = port.Protocol == PortProtocol.Udp ? Protocol.Udp : Protocol.Tcp,
        Port = port.Port,
        Reason = port.Reason,
    };

    private void Raise()
    {
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }

    private void RaiseCounts()
    {
        Interlocked.Increment(ref _version);
        Counts?.Invoke();
        Changed?.Invoke();
    }
}
