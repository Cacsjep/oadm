using Oadm.SiteNode.Admin;
using Oadm.SiteNode.App.Services;
using Oadm.SiteNode.App.ViewModels;

namespace Oadm.SiteNode.App.Tests;

public sealed class NodeAppViewModelTests
{
    private readonly FakeChannel _channel = new();
    private readonly FakeElevation _elevation;
    private readonly FakeDialogs _dialogs = new();
    private readonly NodeAppViewModel _vm;

    public NodeAppViewModelTests()
    {
        _elevation = new FakeElevation(_channel);
        _vm = new NodeAppViewModel(_channel, _elevation, _dialogs);
    }

    [Fact]
    public async Task TheStateShowsBindingServerNetworksPortsAndConnections()
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
        Assert.True(_vm.Cards[0].Chosen);
        Assert.False(_vm.Cards[1].Chosen);
        Assert.False(_vm.AllCards);
        Assert.Equal(["TCP 80", "TCP 443", "TCP 554"], _vm.ConnectPorts.Select(p => p.Text));
        Assert.Equal("UDP 123", Assert.Single(_vm.ListenPorts).Text);
        Assert.Equal("Connect to TCP 8443: camera 192.168.0.30", Assert.Single(_vm.RequestedPorts).Text);
        Assert.Equal("NTP server, UDP 123", Assert.Single(_vm.Relays).Text);
        Assert.Equal("3 short, 12 permanent", _vm.ConnectionsText);
        Assert.Null(_vm.ServiceError);
        Assert.Null(_vm.PairingCode); // never read without asking
    }

    [Fact]
    public async Task ABoundNodeNamesTheServerAndTheSite()
    {
        _channel.State = FakeChannel.Sample(NodeBinding.Bound);

        await _vm.RefreshAsync();

        Assert.StartsWith("Bound since ", _vm.BindingText, StringComparison.Ordinal);
        Assert.Equal("Connected to OADM-SRV", _vm.NodeStatusText); // the server once, the site on its own line
        Assert.Equal("Site Building B", _vm.SiteText);
        Assert.False(_vm.IsBindingOpen);
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
    public async Task ThePairingCodeAndEveryChangeRunAsAdministrator()
    {
        await _vm.RefreshAsync();

        await _vm.ShowPairingCodeAsync();
        Assert.Equal("ABCD-EFGH", _vm.PairingCode);
        Assert.Equal(1, _elevation.Runs);
        Assert.True(_channel.Requests[^1].AsAdmin);

        _vm.Cards[1].Chosen = true;
        await Sdk.Testing.Eventually.TrueAsync(() => _elevation.Runs == 2, "the card change runs elevated");
        var request = _channel.Requests[^1].Request;
        Assert.Equal((AdminCommands.SetCard, "office", "true"), (request.Command, request.Arg("id"), request.Arg("reached")));
    }

    [Fact]
    public async Task AnAdministratorSendsChangesItself()
    {
        _elevation.IsAdministrator = true;
        await _vm.RefreshAsync();

        await _vm.AllowRequestAsync(_vm.RequestedPorts[0]);

        Assert.Equal(0, _elevation.Runs);
        var request = _channel.Requests[^1].Request;
        Assert.Equal((AdminCommands.AllowRequest, "connect", "tcp", "8443"), (request.Command, request.Arg("use"), request.Arg("protocol"), request.Arg("port")));
    }

    [Fact]
    public async Task ADeclinedPromptChangesNothingAndShowsNoError()
    {
        _elevation.Decline = true;
        await _vm.RefreshAsync();

        await _vm.ShowPairingCodeAsync();
        await _vm.AllowBindingAsync();

        Assert.Null(_vm.PairingCode);
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
    public async Task SavingTheAddressShowsThePairingCodeAtOnce()
    {
        _channel.State = FakeChannel.Sample(serverAddress: "", connected: false, connectionText: "");
        await _vm.RefreshAsync();
        Assert.Null(_vm.PairingCode);

        _vm.ServerAddress = "10.0.0.5";
        await _vm.SaveServerAsync();

        // The save ran elevated (one prompt) and its answer carried the code: no Show pairing code needed.
        Assert.Equal(1, _elevation.Runs);
        Assert.Equal("ABCD-EFGH", _vm.PairingCode);
        Assert.True(_vm.ShowPairing);
        Assert.False(_vm.CanShowPairingCode);
        Assert.DoesNotContain(_channel.Requests, r => r.Request.Command == AdminCommands.PairingCode);

        // The state everyone reads never carries the code; a refresh keeps the one shown.
        await _vm.RefreshAsync();
        Assert.Equal("ABCD-EFGH", _vm.PairingCode);
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
        _channel.Answer = r => r.Command == AdminCommands.AddSubnet ? AdminResponse.Fail("At most 64 extra networks.") : null!;
        await _vm.RefreshAsync();

        _vm.NewSubnet = "10.30.0.0/16";
        await _vm.AddSubnetAsync();

        Assert.Equal("At most 64 extra networks.", _vm.ErrorOf(nameof(NodeAppViewModel.NewSubnet)));
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
        Assert.False(_vm.ShowPairing); // the pairing code comes once the node has a server
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

        Assert.True(_vm.AllCards);
        Assert.All(_vm.Cards, c => Assert.False(c.Chosen)); // All cards: no card is ticked

        _vm.Cards[0].Chosen = true;
        await Sdk.Testing.Eventually.TrueAsync(() => _channel.Requests[^1].Request.Command == AdminCommands.SetCard, "the card is chosen");
        var card = _channel.Requests[^1].Request;
        Assert.Equal(("cam", "true"), (card.Arg("id"), card.Arg("reached")));

        _channel.State = FakeChannel.Sample();
        await _vm.RefreshAsync();
        Assert.False(_vm.AllCards);
        _vm.AllCards = true;
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
        Assert.Equal("Connected to 10.0.0.5:5081", _vm.NodeStatusText);
        Assert.True(_vm.IsNodeStatusOk);

        _channel.State = FakeChannel.Sample(serverAddress: "", connected: false, connectionText: "");
        await _vm.RefreshAsync();
        Assert.Equal("No server address yet", _vm.NodeStatusText);
        Assert.True(_vm.IsNodeStatusWarning);
        Assert.Null(_vm.ConnectionProblem);

        _channel.State = FakeChannel.Sample(connected: false, connectionText: "");
        await _vm.RefreshAsync();
        Assert.Equal("Not connected", _vm.NodeStatusText);
        Assert.True(_vm.IsNodeStatusWarning);

        _channel.State = FakeChannel.Sample(connected: false, connectionText: "Cannot reach 10.0.0.5:5081");
        await _vm.RefreshAsync();
        Assert.Equal("Not connected: Cannot reach 10.0.0.5:5081", _vm.NodeStatusText);
        Assert.True(_vm.IsNodeStatusError);
        Assert.Equal("Cannot reach 10.0.0.5:5081", _vm.ConnectionProblem);

        _channel.State = FakeChannel.Sample(NodeBinding.Bound);
        await _vm.RefreshAsync();
        Assert.Equal("Connected to OADM-SRV", _vm.NodeStatusText);

        _channel.State = FakeChannel.Sample(NodeBinding.Bound, connected: false, connectionText: "Cannot reach 10.0.0.5:5081");
        await _vm.RefreshAsync();
        Assert.Equal("Not connected: Cannot reach 10.0.0.5:5081", _vm.NodeStatusText);

        _channel.State = null;
        await _vm.RefreshAsync();
        await _vm.RefreshAsync(); // one failed read keeps the last state
        Assert.Equal("Service not running", _vm.NodeStatusText);
        Assert.True(_vm.IsNodeStatusError);
    }

    [Fact]
    public async Task TheBindingSectionOffersWhatTheStateNeeds()
    {
        await _vm.RefreshAsync();
        Assert.True(_vm.CanShowPairingCode);
        Assert.False(_vm.CanAcceptServer);
        await _vm.ShowPairingCodeAsync();
        Assert.True(_vm.HasPairingCode);
        Assert.False(_vm.CanShowPairingCode);

        _channel.State = FakeChannel.Sample(NodeBinding.Closed);
        await _vm.RefreshAsync();
        Assert.Equal("Binding is closed.", _vm.BindingText);
        Assert.True(_vm.IsBindingClosed);
        Assert.False(_vm.CanShowPairingCode);

        _channel.State = FakeChannel.Sample(NodeBinding.Bound, newIdentity: "0123456789ABCDEF0123456789ABCDEF");
        await _vm.RefreshAsync();
        Assert.True(_vm.CanAcceptServer);
        Assert.False(_vm.CanShowPairingCode);
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
    public async Task TheServersRequestsCountOnThePortsTabAndReviewOpensIt()
    {
        await _vm.RefreshAsync();

        Assert.Equal(NodeTab.General, _vm.Tab);
        Assert.Equal(1, _vm.RequestCount);
        Assert.Equal("The server asks for 1 port.", _vm.RequestsText);
        _vm.ShowPortsCommand.Execute(null);
        Assert.True(_vm.IsPortsTab);
        Assert.False(_vm.IsGeneralTab);

        _channel.State = FakeChannel.Sample(requests: false);
        await _vm.RefreshAsync();
        Assert.Equal(0, _vm.RequestCount);
        Assert.False(_vm.HasRequests);
    }

    [Fact]
    public async Task TheConnectionLogIsReadOnRequest()
    {
        await _vm.LoadLogAsync();

        var row = Assert.Single(_vm.Log);
        Assert.Equal(("OADM-SRV", "192.168.0.20:443", "Short", "Connected"), (row.Server, row.Target, row.Kind, row.Result));
        Assert.True(row.IsOk);
    }

    [Theory]
    [InlineData("Connected", "ok")]
    [InlineData("Accepted", "ok")]
    [InlineData("Port not allowed", "warning")]
    [InlineData("Outside the networks", "warning")]
    [InlineData("Busy", "warning")]
    [InlineData("12 answered", "info")]
    [InlineData("Refused", "error")]
    [InlineData("Timed out", "error")]
    [InlineData("Unreachable", "error")]
    public void TheLogResultIsAStatusWithItsColor(string result, string kind)
    {
        var row = new LogRow("2026-10-19 12:00:00", "OADM-SRV", "192.168.0.20:443", "Short", result);

        Assert.Equal(kind, row.IsOk ? "ok" : row.IsWarning ? "warning" : row.IsInfo ? "info" : row.IsError ? "error" : "none");
        Assert.Equal(1, new[] { row.IsOk, row.IsWarning, row.IsInfo, row.IsError }.Count(b => b));
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
    public async Task ANewCodeOnTheNodeClearsTheCodeShown()
    {
        await _vm.RefreshAsync();
        await _vm.ShowPairingCodeAsync();
        await _vm.RefreshAsync();
        Assert.Equal("ABCD-EFGH", _vm.PairingCode); // the same code: kept

        // Five wrong codes or a service restart: the node has another code now.
        _channel.State = FakeChannel.Sample(codeGeneration: 2);
        await _vm.RefreshAsync();

        Assert.Null(_vm.PairingCode);
        Assert.True(_vm.CanShowPairingCode);
    }

    [Fact]
    public async Task OneFailedReadKeepsTheStateAndTheCode()
    {
        await _vm.RefreshAsync();
        await _vm.ShowPairingCodeAsync();
        var state = _channel.State;

        _channel.State = null;
        await _vm.RefreshAsync();
        Assert.True(_vm.HasState); // no flash of "Service not running"
        Assert.Equal("ABCD-EFGH", _vm.PairingCode);

        await _vm.RefreshAsync();
        Assert.False(_vm.HasState);
        Assert.Equal("Service not running", _vm.NodeStatusText);

        _channel.State = state; // the same run of the service answers again
        await _vm.RefreshAsync();
        Assert.Equal("ABCD-EFGH", _vm.PairingCode);
    }

    [Fact]
    public async Task EveryChangeIsOffWhileTheAdministratorPromptIsOpen()
    {
        await _vm.RefreshAsync();
        _elevation.Prompt = new TaskCompletionSource();
        var port = _vm.ConnectPorts[0];
        var request = _vm.RequestedPorts[0];
        _vm.NewPort = "8080";

        var showing = _vm.ShowPairingCodeAsync();

        Assert.True(_vm.IsBusy);
        Assert.False(_vm.CanChange);
        Assert.False(_vm.CanSaveServer);
        Assert.False(_vm.AllowBindingCommand.CanExecute(null));
        Assert.False(_vm.AddPortCommand.CanExecute(null));
        Assert.False(_vm.AddSubnetCommand.CanExecute(null));
        Assert.False(_vm.RemovePortCommand.CanExecute(port));
        Assert.False(_vm.AllowRequestCommand.CanExecute(request));
        Assert.False(_vm.ResetCommand.CanExecute(null));
        Assert.False(_vm.SaveServerCommand.CanExecute(null));

        // A card or All cards ticked meanwhile sends nothing and goes back to what the service has.
        _vm.Cards[1].Chosen = true;
        _vm.AllCards = true;
        Assert.False(_vm.Cards[1].Chosen);
        Assert.False(_vm.AllCards);
        Assert.Equal(1, _elevation.Runs);

        _elevation.Prompt.SetResult();
        await showing;
        Assert.False(_vm.IsBusy);
        Assert.True(_vm.AddPortCommand.CanExecute(null));
        Assert.True(_vm.ResetCommand.CanExecute(null));
    }

    [Fact]
    public async Task TenThousandLogEntriesArriveAsOneChange()
    {
        var entries = Enumerable.Range(0, 10_000)
            .Select(i => new ConnectionLogEntry(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero).AddSeconds(i), "OADM-SRV", $"192.168.0.{i % 250}:443", "Short", "Connected"))
            .ToList();
        _channel.Answer = r => r.Command == AdminCommands.Log ? new AdminResponse { Ok = true, Log = entries } : null!;
        var changes = 0;
        _vm.Log.CollectionChanged += (_, _) => changes++;

        await _vm.LoadLogAsync();

        Assert.Equal(10_000, _vm.Log.Count);
        Assert.Equal(1, changes);
    }
}
