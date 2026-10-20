using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Validation;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.App.Services;

namespace Oadm.SiteNode.App.ViewModels;

/// <summary>Confirmation popups of the window (ui:MessageWindow); a fake in tests.</summary>
public interface IAppDialogs
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
}

/// <summary>The tabs of the window below the header.</summary>
public enum NodeTab
{
    Status,
    Networks,
    Ports,
    Activity,
}

/// <summary>What the header of the window shows: the state in one line and the one thing to do now.</summary>
public enum NodeHeader
{
    /// <summary>The service does not answer.</summary>
    NoService,

    /// <summary>Not bound, connecting to the server, and no address yet or the server cannot be reached: the address form.</summary>
    NeedsServer,

    /// <summary>The bound server presents a new identity: Accept.</summary>
    NewIdentity,

    /// <summary>Not bound, binding open: the pairing code.</summary>
    BindingOpen,

    /// <summary>Not bound, binding closed: Allow binding.</summary>
    BindingClosed,

    /// <summary>Bound: the server, the site and whether it is reached.</summary>
    Bound,
}

/// <summary>
/// The one window of the OADM Site Node app (docs/sites.md, "App"): server, binding and pairing code, site, networks,
/// ports and requests, relays and connections, the connection log, Reset. Reads the service's state every 2 seconds;
/// every change and the pairing code go through <see cref="IElevation"/> unless the app runs as an administrator.
/// </summary>
public sealed partial class NodeAppViewModel : ValidatingViewModel
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    /// <summary>"The node forgets OADM-SRV and gets a new identity. Bind it again in OADM."</summary>
    public static string ResetMessage(string server) => $"The node forgets {server} and gets a new identity. Bind it again in OADM.";

    private readonly INodeChannel _channel;
    private readonly IElevation _elevation;
    private readonly IAppDialogs _dialogs;
    private readonly TimeProvider _time;
    private string _cardsKey = "";
    private string _listsKey = "";
    private bool _serverEdited;

    public NodeAppViewModel(INodeChannel channel, IElevation elevation, IAppDialogs dialogs, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(elevation);
        ArgumentNullException.ThrowIfNull(dialogs);
        _channel = channel;
        _elevation = elevation;
        _dialogs = dialogs;
        _time = time ?? TimeProvider.System;
        Validation.Rule(nameof(ServerAddress), () =>
            IsConnectMode && ServerAddress.Trim().Length == 0 ? "Enter the server's name or IP address."
            : ServerAddress.Trim().Length > 0 && Admin.ServerAddress.Parse(ServerAddress, 5081) is null ? "Enter a name or IP address, like oadm.example.com or 10.0.0.5."
            : null);
        Validation.Rule(nameof(NewSubnet), () => NewSubnet.Trim().Length > 0 && Subnet.Parse(NewSubnet) is null ? "Enter a network like 10.20.0.0/16." : null);
        Validation.Rule(nameof(NewPort), () => NewPort.Trim().Length > 0 && Port() is null ? "Enter a port from 1 to 65535." : null);
        NewPortUse = PortUses[0];
        NewPortProtocol = PortProtocols[0];
    }

    public IReadOnlyList<Choice<PortUse>> PortUses { get; } = [new(PortUse.Connect, "Connect to"), new(PortUse.Listen, "Listen on")];

    public IReadOnlyList<Choice<PortProtocol>> PortProtocols { get; } = [new(PortProtocol.Tcp, "TCP"), new(PortProtocol.Udp, "UDP")];

    public ObservableCollection<CardItem> Cards { get; } = [];

    public ObservableCollection<SubnetItem> ExtraSubnets { get; } = [];

    public ObservableCollection<PortItem> ConnectPorts { get; } = [];

    public ObservableCollection<PortItem> ListenPorts { get; } = [];

    public ObservableCollection<PortItem> RequestedPorts { get; } = [];

    public ObservableCollection<RelayItem> Relays { get; } = [];

    public ObservableCollection<LogRow> Log { get; } = [];

    /// <summary>The last state read; null while the service does not answer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasState), nameof(IsBound), nameof(IsBindingOpen), nameof(IsBindingClosed), nameof(BindingText),
        nameof(SiteText), nameof(IdText), nameof(VersionText), nameof(ConnectionsText), nameof(HasNewIdentity), nameof(NewIdentityText),
        nameof(WrongCodesText), nameof(IsConnected), nameof(ConnectionText), nameof(HasConnectionProblem), nameof(HasRequests),
        nameof(HasRelays), nameof(AllCards), nameof(HasNoListenPorts))]
    public partial NodeView? State { get; private set; }

    /// <summary>"The OADM Site Node service is not running."</summary>
    [ObservableProperty]
    public partial string? ServiceError { get; private set; }

    [ObservableProperty]
    public partial string? PairingCode { get; private set; }

    /// <summary>The tab shown below the header.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusTab), nameof(IsNetworksTab), nameof(IsPortsTab), nameof(IsActivityTab))]
    public partial NodeTab Tab { get; set; }

    public bool IsStatusTab => Tab == NodeTab.Status;

    public bool IsNetworksTab => Tab == NodeTab.Networks;

    public bool IsPortsTab => Tab == NodeTab.Ports;

    public bool IsActivityTab => Tab == NodeTab.Activity;

    /// <summary>What the header shows now (<see cref="NodeHeader"/>).</summary>
    public NodeHeader Header => State switch
    {
        null => NodeHeader.NoService,
        { NewServerIdentity.Length: > 0 } => NodeHeader.NewIdentity,
        { Binding: NodeBinding.Bound } => NodeHeader.Bound,
        { Binding: NodeBinding.Closed } => NodeHeader.BindingClosed,
        { Mode: NodeLinkMode.Connect } s when s.ServerAddress.Trim().Length == 0 || HasConnectionProblem => NodeHeader.NeedsServer,
        _ => NodeHeader.BindingOpen,
    };

    public bool IsNoServiceHeader => Header == NodeHeader.NoService;

    public bool IsServerHeader => Header == NodeHeader.NeedsServer;

    public bool IsIdentityHeader => Header == NodeHeader.NewIdentity;

    public bool IsOpenHeader => Header == NodeHeader.BindingOpen;

    public bool IsClosedHeader => Header == NodeHeader.BindingClosed;

    public bool IsBoundHeader => Header == NodeHeader.Bound;

    /// <summary>The state in one plain line (the bound state is <see cref="BoundChipText"/>).</summary>
    public string HeadlineText => Header switch
    {
        NodeHeader.NoService => ServiceError ?? AdminTexts.ServiceNotRunning,
        NodeHeader.NeedsServer => State!.ServerAddress.Trim().Length == 0 ? "Not connected. Enter the address of the OADM server." : "Not connected to OADM.",
        NodeHeader.NewIdentity => NewIdentityText,
        NodeHeader.BindingOpen => PairingCode is { Length: > 0 } ? "Type this code at Bind on the Sites page of OADM." : "Not bound. Show the pairing code to bind this node in OADM.",
        NodeHeader.BindingClosed => "Not bound. Binding is closed.",
        _ => "",
    };

    /// <summary>"Binding open for 23 more hours." while binding is open.</summary>
    public string BindingTimeText => State is { Binding: NodeBinding.Open } s ? $"Binding open for {Hours(s.BindingHoursLeft)}." : "";

    /// <summary>"Connected to OADM-SRV · Building B", or "Not connected to OADM-SRV · Building B" while it is not reached.</summary>
    public string BoundChipText
    {
        get
        {
            if (State?.Bound is not { } bound)
                return "";
            var server = bound.SiteName is { Length: > 0 } site ? $"{bound.Name} · {site}" : bound.Name;
            return IsConnected ? $"Connected to {server}" : $"Not connected to {server}";
        }
    }

    /// <summary>Why the bound server is not reached ("Cannot reach 10.0.0.5:5081"); null while connected.</summary>
    public string? BoundChipDetail => State?.Bound is not null && !IsConnected && ConnectionText.Length > 0 ? ConnectionText : null;

    /// <summary>The connection problem below the address field of the header ("Cannot reach 10.0.0.5:5081").</summary>
    public string? ConnectionProblem => HasConnectionProblem ? ConnectionText : null;

    /// <summary>The Status tab shows the server form unless the header shows it.</summary>
    public bool ShowServerFormInTab => HasState && Header != NodeHeader.NeedsServer;

    /// <summary>"Connect" while the node connects to the server, else "Save".</summary>
    public string SaveServerText => IsConnectMode ? "Connect" : "Save";

    /// <summary>Requests of the server that wait (the count badge of the Ports tab).</summary>
    public int RequestCount => RequestedPorts.Count;

    /// <summary>"The server asks for 2 ports."</summary>
    public string RequestsText => RequestCount == 1 ? "The server asks for 1 port." : $"The server asks for {RequestCount} ports.";

    /// <summary>A refusal of a binding action (below the binding card's buttons).</summary>
    [ObservableProperty]
    public partial string? BindingError { get; private set; }

    /// <summary>A refusal of another change (below the card it came from is not known: below the toolbar).</summary>
    [ObservableProperty]
    public partial string? ActionError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaitMode), nameof(SaveServerText))]
    public partial bool IsConnectMode { get; set; } = true;

    public bool IsWaitMode
    {
        get => !IsConnectMode;
        set => IsConnectMode = !value;
    }

    [ObservableProperty]
    public partial string ServerAddress { get; set; } = "";

    [ObservableProperty]
    public partial string NewSubnet { get; set; } = "";

    [ObservableProperty]
    public partial Choice<PortUse>? NewPortUse { get; set; }

    [ObservableProperty]
    public partial Choice<PortProtocol>? NewPortProtocol { get; set; }

    [ObservableProperty]
    public partial string NewPort { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveServer))]
    public partial bool IsBusy { get; private set; }

    public bool HasState => State is not null;

    public bool IsBound => State?.Binding == NodeBinding.Bound;

    public bool IsBindingOpen => State?.Binding == NodeBinding.Open;

    public bool IsBindingClosed => State?.Binding == NodeBinding.Closed;

    public bool AllCards
    {
        get => State?.AllCards ?? false;
        set
        {
            if (State is not null && value != State.AllCards)
                _ = ChangeAsync(AdminRequest.Of(AdminCommands.AllCards, ("on", value ? "true" : "false")));
        }
    }

    /// <summary>"Not bound. Binding open for 23 more hours." / "Binding closed." / "Bound to OADM-SRV since 2026-10-19 08:00".</summary>
    public string BindingText => State switch
    {
        null => "",
        { Binding: NodeBinding.Bound, Bound: { } bound } => $"Bound to {bound.Name} since {bound.BoundUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}",
        { Binding: NodeBinding.Closed } => "Binding closed.",
        var s => $"Not bound. Binding open for {Hours(s.BindingHoursLeft)}.",
    };

    public string SiteText => State?.Bound?.SiteName is { Length: > 0 } site ? site : "Not bound yet";

    public string IdText => State?.NodeId ?? "";

    public string VersionText => State?.Version ?? "";

    /// <summary>"3 short, 12 permanent".</summary>
    public string ConnectionsText => State is null ? "" : $"{State.ShortConnections} short, {State.PermanentConnections} permanent";

    public bool HasNewIdentity => State?.NewServerIdentity.Length > 0;

    public string NewIdentityText => !HasNewIdentity
        ? ""
        : State!.NewServerAccepted
            ? $"Type the pairing code at Bind on the Sites page of the OADM server with the new identity ({Fingerprint(State.NewServerIdentity)})."
            : $"{State.Bound?.Name}{(State.NewServerFrom.Length > 0 ? " at " + State.NewServerFrom : "")} presents a new identity ({Fingerprint(State.NewServerIdentity)}).";

    /// <summary>"2 wrong pairing codes, the last from 10.0.0.9 at 14:02." plus the lock.</summary>
    public string? WrongCodesText
    {
        get
        {
            if (State is null || State.WrongCodes.Count == 0)
                return null;
            var last = State.WrongCodes[^1];
            var text = $"{State.WrongCodes.Count} wrong pairing {(State.WrongCodes.Count == 1 ? "code" : "codes")}, the last from {last.From} at {last.Time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}.";
            return State.LockedUntil is { } until
                ? text + $" Binding refused until {until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}."
                : text;
        }
    }

    public bool IsConnected => State?.Connected ?? false;

    public string ConnectionText => State?.ConnectionText ?? "";

    public bool HasConnectionProblem => State is not null && !State.Connected && State.ConnectionText.StartsWith("Cannot", StringComparison.Ordinal);

    public bool HasRequests => RequestedPorts.Count > 0;

    public bool HasRelays => Relays.Count > 0;

    public bool HasNoListenPorts => ListenPorts.Count == 0;

    /// <summary>Why Save is disabled (the address field's error), null when it is not.</summary>
    public string? SaveServerBlockedReason => ErrorOf(nameof(ServerAddress));

    public bool CanSaveServer => SaveServerBlockedReason is null && !IsBusy;

    public string? AddSubnetBlockedReason => ErrorOf(nameof(NewSubnet));

    public string? AddPortBlockedReason => ErrorOf(nameof(NewPort));

    protected override void OnValidationChanged()
    {
        base.OnValidationChanged();
        OnPropertyChanged(nameof(SaveServerBlockedReason));
        OnPropertyChanged(nameof(CanSaveServer));
        OnPropertyChanged(nameof(AddSubnetBlockedReason));
        OnPropertyChanged(nameof(AddPortBlockedReason));
    }

    /// <summary>Reads the state (and keeps the user's unsaved server address).</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var response = await _channel.SendAsync(AdminRequest.Of(AdminCommands.State), CancellationToken.None).ConfigureAwait(true);
        if (!response.Ok || response.State is null)
        {
            ServiceError = response.Error.Length > 0 ? response.Error : AdminTexts.ServiceNotRunning;
            State = null;
            return;
        }

        ServiceError = null;
        Apply(response.State);
    }

    /// <summary>Refreshes every 2 seconds until <paramref name="ct"/> ends (the window closes).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(RefreshInterval, _time);
        try
        {
            await RefreshAsync().ConfigureAwait(true);
            await LoadLogAsync().ConfigureAwait(true);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(true))
                await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    public async Task LoadLogAsync()
    {
        var response = await _channel.SendAsync(AdminRequest.Of(AdminCommands.Log, ("count", "10000")), CancellationToken.None).ConfigureAwait(true);
        if (!response.Ok)
            return;
        Log.Clear();
        foreach (var entry in response.Log)
            Log.Add(LogRow.From(entry));
    }

    [RelayCommand]
    public async Task SaveServerAsync()
    {
        Validation.ShowAll(nameof(ServerAddress));
        if (!Validation.IsValidFor(nameof(ServerAddress)))
            return;
        var response = await ChangeAsync(AdminRequest.Of(AdminCommands.SetServer, ("mode", IsConnectMode ? "connect" : "wait"), ("address", ServerAddress.Trim()))).ConfigureAwait(true);
        if (response.Ok)
        {
            _serverEdited = false;
            Validation.Reset(nameof(ServerAddress));
        }
        else if (response.Error != SystemElevation.Cancelled)
        {
            Validation.SetServerError(nameof(ServerAddress), response.Error);
            ActionError = null;
        }
    }

    [RelayCommand]
    public async Task ShowPairingCodeAsync()
    {
        BindingError = null;
        var response = await SendAsAdministratorAsync(AdminRequest.Of(AdminCommands.PairingCode)).ConfigureAwait(true);
        if (response.Ok)
            PairingCode = response.PairingCode;
        else if (response.Error != SystemElevation.Cancelled)
            BindingError = response.Error;
    }

    [RelayCommand]
    public async Task AllowBindingAsync()
    {
        BindingError = null;
        PairingCode = null;
        var response = await ChangeAsync(AdminRequest.Of(AdminCommands.AllowBinding)).ConfigureAwait(true);
        if (!response.Ok && response.Error != SystemElevation.Cancelled)
            BindingError = response.Error;
    }

    [RelayCommand]
    public async Task AcceptServerAsync()
    {
        // Accepting lets the new server bind with the pairing code; the app shows the code.
        var response = await ChangeAsync(AdminRequest.Of(AdminCommands.AcceptServer)).ConfigureAwait(true);
        if (response.Ok && response.PairingCode.Length > 0)
            PairingCode = response.PairingCode;
    }

    [RelayCommand]
    public async Task AddSubnetAsync()
    {
        Validation.ShowAll(nameof(NewSubnet));
        if (NewSubnet.Trim().Length == 0 || !Validation.IsValidFor(nameof(NewSubnet)))
            return;
        var response = await ChangeAsync(AdminRequest.Of(AdminCommands.AddSubnet, ("subnet", NewSubnet.Trim()))).ConfigureAwait(true);
        if (response.Ok)
        {
            NewSubnet = "";
            Validation.Reset(nameof(NewSubnet));
        }
        else if (response.Error != SystemElevation.Cancelled)
        {
            Validation.SetServerError(nameof(NewSubnet), response.Error);
            ActionError = null;
        }
    }

    [RelayCommand]
    public Task RemoveSubnetAsync(SubnetItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ChangeAsync(AdminRequest.Of(AdminCommands.RemoveSubnet, ("subnet", item.Subnet)));
    }

    [RelayCommand]
    public async Task AddPortAsync()
    {
        Validation.ShowAll(nameof(NewPort));
        if (Port() is not { } port)
            return;
        var response = await ChangeAsync(PortRequest(AdminCommands.AddPort, new PortEntry(NewPortUse?.Value ?? PortUse.Connect, NewPortProtocol?.Value ?? PortProtocol.Tcp, port))).ConfigureAwait(true);
        if (response.Ok)
        {
            NewPort = "";
            Validation.Reset(nameof(NewPort));
        }
        else if (response.Error != SystemElevation.Cancelled)
        {
            Validation.SetServerError(nameof(NewPort), response.Error);
            ActionError = null;
        }
    }

    [RelayCommand]
    public Task RemovePortAsync(PortItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ChangeAsync(PortRequest(AdminCommands.RemovePort, item.Entry));
    }

    [RelayCommand]
    public Task AllowRequestAsync(PortItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ChangeAsync(PortRequest(AdminCommands.AllowRequest, item.Entry));
    }

    [RelayCommand]
    public Task DismissRequestAsync(PortItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ChangeAsync(PortRequest(AdminCommands.DismissRequest, item.Entry));
    }

    [RelayCommand]
    public async Task ResetAsync()
    {
        var server = State?.Bound?.Name is { Length: > 0 } name ? name : "the server";
        if (!await _dialogs.ConfirmAsync("Reset", ResetMessage(server), "Reset").ConfigureAwait(true))
            return;
        PairingCode = null;
        await ChangeAsync(AdminRequest.Of(AdminCommands.Reset)).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ShowStatus() => Tab = NodeTab.Status;

    [RelayCommand]
    private void ShowNetworks() => Tab = NodeTab.Networks;

    /// <summary>The Ports tab (also "Review" of the header's request line).</summary>
    [RelayCommand]
    private void ShowPorts() => Tab = NodeTab.Ports;

    [RelayCommand]
    private void ShowActivity() => Tab = NodeTab.Activity;

    [RelayCommand]
    private void ChooseConnect() => IsConnectMode = true;

    [RelayCommand]
    private void ChooseWait() => IsConnectMode = false;

    partial void OnServerAddressChanged(string value) => _serverEdited = true;

    partial void OnStateChanged(NodeView? value) => RaiseHeader();

    partial void OnServiceErrorChanged(string? value) => RaiseHeader();

    partial void OnPairingCodeChanged(string? value) => RaiseHeader();

    private void RaiseHeader()
    {
        foreach (var name in HeaderProperties)
            OnPropertyChanged(name);
    }

    private static readonly string[] HeaderProperties =
    [
        nameof(Header), nameof(IsNoServiceHeader), nameof(IsServerHeader), nameof(IsIdentityHeader), nameof(IsOpenHeader),
        nameof(IsClosedHeader), nameof(IsBoundHeader), nameof(HeadlineText), nameof(BindingTimeText), nameof(BoundChipText), nameof(BoundChipDetail),
        nameof(ConnectionProblem), nameof(ShowServerFormInTab),
    ];

    partial void OnIsConnectModeChanged(bool value) => _serverEdited = true;

    private async Task<AdminResponse> ChangeAsync(AdminRequest request)
    {
        ActionError = null;
        var response = await SendAsAdministratorAsync(request).ConfigureAwait(true);
        if (!response.Ok && response.Error != SystemElevation.Cancelled)
            ActionError = response.Error;
        if (response.State is not null)
            Apply(response.State);
        else
            await RefreshAsync().ConfigureAwait(true);
        return response;
    }

    private async Task<AdminResponse> SendAsAdministratorAsync(AdminRequest request)
    {
        IsBusy = true;
        try
        {
            return _elevation.IsAdministrator
                ? await _channel.SendAsync(request, CancellationToken.None).ConfigureAwait(true)
                : await _elevation.RunAsync(request, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(NodeView view)
    {
        var before = State;
        State = view;
        var pairing = view.Binding == NodeBinding.Open || view is { NewServerAccepted: true, NewServerIdentity.Length: > 0 };
        if (!pairing || before?.NodeId != view.NodeId)
            PairingCode = null; // a new code after Reset; none once bound (unless a new server identity was accepted) or closed
        if (!_serverEdited)
        {
            ServerAddress = view.ServerAddress;
            IsConnectMode = view.Mode == NodeLinkMode.Connect;
            _serverEdited = false;
        }

        var cardsKey = string.Join('|', view.Cards.Select(c => c.Id));
        if (cardsKey != _cardsKey)
        {
            _cardsKey = cardsKey;
            Cards.Clear();
            foreach (var card in view.Cards)
                Cards.Add(new CardItem(card, SetCardAsync));
        }
        else
        {
            foreach (var (item, card) in Cards.Zip(view.Cards))
                item.Apply(card);
        }

        var listsKey = string.Join('|', view.ExtraSubnets) + "#" + string.Join('|', view.Allowed.Select(p => p.ToString()))
            + "#" + string.Join('|', view.Requested.Select(p => p.ToString())) + "#" + string.Join('|', view.Relays.Select(r => r.ToString()));
        if (listsKey == _listsKey)
            return;
        _listsKey = listsKey;
        Replace(ExtraSubnets, view.ExtraSubnets.Select(s => new SubnetItem(s)));
        Replace(ConnectPorts, view.Allowed.Where(p => p.Use == PortUse.Connect).OrderBy(p => p.Port).Select(PortItem.Allowed));
        Replace(ListenPorts, view.Allowed.Where(p => p.Use == PortUse.Listen).OrderBy(p => p.Port).Select(PortItem.Allowed));
        Replace(RequestedPorts, view.Requested.Select(PortItem.Requested));
        Replace(Relays, view.Relays.Select(RelayItem.From));
        OnPropertyChanged(nameof(HasRequests));
        OnPropertyChanged(nameof(RequestCount));
        OnPropertyChanged(nameof(RequestsText));
        OnPropertyChanged(nameof(HasRelays));
        OnPropertyChanged(nameof(HasNoListenPorts));
    }

    private async Task SetCardAsync(CardItem card, bool reached) =>
        await ChangeAsync(AdminRequest.Of(AdminCommands.SetCard, ("id", card.Id), ("reached", reached ? "true" : "false"))).ConfigureAwait(true);

    private int? Port() =>
        int.TryParse(NewPort.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;

    private static AdminRequest PortRequest(string command, PortEntry port) => AdminRequest.Of(command,
        ("use", port.Use == PortUse.Listen ? "listen" : "connect"),
        ("protocol", port.Protocol == PortProtocol.Udp ? "udp" : "tcp"),
        ("port", port.Port.ToString(CultureInfo.InvariantCulture)),
        ("reason", port.Reason));

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private static string Hours(double hours)
    {
        var whole = (int)Math.Ceiling(hours);
        return whole == 1 ? "1 more hour" : $"{whole} more hours";
    }

    /// <summary>"AB12 CD34 ..." in groups of four, the first 32 characters.</summary>
    private static string Fingerprint(string hex) =>
        string.Join(' ', Enumerable.Range(0, Math.Min(hex.Length, 32) / 4).Select(i => hex.Substring(i * 4, 4)));
}
