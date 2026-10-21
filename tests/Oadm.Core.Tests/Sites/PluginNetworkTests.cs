using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Compliance;
using Oadm.Core.Devices;
using Oadm.Core.LiveView;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Sites;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Compliance;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Tests.Tasks;
using Oadm.Core.Vapix;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Sites;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

using FakeDevice = Oadm.Sdk.Testing.FakeDevice;

namespace Oadm.Core.Tests.Sites;

/// <summary>
/// The host's side of the plugin SDK for sites (docs/spec/sites.md, Plugins and SDK): <see cref="IDeviceNetwork"/> per route,
/// listeners on the server's own network (loopback only here), the relays a plugin opened closed when it stops, and
/// <c>siteSupport</c> enforced for tasks, compliance types, pages and actions.
/// </summary>
public sealed class PluginNetworkTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    // ---------- IDeviceNetwork per route ----------

    [Fact]
    public async Task ASiteRouteAsksTheSiteNodeAndTheServerRouteIsTheServersOwn()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B");
        site.Answering.Add("192.168.0.20");
        site.Names["CAM-B.SITE.LAN"] = [IPAddress.Parse("192.168.0.20")];
        site.LocalAddress = IPAddress.Parse("192.168.0.5");
        site.Reaches = a => a.StartsWith("192.168.0.", StringComparison.Ordinal);
        var networks = new DeviceNetworks(sites, sites);

        var route = networks.For(new FakeDevice { SiteId = site.Id });
        Assert.Equal(site.Id, route.SiteId);
        Assert.True(await route.PingAsync("192.168.0.20", TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.False(await route.PingAsync("192.168.0.21", TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal([IPAddress.Parse("192.168.0.20")], await route.ResolveAsync("cam-b.site.lan", CancellationToken.None));
        Assert.Equal([IPAddress.Parse("10.1.2.3")], await route.ResolveAsync("[10.1.2.3]", CancellationToken.None)); // an address needs no lookup
        Assert.Equal(IPAddress.Parse("192.168.0.5"), await route.LocalAddressTowardsAsync("192.168.0.20", CancellationToken.None));
        Assert.True(route.Reaches(IPAddress.Parse("192.168.0.99")));
        Assert.False(route.Reaches(IPAddress.Parse("10.0.0.99")));

        Assert.Same(LocalDeviceNetwork.Instance, networks.For((Guid?)null));
        Assert.Null(networks.For(new FakeDevice()).SiteId);
        Assert.True(networks.For((Guid?)null).Reaches(IPAddress.Parse("10.0.0.99")));
    }

    [Fact]
    public async Task ASiteThatCannotCarryTheCallIsASiteErrorWithItsText()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B", online: false);

        var offline = await Assert.ThrowsAsync<SiteException>(() => new DeviceNetworks(sites, sites).For(site.Id).PingAsync("192.168.0.20", TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal("Site offline: Building B", offline.Message);

        // A server without the node's network calls refuses every call through a site.
        var none = await Assert.ThrowsAsync<SiteException>(() => new DeviceNetworks(null, sites).For(site.Id).ProbeTcpAsync("192.168.0.20", 80, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(SiteProblem.NotSupported, none.Problem);
        Assert.False(new DeviceNetworks(null, sites).For(site.Id).Reaches(IPAddress.Parse("192.168.0.20")));
    }

    [Fact]
    public async Task TheServersOwnNetworkResolvesAddressesAndFindsLoopbackTowardsLoopback()
    {
        var local = LocalDeviceNetwork.Instance;
        Assert.Equal([IPAddress.Loopback], await local.ResolveAsync("127.0.0.1", CancellationToken.None));
        Assert.Equal(IPAddress.Loopback, await local.LocalAddressTowardsAsync("127.0.0.1", CancellationToken.None));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(await local.ProbeTcpAsync("127.0.0.1", port, Wait, CancellationToken.None));

        // A refused connection is a host that answers: the address is in use (the address-in-use check of Assign IP address).
        listener.Stop();
        Assert.True(await local.ProbeTcpAsync("127.0.0.1", port, Wait, CancellationToken.None));
    }

    // ---------- listeners on the server's own network ----------

    [Fact]
    public async Task AUdpListenerAnswersDatagramsAndKeepsItsFirewallRuleWhileItListens()
    {
        var firewall = new RecordingFirewall();
        await using var scope = new PluginNetworkScope(DeviceNetworks.Local, firewall: firewall, loopbackOnly: true);

        var listener = await scope.ListenUdpAsync(0, "Syslog", CancellationToken.None);
        Assert.Equal(RelayState.Running, listener.State);
        Assert.Null(listener.SiteId);
        Assert.Equal($"Syslog at this server: running", listener.StateText);
        Assert.StartsWith("open OADM Server (Syslog, UDP ", Assert.Single(firewall.CallList), StringComparison.Ordinal);

        using var device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cts = new CancellationTokenSource(Wait);
        await device.SendAsync(Encoding.ASCII.GetBytes("<13>hello"), new IPEndPoint(IPAddress.Loopback, listener.Port), cts.Token);
        var datagram = await listener.ReceiveAsync(cts.Token);
        Assert.Equal("<13>hello", Encoding.ASCII.GetString(datagram.Data.Span));
        await listener.SendAsync(Encoding.ASCII.GetBytes("ok"), datagram.Remote, cts.Token);
        Assert.Equal("ok", Encoding.ASCII.GetString((await device.ReceiveAsync(cts.Token)).Buffer));

        await listener.DisposeAsync();
        Assert.Equal(0, scope.OpenCount);
        Assert.StartsWith("close OADM Server (Syslog, UDP ", firewall.CallList[^1], StringComparison.Ordinal);
        Assert.Equal(2, firewall.CallList.Count);
    }

    [Fact]
    public async Task ATcpListenerHandsOverEveryConnection()
    {
        await using var scope = new PluginNetworkScope(DeviceNetworks.Local, loopbackOnly: true);
        var listener = await scope.ListenTcpAsync(0, "Reports", CancellationToken.None);

        using var cts = new CancellationTokenSource(Wait);
        using var device = new TcpClient(AddressFamily.InterNetwork);
        await device.ConnectAsync(IPAddress.Loopback, listener.Port, cts.Token);
        await using var connection = await listener.AcceptAsync(cts.Token);
        Assert.Null(connection.SiteId);
        await device.GetStream().WriteAsync(Encoding.ASCII.GetBytes("ping"), cts.Token);
        var buffer = new byte[4];
        await connection.Stream.ReadExactlyAsync(buffer, cts.Token);
        Assert.Equal("ping", Encoding.ASCII.GetString(buffer));
    }

    [Fact]
    public async Task APortInUseSaysSoAndIsTriedAgain()
    {
        var time = new FakeTimeProvider();
        using var other = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            other.ExclusiveAddressUse = true;
        }

        other.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)other.LocalEndPoint!).Port;
        await using var scope = new PluginNetworkScope(DeviceNetworks.Local, time: time, loopbackOnly: true);

        var listener = await scope.ListenUdpAsync(port, "NTP", CancellationToken.None);
        Assert.Equal(RelayState.PortInUse, listener.State);
        Assert.Equal($"NTP at this server: UDP {port} is used by another program", listener.StateText);

        var changed = 0;
        listener.StateChanged += (_, _) => Interlocked.Increment(ref changed);
        other.Close();
        time.Advance(LocalListener.RetryInterval);
        await Eventually.TrueAsync(() => listener.State == RelayState.Running, "bound after the retry");
        Assert.True(Volatile.Read(ref changed) > 0);
    }

    // ---------- the plugin's scope ----------

    [Fact]
    public async Task DisposingTheScopeClosesEveryListenerAndRelayThePluginLeftOpen()
    {
        var relays = new RecordingRelays();
        var site = relays.Add("Building B");
        var scope = new PluginNetworkScope(DeviceNetworks.Local, relays, loopbackOnly: true);
        var sites = scope.Sites!;
        var raised = 0;
        sites.SitesChanged += (_, _) => raised++;

        var kept = await sites.OpenUdpRelayAsync(site, 123, "NTP", CancellationToken.None);
        var closedByPlugin = await sites.OpenTcpRelayAsync(site, 8080, "Reports", CancellationToken.None);
        var listener = await scope.ListenUdpAsync(0, "Syslog", CancellationToken.None);
        await closedByPlugin.DisposeAsync();
        Assert.Equal(2, scope.OpenCount);
        Assert.Equal("Building B", Assert.Single(sites.List).Name);
        relays.Raise();
        Assert.Equal(1, raised);

        await scope.DisposeAsync();

        Assert.All(relays.Opened, r => Assert.True(r.IsDisposed));
        Assert.Equal(RelayState.Running, kept.State); // the wrapper reads the relay
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.ListenUdpAsync(0, "Syslog", CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sites.OpenUdpRelayAsync(site, 123, "NTP", CancellationToken.None));
        relays.Raise();
        Assert.Equal(1, raised); // a stopped plugin hears nothing
        _ = listener;
    }

    [Fact]
    public async Task ACorePluginGetsTheNetworkAndItsRelaysCloseWhenItStops()
    {
        var registry = new PluginRegistry();
        var devices = new FakeDeviceRepository();
        var vapix = new FakeVapixClientFactory();
        var relays = new RecordingRelays();
        var siteId = relays.Add("Building B");
        await using var engine = new TaskEngine(new InMemoryTaskStore(), registry, devices, vapix);
        await using var host = new CorePluginHost(registry, devices, vapix, engine, new InMemoryPluginSettingsProvider(),
            networks: () => new PluginNetworkScope(DeviceNetworks.Local, relays, loopbackOnly: true));
        var plugin = new NetworkPlugin(siteId);
        Assert.True(registry.RegisterCorePlugin(plugin, new PluginOrigin("test.ntp", "1.0", null) { DeclaredSiteSupport = SiteSupport.Full }));

        await host.StartAllAsync(CancellationToken.None);
        Assert.NotNull(plugin.Context!.Network);
        Assert.NotNull(plugin.Context.Sites);
        var relay = Assert.Single(relays.Opened);
        Assert.False(relay.IsDisposed);

        await host.StopAllAsync(CancellationToken.None);
        Assert.True(relay.IsDisposed);
    }

    // ---------- siteSupport ----------

    [Fact]
    public void SiteSupportComesFromThePluginOrItsPackageAndMissingIsNone()
    {
        var undeclared = new PluginOrigin("third.party", "1.0", null);
        var full = undeclared with { DeclaredSiteSupport = SiteSupport.Full };
        Assert.Equal(SiteSupport.None, undeclared.SiteSupport);
        Assert.Equal(SiteSupport.Full, full.SiteSupport);
        Assert.Equal(SiteSupport.Full, PluginOrigin.FromAssembly(typeof(PluginNetworkTests).Assembly).SiteSupport);

        var plain = new DelegateTaskPlugin("t.plain", (_, _, _) => Task.CompletedTask);
        var none = new SiteTask("t.none", SiteSupport.None);
        Assert.Equal(SiteSupport.Full, new RegisteredTaskPlugin(plain, null, full).SiteSupport);
        Assert.Equal(SiteSupport.None, new RegisteredTaskPlugin(plain, null, undeclared).SiteSupport);
        Assert.Equal(SiteSupport.None, new RegisteredTaskPlugin(none, null, full).SiteSupport); // the task overrides its package

        var atSite = new FakeDevice { SiteId = Guid.NewGuid() };
        Assert.Equal(SiteTexts.NotAvailableAtSite, new RegisteredTaskPlugin(none, null, full).SiteRefusal(atSite));
        Assert.Null(new RegisteredTaskPlugin(none, null, full).SiteRefusal(new FakeDevice()));
        Assert.Equal(SiteTexts.NotAvailableAtSite, TaskSupportReasons.ForSiteSupport(SiteSupport.None, atSite));
        Assert.Null(TaskSupportReasons.ForSiteSupport(SiteSupport.Full, atSite));
    }

    [Fact]
    public async Task ATaskOfAPackageWithoutSiteSupportFailsOnADeviceAtASiteBeforeItRuns()
    {
        var registry = new PluginRegistry();
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B");
        var devices = new ListRepository(new FakeDevice { SiteId = site.Id, SiteName = "Building B" }, new FakeDevice());
        var store = new InMemoryTaskStore();
        await using var engine = new TaskEngine(store, registry, devices, new FakeVapixClientFactory(),
            options: new TaskEngineOptions { Networks = new DeviceNetworks(sites, sites) });
        var ran = new List<(Guid Device, Guid? Route)>();
        Assert.True(registry.RegisterTaskPlugin(new DelegateTaskPlugin("t.none", (ctx, device, _) =>
        {
            lock (ran)
            {
                ran.Add((device.Id, ctx.Network!.SiteId));
            }

            return Task.CompletedTask;
        }), new PluginOrigin("third.party", "1.0", null)));
        Assert.True(registry.RegisterTaskPlugin(new DelegateTaskPlugin("t.full", (ctx, device, _) =>
        {
            lock (ran)
            {
                ran.Add((device.Id, ctx.Network!.SiteId));
            }

            return Task.CompletedTask;
        }), new PluginOrigin("bundled", "1.0", null) { DeclaredSiteSupport = SiteSupport.Full }));

        var refused = Assert.Single(await engine.RunAsync("t.none", [devices.Items[0].Id], null, "o", CancellationToken.None));
        await engine.WaitForCompletionAsync(refused, CancellationToken.None).WaitAsync(Wait);
        var record = (await store.GetAsync(refused, CancellationToken.None))!;
        Assert.Equal(TaskState.Failed, record.State);
        Assert.Equal(SiteTexts.NotAvailableAtSite, record.Devices.Single().Message);
        Assert.Empty(ran);

        var ok = await engine.RunAsync("t.full", [devices.Items[0].Id, devices.Items[1].Id], null, "o", CancellationToken.None);
        foreach (var id in ok)
        {
            await engine.WaitForCompletionAsync(id, CancellationToken.None).WaitAsync(Wait);
        }

        Assert.Equal([(devices.Items[0].Id, (Guid?)site.Id), (devices.Items[1].Id, null)], ran.OrderBy(r => r.Route is null));
    }

    [Fact]
    public async Task APageOfAPackageWithoutSiteSupportCannotReachADeviceAtASite()
    {
        var atSite = new FakeDevice { SiteId = Guid.NewGuid() };
        var local = new FakeDevice();
        var devices = new ListRepository(atSite, local);
        var vapix = new FakeVapixClientFactory();

        var guarded = SiteSupportGuard.Guard(vapix, devices, SiteSupport.None);
        var refused = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => guarded.CreateAsync(atSite.Id, CancellationToken.None));
        Assert.Equal(SiteTexts.NotAvailableAtSite, refused.Message);
        Assert.NotNull(await guarded.CreateAsync(local.Id, CancellationToken.None));
        Assert.Same(vapix, SiteSupportGuard.Guard(vapix, devices, SiteSupport.Full));
    }

    [Fact]
    public async Task TheServersFactoryAndEventStreamsRefuseSiteDevicesWithoutAnExtraRead()
    {
        await using var database = await TestDatabase.CreateAsync();
        var rows = database.Get<DeviceRepository>();
        using var factory = new VapixClientFactory(rows, database.Get<CredentialStore>(), new PlainConnector());
        using var streams = new DeviceEventStreams(rows, database.Get<CredentialStore>());
        var local = await rows.AddAsync(new Device { Id = Guid.NewGuid(), Serial = "ACCC8E000011", Address = "10.0.0.20", Status = DeviceStatus.Ok }, CancellationToken.None);
        var atSite = await rows.AddAsync(new Device { Id = Guid.NewGuid(), Serial = "ACCC8E000012", Address = "192.168.0.20", SiteId = Guid.NewGuid(), Status = DeviceStatus.Ok }, CancellationToken.None);
        var devices = new CountingRepository(rows);

        var vapix = SiteSupportGuard.Guard(factory, devices, SiteSupport.None);
        Assert.NotNull(await vapix.CreateAsync(local.Id, CancellationToken.None));
        var refused = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => vapix.CreateAsync(atSite.Id, CancellationToken.None));
        Assert.Equal(SiteTexts.NotAvailableAtSite, refused.Message);
        var events = SiteSupportGuard.Guard(streams, devices, SiteSupport.None)!;
        refused = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => events.OpenAsync(atSite.Id, CancellationToken.None));
        Assert.Equal(SiteTexts.NotAvailableAtSite, refused.Message);

        // The server's own factory and event streams decide from the row they read anyway: no second read per call.
        Assert.Equal(0, devices.Finds);
    }

    [Fact]
    public async Task ACorePluginWithoutSiteSupportGetsNoNetworkIntoASiteAndNoRelays()
    {
        var registry = new PluginRegistry();
        var devices = new FakeDeviceRepository();
        var vapix = new FakeVapixClientFactory();
        var relays = new RecordingRelays();
        var siteId = relays.Add("Building B");
        var sites = new FakeSiteTransport();
        await using var engine = new TaskEngine(new InMemoryTaskStore(), registry, devices, vapix);
        await using var host = new CorePluginHost(registry, devices, vapix, engine, new InMemoryPluginSettingsProvider(),
            networks: () => new PluginNetworkScope(new DeviceNetworks(sites, sites), relays, loopbackOnly: true));
        var plugin = new ContextPlugin();
        Assert.True(registry.RegisterCorePlugin(plugin, new PluginOrigin("third.party", "1.0", null)));

        await host.StartAllAsync(CancellationToken.None);

        var context = plugin.Context!;
        Assert.Null(context.Sites);
        var network = context.Network!;
        var refused = Assert.Throws<DeviceNotCompatibleException>(() => network.ForDevice(new FakeDevice { SiteId = siteId }));
        Assert.Equal(SiteTexts.NotAvailableAtSite, refused.Message);
        Assert.Throws<DeviceNotCompatibleException>(() => network.ForSite(siteId));
        Assert.Null(network.ForDevice(new FakeDevice()).SiteId);
        Assert.Null(network.ForSite(null).SiteId);
        await using (await network.ListenUdpAsync(0, "Syslog", CancellationToken.None))
        {
        }

        Assert.Empty(relays.Opened);
    }

    [Fact]
    public async Task AComplianceTypeWithoutSiteSupportIsNotCheckedAtASite()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B");
        await using var rig = await ComplianceRig.StartAsync(sites: sites);
        rig.Plugin.Read.SiteSupport = SiteSupport.None;
        var atSite = await rig.Devices.AddAsync(new Oadm.Core.Devices.Device
        {
            Serial = "ACCC8E200001",
            Address = "192.168.0.20",
            SiteId = site.Id,
            Model = "P3265-V",
            FirmwareVersion = "12.11.77",
            Status = DeviceStatus.Ok,
            Category = DeviceCategory.Camera,
        }, CancellationToken.None);
        var local = await rig.AddDeviceAsync("10.0.0.20");

        var check = await rig.SaveAsync("test.read");

        await rig.WaitForAsync(check.Id, local.Id, ComplianceOutcome.Passed);
        await Eventually.TrueAsync(() => rig.Result(check.Id, atSite.Id) is { Outcome: ComplianceOutcome.NotChecked, Detail: SiteTexts.NotAvailableAtSite }, "Not checked at the site");
        Assert.DoesNotContain(atSite.Id, rig.Plugin.Read.Evaluations);
    }

    // ---------- Oadm.Sdk.Testing ----------

    [Fact]
    public async Task TheTestingFakesGiveRelaysTheStatesOfTheirSitesAndCarryConnectionsInMemory()
    {
        var sites = new FakeSites();
        var site = sites.Add("Building B");
        var changes = 0;
        sites.SitesChanged += (_, _) => changes++;
        var udp = (InMemoryUdpRelay)await sites.OpenUdpRelayAsync(site.Id, 514, "Syslog", CancellationToken.None);
        Assert.Equal("Syslog at Building B: waiting for approval in the OADM Site Node app", udp.StateText);

        site.AllowedPorts.Add((SiteProtocol.Udp, 514));
        sites.Update();
        Assert.Equal(RelayState.Running, udp.State);
        site.IsOnline = false;
        sites.Update();
        Assert.Equal(RelayState.SiteOffline, udp.State);
        Assert.Equal(2, changes);
        await Assert.ThrowsAsync<ArgumentException>(() => sites.OpenUdpRelayAsync(Guid.NewGuid(), 514, "Syslog", CancellationToken.None));

        var network = new FakePluginNetwork(sites);
        Assert.Same(site.Network, network.ForDevice(new FakeDevice { SiteId = site.Id }));
        Assert.Same(network.Local, network.ForSite(null));
        var tcp = (InMemoryTcpRelay)await network.ListenTcpAsync(8080, "Reports", CancellationToken.None);
        Assert.Equal("Reports at this server: running", tcp.StateText);
        using var cts = new CancellationTokenSource(Wait);
        var device = tcp.Connect(new IPEndPoint(IPAddress.Parse("10.0.0.20"), 50000));
        await using var connection = await tcp.AcceptAsync(cts.Token);
        Assert.Equal(IPAddress.Parse("10.0.0.20"), connection.Remote.Address);
        await device.WriteAsync(Encoding.ASCII.GetBytes("hi"), cts.Token);
        var buffer = new byte[2];
        await connection.Stream.ReadExactlyAsync(buffer, cts.Token);
        Assert.Equal("hi", Encoding.ASCII.GetString(buffer));
        await connection.DisposeAsync();
        Assert.Equal(0, await device.ReadAsync(buffer, cts.Token)); // the plugin closed it
    }

    // ---------- helpers ----------

    private sealed class ListRepository(params IDeviceInfo[] devices) : IDeviceRepository
    {
        public IDeviceInfo[] Items { get; } = devices;

        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>(Items);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(d => d.Id == id));
    }

    private sealed class CountingRepository(IDeviceRepository inner) : IDeviceRepository
    {
        private int _finds;

        public int Finds => Volatile.Read(ref _finds);

        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct)
        {
            Interlocked.Increment(ref _finds);
            return inner.FindAsync(id, ct);
        }
    }

    /// <summary>Real clients that are never used to send anything.</summary>
    private sealed class PlainConnector : IVapixConnector
    {
        public VapixClient Connect(VapixConnectionOptions options) => VapixClient.Create(options);
    }

    /// <summary>Keeps the context it was started with.</summary>
    private sealed class ContextPlugin : ICorePlugin
    {
        public ICorePluginContext? Context { get; private set; }

        public string Id => "third.party.page";

        public string DisplayName => "Third party";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(ICorePluginContext context, CancellationToken ct)
        {
            Context = context;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class SiteTask(string id, SiteSupport support) : ITaskPlugin
    {
        public string Id => id;

        public string DisplayName => id;

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public SiteSupport? SiteSupport => support;

        public bool CanRun(IDeviceInfo device) => true;

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Relays into sites without a node: records what was opened.</summary>
    private sealed class RecordingRelays : ISiteRelays
    {
        private readonly FakeSites _sites = new();

        public List<InMemoryRelay> Opened { get; } = [];

        public IReadOnlyList<ISiteInfo> BoundSites => _sites.List;

        public event EventHandler? SitesChanged;

        public Guid Add(string name) => _sites.Add(name).Id;

        public void Raise() => SitesChanged?.Invoke(this, EventArgs.Empty);

        public IUdpRelay OpenUdp(Guid siteId, int port, string reason) => Record(_sites.OpenUdpRelayAsync(siteId, port, reason, CancellationToken.None).Result);

        public ITcpRelay OpenTcp(Guid siteId, int port, string reason) => Record(_sites.OpenTcpRelayAsync(siteId, port, reason, CancellationToken.None).Result);

        private T Record<T>(T relay)
            where T : IRelay
        {
            Opened.Add((InMemoryRelay)(object)relay);
            _sites.Sites[((IRelay)relay).SiteId!.Value].AllowedPorts.Add((relay.Protocol, relay.Port));
            ((InMemoryRelay)(object)relay).SetState(RelayState.Running);
            return relay;
        }
    }

    private sealed class NetworkPlugin(Guid siteId) : ICorePlugin
    {
        public ICorePluginContext? Context { get; private set; }

        public string Id => "test.ntp";

        public string DisplayName => "Test NTP";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public async Task StartAsync(ICorePluginContext context, CancellationToken ct)
        {
            Context = context;
            await context.Sites!.OpenUdpRelayAsync(siteId, 123, "NTP", ct);
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
