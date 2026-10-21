using Microsoft.Extensions.Time.Testing;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.App.Services;
using Oadm.SiteNode.App.ViewModels;

namespace Oadm.SiteNode.App.Tests;

public sealed class NodeAppViewModelTests
{
    private readonly FakeChannel _channel = new();
    private readonly FakeElevation _elevation;
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeTimeProvider _time = new(FakeChannel.Now);
    private readonly NodeAppViewModel _vm;

    public NodeAppViewModelTests()
    {
        _elevation = new FakeElevation(_channel);
        _vm = new NodeAppViewModel(_channel, _elevation, _dialogs, _time);
    }

    [Fact]
    public async Task TheStateShowsBindingServerNetworksAndPorts()
    {
        await _vm.RefreshAsync();

        Assert.Equal("Not bound. Binding open for 23 more hours.", _vm.BindingText);
        Assert.True(_vm.IsBindingOpen);
        Assert.Equal("10.0.0.5", _vm.ServerAddress);
        Assert.Equal(NodeAppViewModel.PortHint, _vm.ServerHint);
        Assert.True(_vm.ShowPairing);
        Assert.True(_vm.IsConnected);
        Assert.Equal("", _vm.SiteText);
        Assert.False(_vm.HasSite);
        Assert.Equal("Node ID a1b2c3d4… · Version 3.7.0", _vm.NodeInfoText);
        Assert.Equal("a1b2c3d4e5f60718", _vm.IdText);
        Assert.Equal(2, _vm.Cards.Count);
        Assert.Equal("Default router", _vm.Cards[1].Hint);
        Assert.Equal("10.0.0.7/24 · Default router", _vm.Cards[1].Detail);
        Assert.True(_vm.Cards[0].Chosen);
        Assert.False(_vm.Cards[1].Chosen);
        Assert.False(_vm.AllCards);
        Assert.Equal("Cameras", _vm.NetworksText);
        // The ports table: the request first, then connect before listen, by port.
        Assert.Equal(["Out TCP 8443", "Out TCP 80", "Out TCP 443", "Out TCP 554", "In UDP 123"],
            _vm.Ports.Select(p => $"{p.Direction} {p.Protocol} {p.Port}"));
        Assert.True(_vm.Ports[0].IsRequested);
        Assert.Equal("camera 192.168.0.30", _vm.Ports[0].Reason);
        Assert.Equal("Requested", _vm.Ports[0].StateText);
        Assert.Equal("Allowed", _vm.Ports[1].StateText);
        Assert.Empty(_vm.RelayProblems); // the NTP relay runs
        Assert.Null(_vm.ServiceError);
        Assert.Equal("ABCD-EFGH", _vm.PairingCode); // part of the state, no prompt
        Assert.Equal(0, _elevation.Runs);
    }

