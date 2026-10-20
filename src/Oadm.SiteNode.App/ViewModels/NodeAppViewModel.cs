using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.App.Services;

namespace Oadm.SiteNode.App.ViewModels;

/// <summary>Confirmation popups of the window (ui:MessageWindow); a fake in tests.</summary>
public interface IAppDialogs
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
}

/// <summary>The tabs of the window.</summary>
public enum NodeTab
{
    General,
    Networks,
    Ports,
    Activity,
}

/// <summary>How the node status line below the tabs is colored.</summary>
public enum NodeStatusKind
{
    Neutral,
    Ok,
    Warning,
    Error,
}

/// <summary>
/// The one window of the OADM Site Node app (docs/sites.md, "App"): server, binding and pairing code, site, networks,
/// ports and requests, relays and connections, the connection log, Reset. Reads the service's state every 2 seconds;
/// every change and the pairing code go through <see cref="IElevation"/> unless the app runs as an administrator.
/// </summary>
public sealed partial class NodeAppViewModel : ValidatingViewModel
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    /// <summary>What to do first on a new node: the hint of the empty address field.</summary>
    public const string EnterServer = "Enter the address of the OADM server.";

    /// <summary>The hint of the address field once an address is saved.</summary>
    public const string PortHint = "Port 5081 unless you type another.";
    /// <summary>"The node forgets OADM-SRV and gets a new identity. Bind it again in OADM."</summary>
    public static string ResetMessage(string server) => $"The node forgets {server} and gets a new identity. Bind it again in OADM.";

    private readonly INodeChannel _channel;
    private readonly IElevation _elevation;
    private readonly IAppDialogs _dialogs;
    private readonly TimeProvider _time;
    private string _cardsKey = "";
    private string _listsKey = "";
    private bool _serverEdited;
    private int _busy;
    private int _failedReads;
    private string? _lastNodeId;
    private long? _lastCodeGeneration;

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
            ServerAddress.Trim().Length == 0 ? EnterServer
            : Admin.ServerAddress.Parse(ServerAddress, 5081) is null ? "Enter a name or IP address, like oadm.example.com or 10.0.0.5."
            : null);
        Validation.Rule(nameof(NewSubnet), () => NewSubnet.Trim().Length > 0 && Subnet.Parse(NewSubnet) is null ? "Enter a network like 10.20.0.0/16." : null);
        Validation.Rule(nameof(NewPort), () => NewPort.Trim().Length > 0 && Port() is null ? "Enter a port from 1 to 65535." : null);
        NewPortUse = PortUses[0];
        NewPortProtocol = PortProtocols[0];
    }

    public IReadOnlyList<Choice<PortUse>> PortUses { get; } = [new(PortUse.Connect, "Connect to"), new(PortUse.Listen, "Listen on")];

    public IReadOnlyList<Choice<PortProtocol>> PortProtocols { get; } = [new(PortProtocol.Tcp, "TCP"), new(PortProtocol.Udp, "UDP")];

    public RangeObservableCollection<CardItem> Cards { get; } = [];

    public RangeObservableCollection<SubnetItem> ExtraSubnets { get; } = [];

    public RangeObservableCollection<PortItem> ConnectPorts { get; } = [];

    public RangeObservableCollection<PortItem> ListenPorts { get; } = [];

    public RangeObservableCollection<PortItem> RequestedPorts { get; } = [];

    public RangeObservableCollection<RelayItem> Relays { get; } = [];

    public RangeObservableCollection<LogRow> Log { get; } = [];

    /// <summary>The last state read; null while the service does not answer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasState), nameof(IsBound), nameof(IsBindingOpen), nameof(IsBindingClosed), nameof(BindingText),
        nameof(SiteText), nameof(HasSite), nameof(IdText), nameof(NodeInfoText), nameof(ConnectionsText), nameof(HasNewIdentity), nameof(NewIdentityText),
        nameof(WrongCodesText), nameof(IsConnected), nameof(ConnectionText), nameof(HasConnectionProblem), nameof(HasRequests),
        nameof(HasRelays), nameof(AllCards), nameof(HasNoListenPorts), nameof(HasServerAddress), nameof(ServerHint))]
    public partial NodeView? State { get; private set; }

    /// <summary>"The OADM Site Node service is not running."</summary>
    [ObservableProperty]
    public partial string? ServiceError { get; private set; }

    [ObservableProperty]
    public partial string? PairingCode { get; private set; }

    /// <summary>The tab shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralTab), nameof(IsNetworksTab), nameof(IsPortsTab), nameof(IsActivityTab))]
    public partial NodeTab Tab { get; set; }

    public bool IsGeneralTab => Tab == NodeTab.General;

    public bool IsNetworksTab => Tab == NodeTab.Networks;

    public bool IsPortsTab => Tab == NodeTab.Ports;

    public bool IsActivityTab => Tab == NodeTab.Activity;

    /// <summary>
    /// The status line of the Node status block below the tabs: "Connected to OADM-SRV", "Not connected: Cannot reach
    /// 10.0.0.5:5081", "No server address yet", "Service not running". The site has a line of its own (<see cref="SiteText"/>).
    /// </summary>
    public string NodeStatusText => NodeStatus.Text;

    public NodeStatusKind NodeStatusKind => NodeStatus.Kind;

    public bool IsNodeStatusOk => NodeStatusKind == NodeStatusKind.Ok;

    public bool IsNodeStatusWarning => NodeStatusKind == NodeStatusKind.Warning;

    public bool IsNodeStatusError => NodeStatusKind == NodeStatusKind.Error;

    private (string Text, NodeStatusKind Kind) NodeStatus
    {
        get
        {
            if (State is not { } s)
                return (ServiceError is null or AdminTexts.ServiceNotRunning ? "Service not running" : ServiceError, NodeStatusKind.Error);
            if (s.Connected)
                return (s.Bound is { } bound ? $"Connected to {bound.Name}" : s.ConnectionText.Length > 0 ? s.ConnectionText : "Connected", NodeStatusKind.Ok);
            if (s.ServerAddress.Length == 0)
                return ("No server address yet", NodeStatusKind.Warning);
            if (s.Binding == NodeBinding.Closed)
                return ("Not connected", NodeStatusKind.Warning); // the binding line says why
            return s.ConnectionText.Length > 0
                ? ("Not connected: " + s.ConnectionText, HasConnectionProblem ? NodeStatusKind.Error : NodeStatusKind.Warning)
                : ("Not connected", NodeStatusKind.Warning);
        }
    }

    /// <summary>
    /// The binding line of the Node status block: "Bound since 2026-10-19 08:00" (the status line names the server),
    /// "Not bound. Binding open for 23 more hours.", "Binding is closed.".
    /// </summary>
    public string BindingText => State switch
    {
        null => "",
        { Binding: NodeBinding.Bound, Bound: { } bound } => $"Bound since {bound.BoundUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}",
        { Binding: NodeBinding.Closed } => "Binding is closed.",
        var s => $"Not bound. Binding open for {Hours(s.BindingHoursLeft)}.",
    };

    /// <summary>Show pairing code: binding is open (or a new server identity was accepted) and no code is shown yet.</summary>
    public bool CanShowPairingCode => PairingCode is not { Length: > 0 } && IsPairing;

    /// <summary>Binding is open, or a new server identity was accepted: the server binds with the pairing code.</summary>
    public bool IsPairing => IsBindingOpen || State is { NewServerAccepted: true, NewServerIdentity.Length: > 0 };

    /// <summary>
    /// The pairing code part of the General tab: once a server address is saved and the node is not bound (or a new server
    /// identity was accepted). Before an address the field is the one thing to do.
    /// </summary>
    public bool ShowPairing => HasServerAddress && (IsPairing || IsBindingClosed);

    public bool HasPairingCode => PairingCode is { Length: > 0 };

    /// <summary>Accept new server identity: the bound server presents a new identity that is not accepted yet.</summary>
    public bool CanAcceptServer => State is { NewServerAccepted: false, NewServerIdentity.Length: > 0 };

    /// <summary>The connection problem below the address field ("Cannot reach 10.0.0.5:5081").</summary>
    public string? ConnectionProblem => HasConnectionProblem ? ConnectionText : null;

    /// <summary>The service has a server address (saved).</summary>
    public bool HasServerAddress => State?.ServerAddress.Length > 0;

    /// <summary>The address field's hint: what to do first on a new node, else the port.</summary>
    public string ServerHint => State is not null && !HasServerAddress ? EnterServer + " " + PortHint : PortHint;

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
    public partial string ServerAddress { get; set; } = "";

    [ObservableProperty]
    public partial string NewSubnet { get; set; } = "";

    [ObservableProperty]
    public partial Choice<PortUse>? NewPortUse { get; set; }

    [ObservableProperty]
    public partial Choice<PortProtocol>? NewPortProtocol { get; set; }

    [ObservableProperty]
    public partial string NewPort { get; set; } = "";

    /// <summary>A change runs (often waiting for the administrator prompt): every change button and check box is off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveServer), nameof(SaveServerBlockedReason), nameof(CanChange))]
    [NotifyCanExecuteChangedFor(nameof(SaveServerCommand), nameof(ShowPairingCodeCommand), nameof(AllowBindingCommand), nameof(AcceptServerCommand),
        nameof(AddSubnetCommand), nameof(RemoveSubnetCommand), nameof(AddPortCommand), nameof(RemovePortCommand), nameof(AllowRequestCommand),
        nameof(DismissRequestCommand), nameof(ResetCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>No change runs: the change buttons and check boxes are on.</summary>
    public bool CanChange => !IsBusy;

    public bool HasState => State is not null;

    public bool IsBound => State?.Binding == NodeBinding.Bound;

    public bool IsBindingOpen => State?.Binding == NodeBinding.Open;

    public bool IsBindingClosed => State?.Binding == NodeBinding.Closed;

    /// <summary>
    /// All cards (the default) of the Networks tab's choice: ticking it clears the chosen cards; choosing a card unticks it.
    /// Unticking it without a chosen card changes nothing (no chosen card means all cards).
    /// </summary>
    public bool AllCards
    {
        get => State?.AllCards ?? true;
        set
        {
            if (State is not null && value != State.AllCards && CanChange)
                _ = ChangeAsync(AdminRequest.Of(AdminCommands.AllCards, ("on", value ? "true" : "false")));
            else
                OnPropertyChanged();
        }
    }

    /// <summary>"Site Building B" below the status line once the server named the site; empty before.</summary>
    public string SiteText => State is { Binding: NodeBinding.Bound, Bound.SiteName: { Length: > 0 } site } ? "Site " + site : "";

    public bool HasSite => SiteText.Length > 0;

    /// <summary>The whole node ID (the tooltip of <see cref="NodeInfoText"/>).</summary>
    public string IdText => State?.NodeId ?? "";

    /// <summary>"Node ID 1457a0b5… · Version 1.0.0", small and grey at the end of the Node status block.</summary>
    public string NodeInfoText => State is not { } s
        ? ""
        : $"Node ID {(s.NodeId.Length > 8 ? s.NodeId[..8] + "…" : s.NodeId)} · Version {s.Version}";

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

    /// <summary>The address field differs from the service's server address.</summary>
    public bool HasUnsavedChanges => State is { } s && !string.Equals(ServerAddress.Trim(), s.ServerAddress.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Why Save is disabled (the address field's error, or nothing changed), null when it is not.</summary>
    public string? SaveServerBlockedReason => ErrorOf(nameof(ServerAddress)) ?? (HasUnsavedChanges ? null : "No changes to save.");

    /// <summary>Save of the footer: the one accent button of the window.</summary>
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
            // One failed read is tolerated (the service was busy for a moment): the window keeps the last state and code.
            if (++_failedReads < 2 && State is not null)
                return;
            ServiceError = response.Error.Length > 0 ? response.Error : AdminTexts.ServiceNotRunning;
            State = null;
            return;
        }

        _failedReads = 0;
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
        Log.ReplaceAll(response.Log.Select(LogRow.From));
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    public async Task SaveServerAsync()
    {
        Validation.ShowAll(nameof(ServerAddress));
        if (!Validation.IsValidFor(nameof(ServerAddress)) || !HasUnsavedChanges)
            return;
        var response = await ChangeAsync(AdminRequest.Of(AdminCommands.SetServer, ("address", ServerAddress.Trim()))).ConfigureAwait(true);
        if (response.Ok)
        {
            _serverEdited = false;
            Validation.Reset(nameof(ServerAddress));
            // The save ran as an administrator: the answer carries the pairing code while the node is not bound.
            if (response.PairingCode.Length > 0)
                PairingCode = response.PairingCode;
        }
        else if (response.Error != SystemElevation.Cancelled)
        {
            Validation.SetServerError(nameof(ServerAddress), response.Error);
            ActionError = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    public async Task ShowPairingCodeAsync()
    {
        BindingError = null;
        var response = await SendAsAdministratorAsync(AdminRequest.Of(AdminCommands.PairingCode)).ConfigureAwait(true);
        if (response.State is not null)
            Apply(response.State); // the code belongs to this state: a later read with the same generation keeps it
        if (response.Ok)
            PairingCode = response.PairingCode;
        else if (response.Error != SystemElevation.Cancelled)
            BindingError = response.Error;
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    public async Task AllowBindingAsync()
    {
        BindingError = null;
        PairingCode = null;
        var response = await ChangeAsync(AdminRequest.Of(AdminCommands.AllowBinding)).ConfigureAwait(true);
        if (!response.Ok && response.Error != SystemElevation.Cancelled)
            BindingError = response.Error;
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    public async Task AcceptServerAsync()
    {
        // Accepting lets the new server bind with the pairing code; the app shows the code.
        var response = await ChangeAsync(AdminRequest.Of(AdminCommands.AcceptServer)).ConfigureAwait(true);
        if (response.Ok && response.PairingCode.Length > 0)
            PairingCode = response.PairingCode;
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
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

    [RelayCommand(CanExecute = nameof(CanChange))]
    public Task RemoveSubnetAsync(SubnetItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ChangeAsync(AdminRequest.Of(AdminCommands.RemoveSubnet, ("subnet", item.Subnet)));
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
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

    [RelayCommand(CanExecute = nameof(CanChange))]
    public Task RemovePortAsync(PortItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ChangeAsync(PortRequest(AdminCommands.RemovePort, item.Entry));
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    public Task AllowRequestAsync(PortItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ChangeAsync(PortRequest(AdminCommands.AllowRequest, item.Entry));
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    public Task DismissRequestAsync(PortItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ChangeAsync(PortRequest(AdminCommands.DismissRequest, item.Entry));
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    public async Task ResetAsync()
    {
        var server = State?.Bound?.Name is { Length: > 0 } name ? name : "the server";
        if (!await _dialogs.ConfirmAsync("Reset", ResetMessage(server), "Reset").ConfigureAwait(true))
            return;
        PairingCode = null;
        await ChangeAsync(AdminRequest.Of(AdminCommands.Reset)).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ShowGeneral() => Tab = NodeTab.General;

    [RelayCommand]
    private void ShowNetworks() => Tab = NodeTab.Networks;

    /// <summary>The Ports tab (also "Review" of the request line below the card).</summary>
    [RelayCommand]
    private void ShowPorts() => Tab = NodeTab.Ports;

    [RelayCommand]
    private void ShowActivity() => Tab = NodeTab.Activity;

    /// <summary>Closes the window (Close of the footer).</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    partial void OnServerAddressChanged(string value)
    {
        _serverEdited = true;
        RaiseSave();
    }

    partial void OnStateChanged(NodeView? value) => RaiseState();

    partial void OnServiceErrorChanged(string? value) => RaiseState();

    partial void OnPairingCodeChanged(string? value) => RaiseState();

    private void RaiseSave()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(SaveServerBlockedReason));
        OnPropertyChanged(nameof(CanSaveServer));
    }

    private void RaiseState()
    {
        foreach (var name in StateProperties)
            OnPropertyChanged(name);
        RaiseSave();
    }

    private static readonly string[] StateProperties =
    [
        nameof(NodeStatusText), nameof(NodeStatusKind), nameof(IsNodeStatusOk), nameof(IsNodeStatusWarning), nameof(IsNodeStatusError),
        nameof(CanShowPairingCode), nameof(HasPairingCode), nameof(CanAcceptServer), nameof(ConnectionProblem), nameof(IsPairing), nameof(ShowPairing),
    ];

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
        // A counter, not a flag: a second change that started meanwhile keeps the buttons off until both ended.
        IsBusy = ++_busy > 0;
        try
        {
            return _elevation.IsAdministrator
                ? await _channel.SendAsync(request, CancellationToken.None).ConfigureAwait(true)
                : await _elevation.RunAsync(request, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = --_busy > 0;
        }
    }

    private void Apply(NodeView view)
    {
        State = view;
        var pairing = view.Binding == NodeBinding.Open || view is { NewServerAccepted: true, NewServerIdentity.Length: > 0 };
        // A new code (5 wrong codes, a service restart, Reset) or a new node: the code shown is no longer the node's.
        // Compared with the last state read, so a failed read in between does not count as a change.
        var codeChanged = (_lastCodeGeneration is { } generation && generation != view.PairingCodeGeneration)
            || (_lastNodeId is { } id && id != view.NodeId);
        _lastCodeGeneration = view.PairingCodeGeneration;
        _lastNodeId = view.NodeId;
        if (!pairing || codeChanged)
            PairingCode = null; // none once bound (unless a new server identity was accepted) or closed
        if (!_serverEdited)
        {
            ServerAddress = view.ServerAddress;
            _serverEdited = false;
        }

        var cardsKey = string.Join('|', view.Cards.Select(c => c.Id));
        if (cardsKey != _cardsKey)
        {
            _cardsKey = cardsKey;
            Cards.ReplaceAll(view.Cards.Select(card => new CardItem(card, view.AllCards, SetCardAsync)));
        }
        else
        {
            foreach (var (item, card) in Cards.Zip(view.Cards))
                item.Apply(card, view.AllCards);
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

    private async Task SetCardAsync(CardItem card, bool chosen)
    {
        if (!CanChange || State is null)
        {
            // Another change runs: the check box goes back to the state of the service.
            if (State is not null && State.Cards.FirstOrDefault(c => c.Id == card.Id) is { } view)
                card.Apply(view, State.AllCards);
            return;
        }

        await ChangeAsync(AdminRequest.Of(AdminCommands.SetCard, ("id", card.Id), ("reached", chosen ? "true" : "false"))).ConfigureAwait(true);
    }

    private int? Port() =>
        int.TryParse(NewPort.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;

    private static AdminRequest PortRequest(string command, PortEntry port) => AdminRequest.Of(command,
        ("use", port.Use == PortUse.Listen ? "listen" : "connect"),
        ("protocol", port.Protocol == PortProtocol.Udp ? "udp" : "tcp"),
        ("port", port.Port.ToString(CultureInfo.InvariantCulture)),
        ("reason", port.Reason));

    private static void Replace<T>(RangeObservableCollection<T> target, IEnumerable<T> items) => target.ReplaceAll(items);

    private static string Hours(double hours)
    {
        var whole = (int)Math.Ceiling(hours);
        return whole == 1 ? "1 more hour" : $"{whole} more hours";
    }

    /// <summary>"AB12 CD34 ..." in groups of four, the first 32 characters.</summary>
    private static string Fingerprint(string hex) =>
        string.Join(' ', Enumerable.Range(0, Math.Min(hex.Length, 32) / 4).Select(i => hex.Substring(i * 4, 4)));
}
