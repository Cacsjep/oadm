using Oadm.SiteNode.Admin;
using Oadm.SiteNode.AdminChannel;
using Oadm.SiteNode.Cli;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.State;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests;

/// <summary>The app's and the command line's commands: reading (the pairing code too) is open, every change needs an administrator.</summary>
public sealed class AdminTests
{
    [Fact]
    public async Task EveryoneReadsTheStateAndTheCodeButOnlyAdministratorsChange()
    {
        await using var node = await TestNode.StartAsync();
        var handler = node.Get<AdminHandler>();

        var state = handler.Handle(AdminRequest.Of(AdminCommands.State), isAdmin: false);
        Assert.True(state.Ok);
        Assert.Equal(NodeBinding.Open, state.State!.Binding);
        Assert.Equal(24, state.State.BindingHoursLeft);
        // Not bound: the state carries the code and how long it lasts.
        Assert.Equal(Sites.Binding.PairingCode.Format(node.Code), state.State.PairingCode);
        Assert.NotNull(state.State.PairingCodeValidUntil);
        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.Log), isAdmin: false).Ok);
        var code = handler.Handle(AdminRequest.Of(AdminCommands.PairingCode), isAdmin: false);
        Assert.Equal(Sites.Binding.PairingCode.Format(node.Code), code.PairingCode);

        foreach (var command in new[] { AdminCommands.Reset, AdminCommands.AllowBinding, AdminCommands.SetServer, AdminCommands.AddPort, AdminCommands.AcceptServer })
        {
            var refused = handler.Handle(AdminRequest.Of(command), isAdmin: false);
            Assert.False(refused.Ok);
            Assert.Equal(AdminTexts.NeedsAdministrator, refused.Error);
        }
    }

    [Fact]
    public async Task ABoundNodeGivesItsCodeForANewServerIdentityToAdministratorsOnly()
    {
        await using var node = await TestNode.StartAsync(c =>
        {
            c.BoundServers.Add(new BoundServer { Name = "OADM-SRV", Fingerprint = "AB12", BoundUtc = DateTimeOffset.UnixEpoch, SiteName = "Building B" });
            c.BindingSecondsLeft = 0;
            c.NewServerIdentity = "0123456789ABCDEF";
            c.NewServerAccepted = true;
        });
        var handler = node.Get<AdminHandler>();

        Assert.Empty(handler.Handle(AdminRequest.Of(AdminCommands.State), isAdmin: false).State!.PairingCode);
        Assert.Equal(AdminTexts.NeedsAdministrator, handler.Handle(AdminRequest.Of(AdminCommands.PairingCode), isAdmin: false).Error);
        var admin = handler.Handle(AdminRequest.Of(AdminCommands.PairingCode), isAdmin: true);
        Assert.True(admin.Ok);
        Assert.Equal(Sites.Binding.PairingCode.Format(node.Get<BindingState>().PairingCode), admin.PairingCode);
        Assert.NotNull(admin.PairingCodeValidUntil);
    }

    [Fact]
    public async Task AddPortNeverMarksAPortAsTheServersEvenWhenTheServerAskedForIt()
    {
        await using var node = await TestNode.StartAsync();
        var handler = node.Get<AdminHandler>();
        var store = node.Get<NodeStore>();
        node.Get<PortPolicy>().Request([new PortEntry(PortUse.Listen, PortProtocol.Udp, 123, "NTP server")]);

        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.AddPort, ("use", "listen"), ("protocol", "udp"), ("port", "123")), true).Ok);

        var port = store.Read(c => c.Allowed.Single(p => p.Port == 123));
        Assert.False(port.ByServer); // the administrator's: a release of the server never removes it
        Assert.False(node.Get<PortPolicy>().Release(port));
        Assert.Contains(store.Read(c => c.Allowed.ToList()), p => p.Port == 123);
    }

    [Fact]
    public async Task AdministratorsChangeServerNetworksAndPorts()
    {
        await using var node = await TestNode.StartAsync();
        var handler = node.Get<AdminHandler>();
        var store = node.Get<NodeStore>();

        var saved = handler.Handle(AdminRequest.Of(AdminCommands.SetServer, ("address", "oadm.example.com")), true);
        Assert.True(saved.Ok);
        Assert.Equal("oadm.example.com", store.Read(c => c.ServerAddress));
        // Not bound: the save (an administrator's) answers with the pairing code, so the app shows it at once.
        Assert.Equal(Sites.Binding.PairingCode.Format(node.Code), saved.PairingCode);
        Assert.False(handler.Handle(AdminRequest.Of(AdminCommands.SetServer, ("address", "not a host!")), true).Ok);
        Assert.False(handler.Handle(AdminRequest.Of(AdminCommands.SetServer, ("address", "")), true).Ok); // the node always needs a server
        Assert.Equal("oadm.example.com", store.Read(c => c.ServerAddress));

        // Choosing a card unticks All cards, ticking All cards clears the choice (the test node starts with the camera card).
        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.AllCards, ("on", "true")), true).Ok);
        Assert.True(store.Read(c => c.AllCards));
        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.SetCard, ("id", "Office"), ("reached", "true")), true).Ok);
        Assert.True(store.Read(c => c.Cards["office"]));
        Assert.False(store.Read(c => c.AllCards));
        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.SetCard, ("id", "cam"), ("reached", "true")), true).Ok);
        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.SetCard, ("id", "office"), ("reached", "false")), true).Ok);
        Assert.Equal(["cam"], store.Read(c => c.Cards.Where(e => e.Value).Select(e => e.Key).ToList()));
        Assert.False(handler.Handle(AdminRequest.Of(AdminCommands.SetCard, ("id", "wifi"), ("reached", "true")), true).Ok);
        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.AllCards, ("on", "true")), true).Ok);
        Assert.True(store.Read(c => c.AllCards));
        Assert.Empty(store.Read(c => c.Cards));

        // Unchoosing the last card is all cards again; All cards off without a chosen card changes nothing.
        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.SetCard, ("id", "cam"), ("reached", "true")), true).Ok);
        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.SetCard, ("id", "cam"), ("reached", "false")), true).Ok);
        Assert.True(store.Read(c => c.AllCards));
        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.AllCards, ("on", "false")), true).Ok);
        Assert.True(store.Read(c => c.AllCards));

        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.AddSubnet, ("subnet", "10.20.5.0/16")), true).Ok);
        Assert.Equal(["10.20.0.0/16"], store.Read(c => c.ExtraSubnets));
        Assert.False(handler.Handle(AdminRequest.Of(AdminCommands.AddSubnet, ("subnet", "10.20")), true).Ok);
        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.RemoveSubnet, ("subnet", "10.20.0.0/16")), true).Ok);
        Assert.Empty(store.Read(c => c.ExtraSubnets));

        node.Get<PortPolicy>().Request([new PortEntry(PortUse.Listen, PortProtocol.Udp, 123, "NTP server")]);
        var view = handler.Handle(AdminRequest.Of(AdminCommands.AllowRequest, ("use", "listen"), ("protocol", "udp"), ("port", "123")), true).State!;
        Assert.Empty(view.Requested);
        Assert.Contains(view.Allowed, p => p is { Use: PortUse.Listen, Protocol: PortProtocol.Udp, Port: 123, Reason: "NTP server" });
        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.RemovePort, ("use", "connect"), ("protocol", "tcp"), ("port", "554")), true).Ok);
        Assert.DoesNotContain(store.Read(c => c.Allowed), p => p.Port == 554);
        Assert.False(handler.Handle(AdminRequest.Of(AdminCommands.AddPort, ("port", "0")), true).Ok);
    }

    [Fact]
    public async Task AllowBindingAndResetWorkFromTheApp()
    {
        await using var node = await TestNode.StartAsync();
        var handler = node.Get<AdminHandler>();
        var code = node.Code;

        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.AllowBinding), true).Ok);
        Assert.NotEqual(code, node.Code);
        var id = node.Get<NodeStore>().Read(c => c.Id);
        Assert.True(handler.Handle(AdminRequest.Of(AdminCommands.Reset), true).Ok);
        Assert.NotEqual(id, node.Get<NodeStore>().Read(c => c.Id));
        Assert.False(handler.Handle(AdminRequest.Of(AdminCommands.AcceptServer), true).Ok);
    }

    [Fact]
    public async Task TheLocalChannelAnswersOverAPipeOrSocket()
    {
        using var folder = new TempFolder();
        var endpoint = AdminEndpoint.ForDataDirectory(folder.Path);
        await using var node = await TestNode.StartAsync(options: o => o.AdminEndpoint = endpoint);
        var client = new AdminClient(endpoint);

        AdminResponse response = new();
        await Sdk.Testing.Eventually.TrueAsync(async () => (response = await client.SendAsync(AdminRequest.Of(AdminCommands.State), CancellationToken.None)).Ok, "the channel answers");

        Assert.Equal(node.Get<NodeStore>().Read(c => c.Id), response.State!.NodeId);
        var unknown = await client.SendAsync(AdminRequest.Of("format-disk"), CancellationToken.None);
        Assert.False(unknown.Ok);
    }

    [Fact]
    public async Task WithoutTheServiceTheClientSaysSo()
    {
        using var folder = new TempFolder();
        var response = await new AdminClient(AdminEndpoint.ForDataDirectory(folder.Path)).SendAsync(AdminRequest.Of(AdminCommands.State), CancellationToken.None);
        Assert.False(response.Ok);
        Assert.Equal(AdminTexts.ServiceNotRunning, response.Error);
    }

    [Theory]
    [InlineData("status", "", AdminCommands.State)]
    [InlineData("pairing-code", "", AdminCommands.PairingCode)]
    [InlineData("server", "oadm.example.com", AdminCommands.SetServer)]
    [InlineData("allow-binding", "", AdminCommands.AllowBinding)]
    [InlineData("networks", "", AdminCommands.State)]
    [InlineData("networks", "card eth1 on", AdminCommands.SetCard)]
    [InlineData("networks", "all off", AdminCommands.AllCards)]
    [InlineData("networks", "add 10.20.0.0/16", AdminCommands.AddSubnet)]
    [InlineData("ports", "", AdminCommands.State)]
    [InlineData("ports", "allow connect tcp 8443", AdminCommands.AllowRequest)]
    [InlineData("ports", "dismiss listen udp 123", AdminCommands.DismissRequest)]
    [InlineData("accept-server", "", AdminCommands.AcceptServer)]
    [InlineData("reset", "", AdminCommands.Reset)]
    [InlineData("log", "20", AdminCommands.Log)]
    public void CommandLineVerbsBecomeRequests(string verb, string args, string command)
    {
        var request = CommandLine.Request(verb, args.Length == 0 ? [] : args.Split(' '));
        Assert.Equal(command, request?.Command);
    }

    [Theory]
    [InlineData("status", "x")]
    [InlineData("server", "--wait")]
    [InlineData("networks", "card eth1 maybe")]
    [InlineData("ports", "allow both tcp 80")]
    [InlineData("format", "")]
    public void UnknownVerbsAreUsageErrors(string verb, string args) =>
        Assert.Null(CommandLine.Request(verb, args.Length == 0 ? [] : args.Split(' ')));

    [Fact]
    public void TheDataFolderOptionIsTakenOut()
    {
        var (verb, rest, dataDirectory) = CommandLine.Split(["--data-dir", "C:/node", "networks", "add", "10.0.0.0/8"]);
        Assert.Equal("networks", verb);
        Assert.Equal(["add", "10.0.0.0/8"], rest);
        Assert.Equal("C:/node", dataDirectory);
        Assert.Null(CommandLine.Split([]).Verb);
    }

    [Fact]
    public void StatusTextNamesStateServerAndConnections()
    {
        var text = CommandLine.Status(new NodeView
        {
            NodeId = "a1b2",
            Version = "3.7.0",
            Binding = NodeBinding.Open,
            BindingHoursLeft = 22.4,
            ServerAddress = "10.0.0.5",
            ConnectionText = AdminTexts.CannotReach("10.0.0.5:5081"),
            ShortConnections = 3,
        });

        Assert.Contains("Not bound. Binding open for 23 more hours.", text, StringComparison.Ordinal);
        Assert.Contains("Server: 10.0.0.5", text, StringComparison.Ordinal);
        Assert.Contains("Cannot reach 10.0.0.5:5081", text, StringComparison.Ordinal);
        Assert.Contains("Connections: 3 short, 0 permanent", text, StringComparison.Ordinal);
    }
}