    [Fact]
    public async Task ABoundNodeNamesTheServerAndTheSite()
    {
        _channel.State = FakeChannel.Sample(NodeBinding.Bound);

        await _vm.RefreshAsync();

        Assert.StartsWith("Bound since ", _vm.BindingText, StringComparison.Ordinal);
        Assert.Equal("Connected", _vm.NodeStatusText); // the picture names the server
        Assert.Equal(("OADM-SRV", "Building B", "Site"), (_vm.ServerLabel, _vm.NodeLabel, _vm.NodeRole));
        Assert.Equal(LinkKind.Connected, _vm.Link);
        Assert.Equal("Site Building B", _vm.SiteText);
        Assert.False(_vm.IsBindingOpen);
        Assert.Null(_vm.PairingCode);
        Assert.False(_vm.ShowPairing);
        Assert.True(_vm.ShowBoundSince);
        Assert.False(_vm.ShowBindingLine);
        Assert.Equal(FakeChannel.BoundUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), _vm.BoundSince);
    }

    [Fact]
    public async Task WithoutTheServiceTheWindowSaysSo()
    {
        _channel.State = null;

        await _vm.RefreshAsync();

        Assert.Equal(AdminTexts.ServiceNotRunning, _vm.ServiceError);
        Assert.False(_vm.HasState);
    }

    [Fact]
    public async Task EveryChangeRunsAsAdministrator()
    {
        await _vm.RefreshAsync();

        _vm.Cards[1].Chosen = true;
        await Sdk.Testing.Eventually.TrueAsync(() => _elevation.Runs == 1, "the card change runs elevated");
        var request = _channel.Requests[^1];
        Assert.True(request.AsAdmin);
        Assert.Equal((AdminCommands.SetCard, "office", "true"), (request.Request.Command, request.Request.Arg("id"), request.Request.Arg("reached")));
    }

    [Fact]
    public async Task AnAdministratorSendsChangesItself()
    {
        _elevation.IsAdministrator = true;
        await _vm.RefreshAsync();

        await _vm.AllowRequestAsync(_vm.Ports[0]);

        Assert.Equal(0, _elevation.Runs);
        var request = _channel.Requests[^1].Request;
        Assert.Equal((AdminCommands.AllowRequest, "connect", "tcp", "8443"), (request.Command, request.Arg("use"), request.Arg("protocol"), request.Arg("port")));
    }

    [Fact]
    public async Task ADeclinedPromptChangesNothingAndShowsNoError()
    {
        _elevation.Decline = true;
        await _vm.RefreshAsync();

        await _vm.AllowBindingAsync();
        await _vm.RemovePortAsync(_vm.Ports[1]);

        Assert.Null(_vm.BindingError);
        Assert.Null(_vm.ActionError);
    }

    [Fact]
    public async Task TheServerAddressIsCheckedBelowTheFieldBeforeSaving()
    {
        await _vm.RefreshAsync();
        var sent = _channel.Requests.Count;

        _vm.ServerAddress = "not a host!";
        _vm.FieldLeft(nameof(NodeAppViewModel.ServerAddress));

        Assert.NotNull(_vm.ErrorOf(nameof(NodeAppViewModel.ServerAddress)));
        Assert.False(_vm.CanSaveServer);
        Assert.Equal(_vm.ErrorOf(nameof(NodeAppViewModel.ServerAddress)), _vm.SaveServerBlockedReason);
        await _vm.SaveServerAsync();
        Assert.Equal(sent, _channel.Requests.Count);

        _vm.ServerAddress = "oadm.example.com:5082";
        Assert.True(_vm.CanSaveServer);
        await _vm.SaveServerAsync();
        var request = _channel.Requests.Last(r => r.Request.Command == AdminCommands.SetServer).Request;
        Assert.Equal("oadm.example.com:5082", request.Arg("address"));
        Assert.False(request.Args.ContainsKey("mode")); // the node always connects to the server
    }

    [Fact]
    public async Task ThePairingCodeShowsWithoutAPromptAndSaysHowLongItLasts()
    {
        _channel.State = FakeChannel.Sample(serverAddress: "", connected: false, connectionText: "");
        await _vm.RefreshAsync();

        // Right below the empty address field: no Show pairing code, no prompt.
        Assert.True(_vm.ShowPairing);
        Assert.Equal("ABCD-EFGH", _vm.PairingCode);
        Assert.Equal(NodeAppViewModel.TypeCode + " Valid for 8 more minutes.", _vm.PairingHint);
        Assert.Equal(0, _elevation.Runs);
        Assert.DoesNotContain(_channel.Requests, r => r.Request.Command == AdminCommands.PairingCode);

        _time.Advance(TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(30));
        Assert.Equal(NodeAppViewModel.TypeCode + " Valid for 2 more minutes.", _vm.PairingHint);
        _time.Advance(TimeSpan.FromSeconds(80)); // the last minute counts in seconds
        Assert.Equal(NodeAppViewModel.TypeCode + " Valid for 10 more seconds.", _vm.PairingHint);

        await _vm.CopyPairingCodeAsync();
        Assert.Equal(["ABCD-EFGH"], _dialogs.Copied);
    }

    [Fact]
    public async Task TheCodeShownIsAlwaysTheNodes()
    {
        await _vm.RefreshAsync();
        Assert.Equal("ABCD-EFGH", _vm.PairingCode);

        // A new code (10 minutes, five wrong codes, a service restart): the next read shows it.
        _channel.State = new NodeView
        {
            NodeId = "a1",
            Binding = NodeBinding.Open,
            PairingCode = "WXYZ-2345",
            PairingCodeValidUntil = FakeChannel.Now + TimeSpan.FromMinutes(10),
        };
        await _vm.RefreshAsync();

        Assert.Equal("WXYZ-2345", _vm.PairingCode);
        Assert.Equal(NodeAppViewModel.TypeCode + " Valid for 10 more minutes.", _vm.PairingHint);
    }

    [Fact]
    public async Task SavingTheAddressOfABoundNodeShowsNoCode()
    {
        _channel.State = FakeChannel.Sample(NodeBinding.Bound);
        await _vm.RefreshAsync();

        _vm.ServerAddress = "oadm.example.com";
        await _vm.SaveServerAsync();

        Assert.Null(_vm.PairingCode);
        Assert.False(_vm.ShowPairing);
    }

    [Fact]
    public async Task AServerRefusalStandsBelowItsField()
    {
        _elevation.IsAdministrator = true;
        _channel.Answer = r => r.Command == AdminCommands.AddPort ? AdminResponse.Fail("At most 64 ports.") : null!;
        await _vm.RefreshAsync();
        AddPortViewModel? dialog = null;
        _dialogs.AddPort = async d =>
        {
            dialog = d;
            d.PortText = "8080";
            await d.AddAsync();
        };

        await _vm.AddPortAsync();

        Assert.Equal("At most 64 ports.", dialog!.ErrorOf(nameof(AddPortViewModel.PortText))); // in the dialog
        Assert.Null(_vm.ActionError);
    }

    [Fact]
    public async Task ANewNodeAsksForTheServerAddressFirst()
    {
        _channel.State = FakeChannel.Sample(serverAddress: "", connected: false, connectionText: "");
        _elevation.IsAdministrator = true;
        await _vm.RefreshAsync();

        Assert.False(_vm.HasServerAddress);
        Assert.StartsWith(NodeAppViewModel.EnterServer, _vm.ServerHint, StringComparison.Ordinal);
        Assert.Null(_vm.ErrorOf(nameof(NodeAppViewModel.ServerAddress))); // nothing red on an untouched form

        _vm.FieldLeft(nameof(NodeAppViewModel.ServerAddress));
        Assert.Equal(NodeAppViewModel.EnterServer, _vm.ErrorOf(nameof(NodeAppViewModel.ServerAddress)));
        var sent = _channel.Requests.Count;
        await _vm.SaveServerAsync();
        Assert.Equal(sent, _channel.Requests.Count);
    }

    [Fact]
    public async Task NetworksAreAllCardsOrTheChosenCards()
    {
        _elevation.IsAdministrator = true;
        _channel.State = FakeChannel.Sample(allCards: true);
        await _vm.RefreshAsync();

        // The select: All cards first, then every card.
        Assert.Equal([_vm.AllCardsChoice, .. _vm.Cards], _vm.NetworkChoices);
        Assert.True(_vm.AllCards);
        Assert.True(_vm.AllCardsChoice.Chosen);
        Assert.Equal("All cards", _vm.NetworksText);
        Assert.All(_vm.Cards, c => Assert.False(c.Chosen)); // All cards: no card is ticked

        _vm.Cards[0].Chosen = true;
        await Sdk.Testing.Eventually.TrueAsync(() => _channel.Requests[^1].Request.Command == AdminCommands.SetCard, "the card is chosen");
        var card = _channel.Requests[^1].Request;
        Assert.Equal(("cam", "true"), (card.Arg("id"), card.Arg("reached")));

        _channel.State = FakeChannel.Sample();
        await _vm.RefreshAsync();
        Assert.False(_vm.AllCards);
        Assert.False(_vm.AllCardsChoice.Chosen);
        Assert.Equal("Cameras", _vm.NetworksText);
        _vm.AllCardsChoice.Chosen = true;
        await Sdk.Testing.Eventually.TrueAsync(() => _channel.Requests[^1].Request.Command == AdminCommands.AllCards, "All cards is ticked");
        Assert.Equal("true", _channel.Requests[^1].Request.Arg("on"));
    }

    [Fact]
    public async Task ResetAsksFirstNamingTheServer()
    {
        _channel.State = FakeChannel.Sample(NodeBinding.Bound);
        await _vm.RefreshAsync();
        _dialogs.Answer = false;

        await _vm.ResetAsync();
        Assert.Equal(0, _elevation.Runs);
        Assert.Equal(NodeAppViewModel.ResetMessage("OADM-SRV"), Assert.Single(_dialogs.Asked));

        _dialogs.Answer = true;
        await _vm.ResetAsync();
        Assert.Equal(AdminCommands.Reset, _channel.Requests.Last(r => r.AsAdmin).Request.Command);
    }

    [Fact]
    public async Task WrongCodesAndANewServerIdentityAreShown()
    {
        _channel.State = new NodeView
        {
            NodeId = "a1",
            Binding = NodeBinding.Bound,
            Bound = new BoundServerView("OADM-SRV", "AB", DateTimeOffset.UnixEpoch, "Building B"),
            NewServerIdentity = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
            WrongCodes = [new WrongCodeView(DateTimeOffset.UnixEpoch, "10.0.0.9"), new WrongCodeView(DateTimeOffset.UnixEpoch, "10.0.0.9")],
        };

        await _vm.RefreshAsync();

        Assert.True(_vm.HasNewIdentity);
        Assert.Equal("OADM-SRV presents a new identity (0123 4567 89AB CDEF 0123 4567 89AB CDEF).", _vm.NewIdentityText);
        Assert.StartsWith("2 wrong pairing codes, the last from 10.0.0.9", _vm.WrongCodesText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNodeStatusSaysWhetherTheNodeReachesTheServer()
    {
        await _vm.RefreshAsync();
        Assert.Equal(NodeAppViewModel.ReadyToBind, _vm.NodeStatusText); // connected, not bound: purple
        Assert.Equal(LinkKind.Ready, _vm.Link);
        Assert.Equal(("10.0.0.5", "site-pc", "Site node"), (_vm.ServerLabel, _vm.NodeLabel, _vm.NodeRole));
        Assert.True(_vm.IsNodeStatusOk);

        _channel.State = FakeChannel.Sample(serverAddress: "", connected: false, connectionText: "");
        await _vm.RefreshAsync();
        Assert.Equal("No server address yet", _vm.NodeStatusText);
        Assert.True(_vm.IsNodeStatusWarning);
        Assert.Equal(LinkKind.Idle, _vm.Link);
        Assert.Equal("Not set", _vm.ServerLabel);
        Assert.Null(_vm.ConnectionProblem);

        _channel.State = FakeChannel.Sample(connected: false, connectionText: "");
        await _vm.RefreshAsync();
        Assert.Equal("Not connected", _vm.NodeStatusText);
        Assert.True(_vm.IsNodeStatusWarning);
        Assert.Equal(LinkKind.Idle, _vm.Link); // not bound, connecting

        _channel.State = FakeChannel.Sample(connected: false, connectionText: "Cannot reach 10.0.0.5:5081");
        await _vm.RefreshAsync();
        Assert.Equal("Not connected: Cannot reach 10.0.0.5:5081", _vm.NodeStatusText);
        Assert.True(_vm.IsNodeStatusError);
        Assert.Equal("Cannot reach 10.0.0.5:5081", _vm.ConnectionProblem);
        Assert.Equal(LinkKind.Error, _vm.Link);

        _channel.State = FakeChannel.Sample(NodeBinding.Bound);
        await _vm.RefreshAsync();
        Assert.Equal("Connected", _vm.NodeStatusText);
        Assert.Equal(LinkKind.Connected, _vm.Link); // bound: green

        _channel.State = FakeChannel.Sample(NodeBinding.Closed);
        await _vm.RefreshAsync();
        Assert.Equal("Connected", _vm.NodeStatusText);
        Assert.Equal(LinkKind.Idle, _vm.Link); // binding closed: nothing to bind

        _channel.State = FakeChannel.Sample(NodeBinding.Bound, connected: false, connectionText: "");
        await _vm.RefreshAsync();
        Assert.Equal(LinkKind.Error, _vm.Link); // a bound node that lost its connection

        _channel.State = FakeChannel.Sample(NodeBinding.Bound, connected: false, connectionText: "Cannot reach 10.0.0.5:5081");
        await _vm.RefreshAsync();
        Assert.Equal("Not connected: Cannot reach 10.0.0.5:5081", _vm.NodeStatusText);

        _channel.State = null;
        await _vm.RefreshAsync();
        await _vm.RefreshAsync(); // one failed read keeps the last state
        Assert.Equal("Service not running", _vm.NodeStatusText);
        Assert.True(_vm.IsNodeStatusError);
        Assert.Equal(LinkKind.Error, _vm.Link);
    }

    [Fact]
    public async Task ThePairingRowOffersWhatTheStateNeeds()
    {
        await _vm.RefreshAsync();
        Assert.True(_vm.HasPairingCode);
        Assert.False(_vm.CanAcceptServer);

        _channel.State = FakeChannel.Sample(NodeBinding.Closed);
        await _vm.RefreshAsync();
        Assert.Equal("Binding is closed.", _vm.BindingText);
        Assert.True(_vm.IsBindingClosed);
        Assert.True(_vm.ShowPairing); // with Allow binding
        Assert.False(_vm.HasPairingCode);

        _channel.State = FakeChannel.Sample(NodeBinding.Bound, newIdentity: "0123456789ABCDEF0123456789ABCDEF");
        await _vm.RefreshAsync();
        Assert.True(_vm.CanAcceptServer);
        Assert.False(_vm.ShowPairing);
    }

    [Fact]
    public async Task SaveIsOnlyEnabledWhileTheFormHasChanges()
    {
        await _vm.RefreshAsync();
        Assert.False(_vm.HasUnsavedChanges);
        Assert.False(_vm.CanSaveServer);
        Assert.Equal("No changes to save.", _vm.SaveServerBlockedReason);

        _vm.ServerAddress = "10.0.0.6";
        Assert.True(_vm.HasUnsavedChanges);
        Assert.True(_vm.CanSaveServer);

        _vm.ServerAddress = "10.0.0.5";
        Assert.False(_vm.CanSaveServer);
    }

    [Fact]
    public void CloseAsksTheWindowToClose()
    {
        var asked = 0;
        _vm.CloseRequested += (_, _) => asked++;

        _vm.CloseCommand.Execute(null);

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task TheServersRequestsAndRelayProblemsAreInTheStatus()
    {
        await _vm.RefreshAsync();

        Assert.Equal(1, _vm.RequestCount);
        Assert.True(_vm.HasRequests);
        Assert.Equal("The server asks for 1 port.", _vm.RequestsText);
        Assert.False(_vm.HasRelayProblems);

        _channel.State = new NodeView
        {
            NodeId = "a1",
            Binding = NodeBinding.Open,
            Relays = [new RelayView(PortProtocol.Udp, 123, "NTP server", "Port in use", "UDP 123 is used by another program.")],
        };
        await _vm.RefreshAsync();
        Assert.Equal(0, _vm.RequestCount);
        Assert.False(_vm.HasRequests);
        var problem = Assert.Single(_vm.RelayProblems);
        Assert.Equal(("NTP server, UDP 123", "UDP 123 is used by another program."), (problem.Text, problem.Message));
    }

    [Fact]
    public void TheElevatedCopyGetsTheRequestBackIntact()
    {
        var request = AdminRequest.Of(AdminCommands.AddPort, ("use", "listen"), ("protocol", "udp"), ("port", "123"), ("reason", "NTP server"));

        var decoded = SystemElevation.Decode(SystemElevation.Encode(request))!;

        Assert.Equal(request.Command, decoded.Command);
        Assert.Equal(request.Args, decoded.Args);
        Assert.Null(SystemElevation.Decode("not base64!"));
        Assert.Equal("x", SystemElevation.Value(["--out", "x"], "--out"));
        Assert.Null(SystemElevation.Value(["--out"], "--out"));
    }

    [Fact]
    public async Task OneFailedReadKeepsTheStateAndTheCode()
    {
        await _vm.RefreshAsync();
        var state = _channel.State;

        _channel.State = null;
        await _vm.RefreshAsync();
        Assert.True(_vm.HasState); // no flash of "Service not running"
        Assert.Equal("ABCD-EFGH", _vm.PairingCode);

        await _vm.RefreshAsync();
        Assert.False(_vm.HasState);
        Assert.Null(_vm.PairingCode);
        Assert.Equal("Service not running", _vm.NodeStatusText);

        _channel.State = state;
        await _vm.RefreshAsync();
        Assert.Equal("ABCD-EFGH", _vm.PairingCode);
    }

    [Fact]
    public async Task EveryChangeIsOffWhileTheAdministratorPromptIsOpen()
    {
        await _vm.RefreshAsync();
        _elevation.Prompt = new TaskCompletionSource();
        var request = _vm.Ports[0];
        var port = _vm.Ports[1];

        var allowing = _vm.AllowRequestAsync(request);

        Assert.True(_vm.IsBusy);
        Assert.False(_vm.CanChange);
        Assert.False(_vm.CanSaveServer);
        Assert.False(_vm.AllowBindingCommand.CanExecute(null));
        Assert.False(_vm.AddPortCommand.CanExecute(null));
        Assert.False(_vm.RemovePortCommand.CanExecute(port));
        Assert.False(_vm.AllowRequestCommand.CanExecute(request));
        Assert.False(_vm.ResetCommand.CanExecute(null));
        Assert.False(_vm.SaveServerCommand.CanExecute(null));

        // A card or All cards ticked meanwhile sends nothing and goes back to what the service has.
        _vm.Cards[1].Chosen = true;
        _vm.AllCardsChoice.Chosen = true;
        Assert.False(_vm.Cards[1].Chosen);
        Assert.False(_vm.AllCards);
        Assert.False(_vm.AllCardsChoice.Chosen);
        Assert.Equal(1, _elevation.Runs);

        _elevation.Prompt.SetResult();
        await allowing;
        Assert.False(_vm.IsBusy);
        Assert.True(_vm.AddPortCommand.CanExecute(null));
        Assert.True(_vm.ResetCommand.CanExecute(null));
    }

    [Fact]
    public async Task AddOpensADialogThatSendsThePortAndClosesWhenItIsAdded()
    {
        _elevation.IsAdministrator = true;
        await _vm.RefreshAsync();
        var closed = new List<bool>();
        _dialogs.AddPort = async d =>
        {
            d.CloseRequested += (_, added) => closed.Add(added);
            Assert.Equal("Enter a port.", d.AddBlockedReason);
            d.PortText = "70000";
            d.FieldLeft(nameof(AddPortViewModel.PortText));
            Assert.Equal(AddPortViewModel.PortProblem, d.AddBlockedReason);
            d.PortText = "123";
            d.Use = d.Uses[1];
            d.Protocol = d.Protocols[1];
            Assert.Null(d.AddBlockedReason);
            await d.AddAsync();
        };

        await _vm.AddPortAsync();

        var request = _channel.Requests[^1].Request;
        Assert.Equal((AdminCommands.AddPort, "listen", "udp", "123"), (request.Command, request.Arg("use"), request.Arg("protocol"), request.Arg("port")));
        Assert.Equal([true], closed);
        Assert.Equal(["Out", "In"], new AddPortViewModel(_ => Task.FromResult(new AdminResponse())).Uses.Select(u => u.Text));
    }

    [Fact]
    public async Task ADismissedPromptKeepsTheAddPortDialogOpenWithoutAnError()
    {
        _elevation.Decline = true;
        await _vm.RefreshAsync();
        AddPortViewModel? dialog = null;
        var closed = false;
        _dialogs.AddPort = async d =>
        {
            dialog = d;
            d.CloseRequested += (_, _) => closed = true;
            d.PortText = "8080";
            await d.AddAsync();
        };

        await _vm.AddPortAsync();

        Assert.False(closed);
        Assert.Null(dialog!.ErrorOf(nameof(AddPortViewModel.PortText)));
        Assert.True(dialog.IsIdle);
    }

    [Fact]
    public async Task ABoundNodeThatAcceptedANewServerShowsItsCodeOnlyAfterAnAdministratorAskedForIt()
    {
        var accepted = new NodeView
        {
            NodeId = "a1",
            Binding = NodeBinding.Bound,
            Bound = new BoundServerView("OADM-SRV", "AB", DateTimeOffset.UnixEpoch, "Building B"),
            NewServerIdentity = "0123456789ABCDEF",
            NewServerAccepted = true,
        };
        _channel.State = accepted;
        _channel.Answer = r => r.Command == AdminCommands.PairingCode ? new AdminResponse { Ok = true, PairingCode = "WXYZ-2345", State = accepted } : null!;
        await _vm.RefreshAsync();
        Assert.True(_vm.ShowPairing);
        Assert.Null(_vm.PairingCode); // the state everyone reads never carries it
        Assert.True(_vm.CanShowPairingCode);

        await _vm.ShowPairingCodeAsync();

        Assert.Equal(1, _elevation.Runs);
        Assert.Equal("WXYZ-2345", _vm.PairingCode);
        Assert.False(_vm.CanShowPairingCode);
        await _vm.RefreshAsync();
        Assert.Equal("WXYZ-2345", _vm.PairingCode); // kept while the accepted identity binds

        _channel.State = FakeChannel.Sample(NodeBinding.Bound);
        await _vm.RefreshAsync();
        Assert.Null(_vm.PairingCode);
    }

    [Fact]
    public async Task TheAddPortDialogCannotBeClosedWhileTheAdministratorPromptIsOpen()
    {
        await _vm.RefreshAsync();
        _elevation.Prompt = new TaskCompletionSource();
        Task? adding = null;
        AddPortViewModel? dialog = null;
        _dialogs.AddPort = d =>
        {
            dialog = d;
            d.PortText = "8080";
            adding = d.AddAsync();
            return Task.CompletedTask;
        };

        await _vm.AddPortAsync();

        Assert.False(dialog!.IsIdle);
        Assert.False(dialog.CancelCommand.CanExecute(null));
        _elevation.Prompt.SetResult();
        await adding!;
        Assert.True(dialog.IsIdle);
        Assert.True(dialog.CancelCommand.CanExecute(null));
    }
}
