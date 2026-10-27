using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.App.Services;

namespace Oadm.SiteNode.App.ViewModels;

/// <summary>Confirmation popups of the window (ui:MessageWindow) and the clipboard; a fake in tests.</summary>
public interface IAppDialogs
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>Puts <paramref name="text"/> on the clipboard.</summary>
    Task CopyAsync(string text);

    /// <summary>Shows the Add port dialog until it closes.</summary>
    Task ShowAddPortAsync(AddPortViewModel dialog);
}

/// <summary>
/// The line between server and node in the picture on top: grey (not linked yet), purple (connected, ready to bind),
/// green (connected and bound), red (not connected, lost, error).
/// </summary>
public enum LinkKind
{
    Idle,
    Ready,
    Connected,
    Error,
}

/// <summary>How the node status line is colored.</summary>
public enum NodeStatusKind
{
    Neutral,
    Ok,
    Warning,
    Error,
}

/// <summary>
/// The one window of the OADM Site Node app (docs/spec/sites.md, The node: Node app): server and pairing code, networks, ports and
/// requests, the node status, Reset. Reads the service's state every 2 seconds (the pairing code is part of it while the
/// node is not bound); every change goes through <see cref="IElevation"/> unless the app runs as an administrator.
/// </summary>
public sealed partial class NodeAppViewModel : ValidatingViewModel
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    /// <summary>What to do first on a new node: the hint of the empty address field.</summary>
    public const string EnterServer = "Enter the address of the OADM server.";

    /// <summary>The hint of the address field once an address is saved.</summary>
    public const string PortHint = "Port 5081 unless you type another.";

    /// <summary>The status below the line while the node is connected and not bound yet.</summary>
    public const string ReadyToBind = "Ready to bind";

    /// <summary>The first sentence below the pairing code.</summary>
    public const string TypeCode = "Type this code at Bind on the Sites page of OADM.";

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
    private long _applied;
    private string? _adminCode;

    public NodeAppViewModel(INodeChannel channel, IElevation elevation, IAppDialogs dialogs, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(elevation);
        ArgumentNullException.ThrowIfNull(dialogs);
        _channel = channel;
        _elevation = elevation;
        _dialogs = dialogs;
        _time = time ?? TimeProvider.System;
        AllCardsChoice = new AllCardsItem(() => AllCards, value => AllCards = value);
        Validation.Rule(nameof(ServerAddress), () =>
            ServerAddress.Trim().Length == 0 ? EnterServer
            : Admin.ServerAddress.Parse(ServerAddress, 5081) is null ? "Enter a name or IP address, like oadm.example.com or 10.0.0.5."
            : null);
    }

    public RangeObservableCollection<CardItem> Cards { get; } = [];

    /// <summary>"All cards", the first entry of the network select.</summary>
    public AllCardsItem AllCardsChoice { get; }

    /// <summary>The entries of the network select: All cards, then every card.</summary>
    public RangeObservableCollection<object> NetworkChoices { get; } = [];

    /// <summary>The ports table: the server's requests first, then the allowed ports (connect before listen, by port).</summary>
    public RangeObservableCollection<PortItem> Ports { get; } = [];

    /// <summary>Relays the server opened that do not run ("UDP 123 is used by another program"): lines of the node status.</summary>
    public RangeObservableCollection<RelayItem> RelayProblems { get; } = [];

    /// <summary>The last state read; null while the service does not answer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasState), nameof(IsBound), nameof(IsBindingOpen), nameof(IsBindingClosed), nameof(BindingText),
        nameof(SiteText), nameof(HasSite), nameof(IdText), nameof(NodeInfoText), nameof(HasNewIdentity), nameof(NewIdentityText),
        nameof(WrongCodesText), nameof(IsConnected), nameof(ConnectionText), nameof(HasConnectionProblem), nameof(AllCards),
        nameof(NetworksText), nameof(HasServerAddress), nameof(ServerHint), nameof(PairingCode), nameof(PairingHint))]
    public partial NodeView? State { get; private set; }

    /// <summary>"The OADM Site Node service is not running."</summary>
    [ObservableProperty]
    public partial string? ServiceError { get; private set; }

    /// <summary>
    /// "ABCD-EFGH": from the state while the node is not bound; for a bound node that accepted a new server identity from an
    /// administrator's answer (Accept, Show pairing code), never from the state everyone reads.
    /// </summary>
    public string? PairingCode => State?.PairingCode is { Length: > 0 } code ? code
        : IsBound && IsPairing && _adminCode is { Length: > 0 } admin ? admin
        : null;

    /// <summary>Show pairing code: a bound node that accepted a new server identity, before the code was read as an administrator.</summary>
    public bool CanShowPairingCode => IsBound && IsPairing && PairingCode is null;

    /// <summary>"Type this code at Bind on the Sites page of OADM. Valid for 8 more minutes."</summary>
    public string PairingHint
    {
        get
        {
            if (State?.PairingCodeValidUntil is not { } until)
                return TypeCode;
            var left = until - _time.GetUtcNow();
            if (left < TimeSpan.FromMinutes(1))
            {
                var seconds = Math.Max(1, (int)Math.Ceiling(left.TotalSeconds));
                return $"{TypeCode} Valid for {seconds} more {(seconds == 1 ? "second" : "seconds")}.";
            }

            var minutes = (int)Math.Ceiling(left.TotalMinutes);
            return $"{TypeCode} Valid for {minutes} more {(minutes == 1 ? "minute" : "minutes")}.";
        }
    }

    /// <summary>
    /// The status below the line of the picture: "Connected", "Not connected: Cannot reach 10.0.0.5:5081",
    /// "No server address yet", "Service not running".
    /// </summary>
    public string NodeStatusText => NodeStatus.Text;

    public NodeStatusKind NodeStatusKind => NodeStatus.Kind;

    public bool IsNodeStatusOk => NodeStatusKind == NodeStatusKind.Ok;

    public bool IsNodeStatusWarning => NodeStatusKind == NodeStatusKind.Warning;

    public bool IsNodeStatusError => NodeStatusKind == NodeStatusKind.Error;

    /// <summary>
    /// The line of the picture: green while connected and bound; purple while connected and ready to bind (binding open,
    /// or a new server identity accepted); red without the service, while a bound node is not connected or the server
    /// cannot be reached; grey otherwise (no address yet, binding closed, connecting).
    /// </summary>
    public LinkKind Link => State switch
    {
        null => LinkKind.Error,
        { Connected: true } => IsPairing ? LinkKind.Ready : IsBound ? LinkKind.Connected : LinkKind.Idle,
        { ServerAddress.Length: 0 } => LinkKind.Idle,
        { Binding: NodeBinding.Bound } => LinkKind.Error,
        _ => HasConnectionProblem ? LinkKind.Error : LinkKind.Idle,
    };

    public bool IsLinkConnected => Link == LinkKind.Connected;

    public bool IsLinkReady => Link == LinkKind.Ready;

    /// <summary>The check badge in the middle of the line: connected and bound, or ready to bind.</summary>
    public bool IsLinkUp => Link is LinkKind.Connected or LinkKind.Ready;

    public bool IsLinkError => Link == LinkKind.Error;

    /// <summary>The server's side of the picture: its name once bound, else the address, else "Not set".</summary>
    public string ServerLabel => State switch
    {
        { Bound.Name: { Length: > 0 } name } => name,
        { ServerAddress: { Length: > 0 } address } => address,
        _ => "Not set",
    };

    /// <summary>The node's side of the picture: the site once bound, else the computer's name.</summary>
    public string NodeLabel => State switch
    {
        { Binding: NodeBinding.Bound, Bound.SiteName: { Length: > 0 } site } => site,
        { HostName: { Length: > 0 } host } => host,
        _ => "This node",
    };

    /// <summary>Below the node's name: "Site" once bound to a named site, else "Site node".</summary>
    public string NodeRole => State is { Binding: NodeBinding.Bound, Bound.SiteName.Length: > 0 } ? "Site" : "Site node";

    private (string Text, NodeStatusKind Kind) NodeStatus
    {
        get
        {
            if (State is not { } s)
                return (ServiceError is null or AdminTexts.ServiceNotRunning ? "Service not running" : ServiceError, NodeStatusKind.Error);
            if (s.Connected)
                return (IsPairing ? ReadyToBind : "Connected", NodeStatusKind.Ok); // the picture names the server and the node
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

    /// <summary>The binding line below the status in the picture: only while not bound (bound: Bound since in the Server card).</summary>
    public bool ShowBindingLine => State is { Binding: not NodeBinding.Bound };

    /// <summary>"2026-10-19 08:00": when the node was bound, right of the server address once bound.</summary>
    public string BoundSince => State?.Bound is { } bound ? bound.BoundUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";

    /// <summary>Bound since in place of the pairing code: bound, and no new server identity is binding with the code.</summary>
    public bool ShowBoundSince => IsBound && !ShowPairing;

    /// <summary>Binding is open, or a new server identity was accepted: the server binds with the pairing code.</summary>
    public bool IsPairing => IsBindingOpen || State is { NewServerAccepted: true, NewServerIdentity.Length: > 0 };

    /// <summary>The pairing code row below the server address: while the node is not bound (or a new server identity was accepted).</summary>
    public bool ShowPairing => IsPairing || IsBindingClosed;

    public bool HasPairingCode => PairingCode is { Length: > 0 };

    /// <summary>Accept new server identity: the bound server presents a new identity that is not accepted yet.</summary>
    public bool CanAcceptServer => State is { NewServerAccepted: false, NewServerIdentity.Length: > 0 };

    /// <summary>The connection problem below the address field ("Cannot reach 10.0.0.5:5081").</summary>
    public string? ConnectionProblem => HasConnectionProblem ? ConnectionText : null;

    /// <summary>The service has a server address (saved).</summary>
    public bool HasServerAddress => State?.ServerAddress.Length > 0;

    /// <summary>The address field's hint: what to do first on a new node, else the port.</summary>
    public string ServerHint => State is not null && !HasServerAddress ? EnterServer + " " + PortHint : PortHint;

    /// <summary>Requests of the server that wait.</summary>
    public int RequestCount { get; private set; }

    /// <summary>"The server asks for 2 ports."</summary>
    public string RequestsText => RequestCount == 1 ? "The server asks for 1 port." : $"The server asks for {RequestCount} ports.";

    /// <summary>A refusal of a binding action (below the pairing code).</summary>
    [ObservableProperty]
    public partial string? BindingError { get; private set; }

    /// <summary>A refusal of another change (in the node status).</summary>
    [ObservableProperty]
    public partial string? ActionError { get; private set; }

    [ObservableProperty]
    public partial string ServerAddress { get; set; } = "";

    /// <summary>A change runs (often waiting for the administrator prompt): every change button and check box is off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveServer), nameof(SaveServerBlockedReason), nameof(CanChange))]
    [NotifyCanExecuteChangedFor(nameof(SaveServerCommand), nameof(AllowBindingCommand), nameof(AcceptServerCommand), nameof(ShowPairingCodeCommand),
        nameof(AddPortCommand), nameof(RemovePortCommand), nameof(AllowRequestCommand),
        nameof(DismissRequestCommand), nameof(ResetCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>No change runs: the change buttons and check boxes are on.</summary>
    public bool CanChange => !IsBusy;

    public bool HasState => State is not null;

    public bool IsBound => State?.Binding == NodeBinding.Bound;

    public bool IsBindingOpen => State?.Binding == NodeBinding.Open;

    public bool IsBindingClosed => State?.Binding == NodeBinding.Closed;

    /// <summary>
    /// All cards (the default) of the network select: ticking it clears the chosen cards; choosing a card unticks it.
    /// Unticking it without a chosen card changes nothing (no chosen card means all cards).
    /// </summary>
    public bool AllCards
    {
        get => State?.AllCards ?? true;
        set
        {
            if (State is not null && value != State.AllCards && CanChange)
            {
                _ = ChangeAsync(AdminRequest.Of(AdminCommands.AllCards, ("on", value ? "true" : "false")));
            }
            else
            {
                OnPropertyChanged();
                AllCardsChoice.Refresh();
            }
        }
    }

    /// <summary>The network select's text: "All cards" or the chosen cards ("Cameras, Office").</summary>
    public string NetworksText => State is { AllCards: false } s && string.Join(", ", s.Cards.Where(c => c.Reached).Select(c => c.Name)) is { Length: > 0 } names
        ? names
        : "All cards";

    /// <summary>"Site Building B" below the status line once the server named the site; empty before.</summary>
    public string SiteText => State is { Binding: NodeBinding.Bound, Bound.SiteName: { Length: > 0 } site } ? "Site " + site : "";

    public bool HasSite => SiteText.Length > 0;

    /// <summary>The whole node ID (the tooltip of <see cref="NodeInfoText"/>).</summary>
    public string IdText => State?.NodeId ?? "";

    /// <summary>"Node ID 1457a0b5… · Version 1.0.0", small and grey at the end of the Node status block.</summary>
    public string NodeInfoText => State is not { } s
        ? ""
        : $"Node ID {(s.NodeId.Length > 8 ? s.NodeId[..8] + "…" : s.NodeId)} · Version {s.Version}";

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

    public bool HasRequests => RequestCount > 0;

    public bool HasRelayProblems => RelayProblems.Count > 0;

    /// <summary>The address field differs from the service's server address.</summary>
    public bool HasUnsavedChanges => State is { } s && !string.Equals(ServerAddress.Trim(), s.ServerAddress.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Why Save is disabled (the address field's error, or nothing changed), null when it is not.</summary>
    public string? SaveServerBlockedReason => ErrorOf(nameof(ServerAddress)) ?? (HasUnsavedChanges ? null : "No changes to save.");

    /// <summary>Save of the footer: the one accent button of the window.</summary>
    public bool CanSaveServer => SaveServerBlockedReason is null && !IsBusy;

    protected override void OnValidationChanged()
    {
        base.OnValidationChanged();
        OnPropertyChanged(nameof(SaveServerBlockedReason));
        OnPropertyChanged(nameof(CanSaveServer));
    }

    /// <summary>Reads the state (and keeps the user's unsaved server address).</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var applied = _applied;
        var response = await _channel.SendAsync(AdminRequest.Of(AdminCommands.State), CancellationToken.None).ConfigureAwait(true);
        if (applied != _applied)
            return; // a change applied a newer state meanwhile
        if (!response.Ok || response.State is null)
        {
            // One failed read is tolerated (the service was busy for a moment): the window keeps the last state.
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
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(true))
                await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
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
        }
        else if (response.Error != SystemElevation.Cancelled)
        {
            Validation.SetServerError(nameof(ServerAddress), response.Error);
            ActionError = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    public async Task AllowBindingAsync()
    {
        BindingError = null;
        var response = await ChangeAsync(AdminRequest.Of(AdminCommands.AllowBinding)).ConfigureAwait(true);
        if (!response.Ok && response.Error != SystemElevation.Cancelled)
            BindingError = response.Error;
    }

    /// <summary>Lets the server with the new identity bind with the pairing code; the administrator's answer carries it.</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    public Task AcceptServerAsync() => ChangeAsync(AdminRequest.Of(AdminCommands.AcceptServer));

    /// <summary>The pairing code of a bound node that accepted a new server identity (administrators only).</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    public async Task ShowPairingCodeAsync()
    {
        BindingError = null;
        var response = await ChangeAsync(AdminRequest.Of(AdminCommands.PairingCode)).ConfigureAwait(true);
        if (!response.Ok && response.Error != SystemElevation.Cancelled)
            BindingError = response.Error;
    }

    /// <summary>Add of the Ports card: the Add port dialog; its refusals stay in the dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    public Task AddPortAsync() => _dialogs.ShowAddPortAsync(new AddPortViewModel(SendPortAsync));

    private async Task<AdminResponse> SendPortAsync(PortEntry port)
    {
        var response = await SendAsAdministratorAsync(PortRequest(AdminCommands.AddPort, port)).ConfigureAwait(true);
        if (response.State is not null)
            Apply(response.State);
        else if (response.Ok)
            await RefreshAsync().ConfigureAwait(true);
        return response;
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

    /// <summary>Reset of the footer (left): the node forgets its server and gets a new identity.</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    public async Task ResetAsync()
    {
        var server = State?.Bound?.Name is { Length: > 0 } name ? name : "the server";
        if (!await _dialogs.ConfirmAsync("Reset", ResetMessage(server), "Reset").ConfigureAwait(true))
            return;
        await ChangeAsync(AdminRequest.Of(AdminCommands.Reset)).ConfigureAwait(true);
    }

    /// <summary>The copy button right of the pairing code.</summary>
    [RelayCommand]
    public Task CopyPairingCodeAsync() => PairingCode is { } code ? _dialogs.CopyAsync(code) : Task.CompletedTask;

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
        nameof(Link), nameof(IsLinkConnected), nameof(IsLinkReady), nameof(IsLinkUp), nameof(IsLinkError), nameof(ServerLabel), nameof(NodeLabel), nameof(NodeRole),
        nameof(HasPairingCode), nameof(CanShowPairingCode), nameof(PairingCode), nameof(CanAcceptServer), nameof(ShowBindingLine), nameof(BoundSince), nameof(ShowBoundSince), nameof(ConnectionProblem), nameof(IsPairing), nameof(ShowPairing),
    ];

    private async Task<AdminResponse> ChangeAsync(AdminRequest request)
    {
        ActionError = null;
        var response = await SendAsAdministratorAsync(request).ConfigureAwait(true);
        if (!response.Ok && response.Error != SystemElevation.Cancelled)
            ActionError = response.Error;
        if (response.Ok && response.PairingCode.Length > 0)
            _adminCode = response.PairingCode;
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
        _applied++;
        if (!(view.Binding == NodeBinding.Bound && view is { NewServerAccepted: true, NewServerIdentity.Length: > 0 }))
            _adminCode = null; // only a bound node that accepted a new identity keeps an administrator's code
        State = view;
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
            NetworkChoices.ReplaceAll([AllCardsChoice, .. Cards]);
        }
        else
        {
            foreach (var (item, card) in Cards.Zip(view.Cards))
                item.Apply(card, view.AllCards);
        }

        AllCardsChoice.Refresh();

        var listsKey = string.Join('|', view.Allowed.Select(p => p.ToString()))
            + "#" + string.Join('|', view.Requested.Select(p => p.ToString())) + "#" + string.Join('|', view.Relays.Select(r => r.ToString()));
        if (listsKey == _listsKey)
            return;
        _listsKey = listsKey;
        Ports.ReplaceAll(view.Requested.Select(PortItem.Requested)
            .Concat(view.Allowed.OrderBy(p => p.Use == PortUse.Listen).ThenBy(p => p.Protocol).ThenBy(p => p.Port).Select(PortItem.Allowed)));
        RelayProblems.ReplaceAll(view.Relays.Where(r => r.State != "Running").Select(RelayItem.From));
        RequestCount = view.Requested.Count;
        OnPropertyChanged(nameof(RequestCount));
        OnPropertyChanged(nameof(HasRequests));
        OnPropertyChanged(nameof(RequestsText));
        OnPropertyChanged(nameof(HasRelayProblems));
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

    private static AdminRequest PortRequest(string command, PortEntry port) => AdminRequest.Of(command,
        ("use", port.Use == PortUse.Listen ? "listen" : "connect"),
        ("protocol", port.Protocol == PortProtocol.Udp ? "udp" : "tcp"),
        ("port", port.Port.ToString(CultureInfo.InvariantCulture)),
        ("reason", port.Reason));

    private static string Hours(double hours)
    {
        var whole = (int)Math.Ceiling(hours);
        return whole == 1 ? "1 more hour" : $"{whole} more hours";
    }

    /// <summary>"AB12 CD34 ..." in groups of four, the first 32 characters.</summary>
    private static string Fingerprint(string hex) =>
        string.Join(' ', Enumerable.Range(0, Math.Min(hex.Length, 32) / 4).Select(i => hex.Substring(i * 4, 4)));
}
