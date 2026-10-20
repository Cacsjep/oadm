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
        Assert.True(_vm.IsConnectMode);
        Assert.True(_vm.IsConnected);
        Assert.Equal("Not bound yet", _vm.SiteText);
        Assert.Equal(2, _vm.Cards.Count);
        Assert.Equal("Default router", _vm.Cards[1].Hint);
        Assert.False(_vm.Cards[1].Reached);
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

        Assert.StartsWith("Bound to OADM-SRV since ", _vm.BindingText, StringComparison.Ordinal);
        Assert.Equal("Building B", _vm.SiteText);
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

        _vm.Cards[1].Reached = true;
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
        Assert.Equal(("connect", "oadm.example.com:5082"), (request.Arg("mode"), request.Arg("address")));
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
    public async Task WaitModeNeedsNoAddress()
    {
        await _vm.RefreshAsync();

        _vm.ChooseWaitCommand.Execute(null);
        _vm.ServerAddress = "";
        _elevation.IsAdministrator = true;
        await _vm.SaveServerAsync();

        Assert.Equal("wait", _channel.Requests.Last(r => r.Request.Command == AdminCommands.SetServer).Request.Arg("mode"));
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
        Assert.Equal("Status: Connected to 10.0.0.5:5081", _vm.NodeStatusText);
        Assert.True(_vm.IsNodeStatusOk);

        _channel.State = FakeChannel.Sample(serverAddress: "", connected: false, connectionText: "");
        await _vm.RefreshAsync();
        Assert.Equal("Status: Not connected", _vm.NodeStatusText);
        Assert.True(_vm.IsNodeStatusWarning);
        Assert.Null(_vm.ConnectionProblem);

        _channel.State = FakeChannel.Sample(connected: false, connectionText: "Cannot reach 10.0.0.5:5081");
        await _vm.RefreshAsync();
        Assert.Equal("Status: Not connected: Cannot reach 10.0.0.5:5081", _vm.NodeStatusText);
        Assert.True(_vm.IsNodeStatusError);
        Assert.Equal("Cannot reach 10.0.0.5:5081", _vm.ConnectionProblem);

        _channel.State = FakeChannel.Sample(NodeBinding.Bound);
        await _vm.RefreshAsync();
        Assert.Equal("Status: Connected to OADM-SRV · Building B", _vm.NodeStatusText);

        _channel.State = FakeChannel.Sample(NodeBinding.Bound, connected: false, connectionText: "Cannot reach 10.0.0.5:5081");
        await _vm.RefreshAsync();
        Assert.Equal("Status: Not connected: Cannot reach 10.0.0.5:5081", _vm.NodeStatusText);

        _channel.State = new NodeView { NodeId = "a1", Binding = NodeBinding.Open, Mode = NodeLinkMode.Wait, ConnectionText = "Waiting for the server on port 5081" };
        await _vm.RefreshAsync();
        Assert.Equal("Status: Waiting for the server on port 5081", _vm.NodeStatusText);
        Assert.Equal(NodeStatusKind.Neutral, _vm.NodeStatusKind);

        _channel.State = null;
        await _vm.RefreshAsync();
        Assert.Equal("Status: Service not running", _vm.NodeStatusText);
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
        Assert.Equal("Not bound. Binding is closed.", _vm.BindingText);
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

        _vm.ChooseWaitCommand.Execute(null);
        Assert.True(_vm.CanSaveServer);
        _vm.ChooseConnectCommand.Execute(null);
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
}
