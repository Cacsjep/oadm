using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Sites;
using Oadm.Core.Tasks;
using Oadm.Sdk.Testing;
using Oadm.Server.AddDevices;
using Oadm.Server.Devices;
using Oadm.Server.Tests.Support;
using Oadm.Tests.Shared;

using CoreDiscoveryService = Oadm.Core.Discovery.DiscoveryService;
using Proto = Oadm.Contracts.V1;
using SdkStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// Discovery and devices at sites through the server with fake sites (CLAUDE.md "# Sites", "Discovery, everywhere by itself",
/// "Overlapping networks", "Moving devices"): two sites both have 192.168.0.20 with different cameras.
/// </summary>
public sealed class SiteDiscoveryTests
{
    private const string CameraA = "ACCC8E0000A1";
    private const string CameraB = "ACCC8E0000B1";

    [Fact]
    public async Task AddManuallyListsTheDeviceOfEachSiteWithTheSameAddressAndAddsEachAtItsSite()
    {
        var (sites, a, b, network) = Setup();
        network.AtSite(a.Id).Add("192.168.0.20", CameraA, null, "P3265-V");
        network.AtSite(b.Id).Add("192.168.0.20", CameraB, null, "Q6135-LE");
        await using var host = await StartAsync(sites, network);
        var discovery = new Proto.DiscoveryService.DiscoveryServiceClient(host.Invoker);

        var session = await discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "192.168.0.20" });
        var watched = await WatchAsync(discovery, session.SessionId);

        Assert.Equal(string.Empty, session.SiteId);
        var rows = watched.Devices.OrderBy(d => d.Serial).ToList();
        Assert.Equal([CameraA, CameraB], rows.Select(r => r.Serial));
        Assert.Equal(["Building A", "Building B"], rows.Select(r => r.SiteName));
        Assert.Equal([a.Id.ToString(), b.Id.ToString()], rows.Select(r => r.SiteId));
        Assert.All(rows, r => Assert.Equal(Proto.AuthState.PasswordNotSet, r.AuthState));

        var add = new Proto.AddDevicesService.AddDevicesServiceClient(host.Invoker);
        var commit = await add.CommitAsync(new Proto.CommitRequest { SessionId = session.SessionId, DiscoveredIds = { CameraA, CameraB } });
        Assert.Equal(2, commit.DeviceIds.Count);
        var repository = host.Get<DeviceRepository>();
        Assert.Equal(a.Id, (await repository.FindBySerialAsync(CameraA, CancellationToken.None))!.SiteId);
        Assert.Equal(b.Id, (await repository.FindBySerialAsync(CameraB, CancellationToken.None))!.SiteId);
    }

    [Fact]
    public async Task AnImportLineWithASiteProbesOnlyThroughItAndAnExpectedMacAddressKeepsOnlyItsRoute()
    {
        var (sites, a, b, network) = Setup();
        network.AtSite(a.Id).Add("192.168.0.20", CameraA, null);
        network.AtSite(b.Id).Add("192.168.0.20", CameraB, null);
        await using var host = await StartAsync(sites, network);
        var discovery = new Proto.DiscoveryService.DiscoveryServiceClient(host.Invoker);

        var site = await discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "192.168.0.20", Site = "building b", ImportLine = true });
        Assert.Equal(b.Id.ToString(), site.SiteId);
        Assert.Equal(CameraB, Assert.Single((await WatchAsync(discovery, site.SessionId)).Devices).Serial);

        var expected = await discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "192.168.0.20", ExpectedSerial = CameraA, ImportLine = true });
        var row = Assert.Single((await WatchAsync(discovery, expected.SessionId)).Devices);
        Assert.Equal((CameraA, "Building A"), (row.Serial, row.SiteName));

        var unknown = await Assert.ThrowsAsync<RpcException>(async () =>
            await discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "192.168.0.20", Site = "Warehouse" }));
        Assert.Equal((StatusCode.InvalidArgument, "There is no site named Warehouse."), (unknown.StatusCode, unknown.Status.Detail));

        // The line's site is offline: nothing answers, with the reason (the line can wait for the site).
        sites.SetOnline(a.Id, false);
        var offline = await discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "192.168.0.20", Site = "Building A" });
        Assert.Equal(a.Id.ToString(), offline.SiteId);
        var nothing = await WatchAsync(discovery, offline.SessionId);
        Assert.Empty(nothing.Devices);
        Assert.Equal("Site offline: Building A", nothing.Finished!.Problem);
    }

    [Fact]
    public async Task TheSameDeviceThroughTheServerAndASiteIsOneRowThroughTheSite()
    {
        var (sites, a, _, network) = Setup();
        network.Add("192.168.0.20", CameraA, null);
        network.AtSite(a.Id).Add("192.168.0.20", CameraA, null);
        await using var host = await StartAsync(sites, network);
        var discovery = new Proto.DiscoveryService.DiscoveryServiceClient(host.Invoker);

        var session = await discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "192.168.0.20" });
        var watched = await WatchAsync(discovery, session.SessionId);

        var row = Assert.Single(watched.Devices);
        Assert.Equal("Building A", row.SiteName);
        Assert.Equal([string.Empty], row.OtherSites); // also through the server's own network
    }

    [Fact]
    public async Task APortASiteDoesNotAllowIsTheProblemOfTheAddress()
    {
        var (sites, a, b, network) = Setup();
        b.Reaches = _ => false;
        network.AtSite(a.Id).SiteProblem = uri => uri.Port == 8443 ? SiteException.For(a.Id, a.Name, SiteProblem.PortNotAllowed, 8443) : null;
        await using var host = await StartAsync(sites, network);
        var discovery = new Proto.DiscoveryService.DiscoveryServiceClient(host.Invoker);

        var session = await discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "https://192.168.0.30:8443" });
        var watched = await WatchAsync(discovery, session.SessionId);

        Assert.Empty(watched.Devices);
        Assert.Equal("Port 8443 is not allowed at Building A. Allow it in the OADM Site Node app.", watched.Finished!.Problem);
    }

    [Fact]
    public async Task DiscoveryAndRangeScansFindTheDevicesOfTheSites()
    {
        var (sites, a, b, network) = Setup();
        a.Announced.Add(new SiteBrowseResult(CameraA, "Cam - " + CameraA, [IPAddress.Parse("192.168.0.20")], 80, "axis-accc8e0000a1.local"));
        network.AtSite(a.Id).Add("192.168.0.20", CameraA, null);
        b.Map("192.168.0.20", 443, new IPEndPoint(IPAddress.Loopback, 9));
        network.AtSite(b.Id).Add("192.168.0.20", CameraB, null);
        await using var host = await StartAsync(sites, network);
        var discovery = new Proto.DiscoveryService.DiscoveryServiceClient(host.Invoker);

        var scan = await discovery.StartZeroConfAsync(new Proto.Empty());
        await Eventually.TrueAsync(() => host.Get<CoreDiscoveryService>().GetDevices(scan.SessionId).Any(d => d.Model is not null), "the device of site A");
        await discovery.StopScanAsync(scan);
        var zeroConf = await WatchAsync(discovery, scan.SessionId);
        Assert.Contains(zeroConf.Devices, d => d.Serial == CameraA && d.SiteName == "Building A");

        var range = await discovery.StartRangeScanAsync(new Proto.RangeScanRequest { From = "192.168.0.15", To = "192.168.0.25" });
        var ranged = await WatchAsync(discovery, range.SessionId);
        Assert.Contains(ranged.Devices, d => d.Serial == CameraB && d.SiteName == "Building B");
        Assert.Equal(100, ranged.Finished!.ProgressPercent);
    }

    [Fact]
    public async Task TheWaitingListKeepsOneEntryPerSiteAndAddressAndTriesEachThroughItsSite()
    {
        var (sites, a, b, network) = Setup();
        await using var host = await StartAsync(sites, network);
        var waiting = new Proto.WaitingDeviceService.WaitingDeviceServiceClient(host.Invoker);
        await waiting.AddAsync(new Proto.AddWaitingRequest
        {
            Source = "Import from file sites.csv",
            Lines =
            {
                new Proto.WaitingLine { Address = "192.168.0.20", SiteId = a.Id.ToString() },
                new Proto.WaitingLine { Address = "192.168.0.20", SiteId = b.Id.ToString() },
                new Proto.WaitingLine { Address = "192.168.0.21" },
            },
        });
        var list = await waiting.ListAsync(new Proto.ListWaitingRequest());
        Assert.Equal(3, list.TotalCount);
        Assert.Equal(new HashSet<string> { "", "Building A", "Building B" }, list.Devices.Select(d => d.SiteName).ToHashSet());

        // The devices come online: each at its site; 192.168.0.21 answers with different devices at both sites.
        network.AtSite(a.Id).Add("192.168.0.20", CameraA, null);
        network.AtSite(b.Id).Add("192.168.0.20", CameraB, null);
        network.AtSite(a.Id).Add("192.168.0.21", "ACCC8E0000A2", null);
        network.AtSite(b.Id).Add("192.168.0.21", "ACCC8E0000B2", null);
        var added = await host.Get<WaitingDeviceRunner>().RunRoundAsync(null, includeLoginFailed: false, CancellationToken.None);

        Assert.Equal(2, added);
        var repository = host.Get<DeviceRepository>();
        Assert.Equal(a.Id, (await repository.FindBySerialAsync(CameraA, CancellationToken.None))!.SiteId);
        Assert.Equal(b.Id, (await repository.FindBySerialAsync(CameraB, CancellationToken.None))!.SiteId);
        var left = Assert.Single((await waiting.ListAsync(new Proto.ListWaitingRequest())).Devices);
        Assert.Equal(("192.168.0.21", "Answers through Building A and Building B.", true), (left.Address, left.LastResult, left.Held));

        // Held: a scheduled round leaves it alone (no tries counted, nothing written); Try now for all tries it again.
        Assert.Equal(0, await host.Get<WaitingDeviceRunner>().RunRoundAsync(null, includeLoginFailed: false, CancellationToken.None));
        Assert.Equal(1, Assert.Single((await waiting.ListAsync(new Proto.ListWaitingRequest())).Devices).Tries);
        await host.Get<WaitingDeviceRunner>().RunRoundAsync(null, includeLoginFailed: true, CancellationToken.None);
        Assert.Equal(2, Assert.Single((await waiting.ListAsync(new Proto.ListWaitingRequest())).Devices).Tries);
    }

    [Fact]
    public async Task AnEntryAnsweringThroughSeveralSitesWithOneDeviceNotManagedAddsThatOne()
    {
        var (sites, a, b, network) = Setup();
        await using var host = await StartAsync(sites, network);
        network.AtSite(a.Id).Add("192.168.0.21", "ACCC8E0000A2", null);
        network.AtSite(b.Id).Add("192.168.0.21", "ACCC8E0000B2", null);
        await AddDeviceAsync(host, "ACCC8E0000A2", "192.168.0.21", a.Id, SdkStatus.Ok); // managed already at site A
        var waiting = new Proto.WaitingDeviceService.WaitingDeviceServiceClient(host.Invoker);
        await waiting.AddAsync(new Proto.AddWaitingRequest { Source = "Import from file", Lines = { new Proto.WaitingLine { Address = "192.168.0.21" } } });

        Assert.Equal(1, await host.Get<WaitingDeviceRunner>().RunRoundAsync(null, includeLoginFailed: false, CancellationToken.None));

        Assert.Equal(b.Id, (await host.Get<DeviceRepository>().FindBySerialAsync("ACCC8E0000B2", CancellationToken.None))!.SiteId);
        Assert.Empty((await waiting.ListAsync(new Proto.ListWaitingRequest())).Devices);
    }

    [Fact]
    public async Task EntriesOfASiteThatIsOfflineAreLeftAloneAndThoseOfARemovedSiteRemoved()
    {
        var (sites, a, b, network) = Setup();
        await using var host = await StartAsync(sites, network);
        var waiting = new Proto.WaitingDeviceService.WaitingDeviceServiceClient(host.Invoker);
        await waiting.AddAsync(new Proto.AddWaitingRequest
        {
            Source = "Import from file",
            Lines =
            {
                new Proto.WaitingLine { Address = "192.168.0.30", SiteId = a.Id.ToString() },
                new Proto.WaitingLine { Address = "192.168.0.31", SiteId = b.Id.ToString() },
            },
        });
        sites.SetOnline(a.Id, false);
        sites.RemoveSite(b.Id);
        var runner = host.Get<WaitingDeviceRunner>();
        var dials = sites.Dials.Count;

        await runner.RunRoundAsync(null, includeLoginFailed: false, CancellationToken.None);

        var entry = Assert.Single((await waiting.ListAsync(new Proto.ListWaitingRequest())).Devices);
        Assert.Equal(("192.168.0.30", 0, ""), (entry.Address, entry.Tries, entry.LastResult)); // not tried, nothing written
        Assert.Equal(dials, sites.Dials.Count);
        Assert.False(runner.IsChecking(Guid.Parse(entry.Id)));

        sites.SetOnline(a.Id, true);
        await runner.RunRoundAsync(null, includeLoginFailed: false, CancellationToken.None);
        Assert.Equal(1, Assert.Single((await waiting.ListAsync(new Proto.ListWaitingRequest())).Devices).Tries);
    }

    [Fact]
    public async Task AnUnreachableDeviceFoundThroughAnotherSiteMovesThereAndADeviceThatAnswersNeverMoves()
    {
        var (sites, a, b, network) = Setup();
        Pinned(network.AtSite(b.Id).Add("192.168.0.40", CameraA, "pass"));
        Pinned(network.AtSite(b.Id).Add("192.168.0.41", CameraB, "pass"));
        b.Announced.Add(new SiteBrowseResult(CameraA, "Cam - " + CameraA, [IPAddress.Parse("192.168.0.40")], 80, "axis-a.local"));
        b.Announced.Add(new SiteBrowseResult(CameraB, "Cam - " + CameraB, [IPAddress.Parse("192.168.0.41")], 80, "axis-b.local"));
        await using var host = await StartAsync(sites, network);
        var moved = await AddDeviceAsync(host, CameraA, "192.168.0.20", a.Id, SdkStatus.Unreachable, pinned: true);
        var answering = await AddDeviceAsync(host, CameraB, "192.168.0.21", a.Id, SdkStatus.Ok, pinned: true);
        var relocation = Relocation(host, new FakeTimeProvider());

        Assert.Equal(1, await relocation.RunOnceAsync(CancellationToken.None));

        var repository = host.Get<DeviceRepository>();
        var after = (await repository.GetAsync(moved, CancellationToken.None))!;
        Assert.Equal((b.Id, "192.168.0.40"), (after.SiteId, after.Address));
        var still = (await repository.GetAsync(answering, CancellationToken.None))!;
        Assert.Equal((a.Id, "192.168.0.21"), (still.SiteId, still.Address));
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Single(e => e.Action == AuditActions.DeviceMoved);
        Assert.Equal(("system", "Found again: from Building A 192.168.0.20 to Building B 192.168.0.40"), (audit.UserName, audit.Detail));
    }

    [Fact]
    public async Task ADeviceAnnouncedThroughSeveralSitesIsTriedThroughEachUntilOneVerifies()
    {
        var (sites, a, b, network) = Setup();
        var c = sites.AddSite("Building C");
        // B reaches 192.168.0.40 but the device is not there (another network with the same address); C has it.
        Pinned(network.AtSite(c.Id).Add("192.168.0.40", CameraA, "pass"));
        b.Announced.Add(new SiteBrowseResult(CameraA, "Cam - " + CameraA, [IPAddress.Parse("192.168.0.40")], 80, "axis-a.local"));
        c.Announced.Add(new SiteBrowseResult(CameraA, "Cam - " + CameraA, [IPAddress.Parse("192.168.0.40")], 80, "axis-a.local"));
        await using var host = await StartAsync(sites, network);
        var id = await AddDeviceAsync(host, CameraA, "192.168.0.20", a.Id, SdkStatus.Unreachable, pinned: true);

        Assert.Equal(1, await Relocation(host, new FakeTimeProvider()).RunOnceAsync(CancellationToken.None));

        var after = (await host.Get<DeviceRepository>().GetAsync(id, CancellationToken.None))!;
        Assert.Equal((c.Id, "192.168.0.40"), (after.SiteId, after.Address));
    }

    [Fact]
    public async Task TheSiteWhoseNetworksIncludeTheAddressIsTriedFirstAndTheCurrentPlaceNever()
    {
        var (sites, a, b, network) = Setup();
        var c = sites.AddSite("Building C");
        b.Reaches = address => address != "192.168.0.40";
        var host = await StartAsync(sites, network);
        await using (host)
        {
            var device = (await host.Get<DeviceRepository>().GetAsync(
                await AddDeviceAsync(host, CameraA, "192.168.0.40", a.Id, SdkStatus.Unreachable), CancellationToken.None))!;
            var relocation = Relocation(host, new FakeTimeProvider());

            var order = relocation.Order(device,
            [
                (DeviceRoute.ViaSite(b.Id), "192.168.0.40"),
                (DeviceRoute.Local, "192.168.0.40"),
                (DeviceRoute.ViaSite(a.Id), "192.168.0.40"), // where it is now
                (DeviceRoute.ViaSite(c.Id), "192.168.0.40"),
            ]).ToList();

            Assert.Equal([(DeviceRoute.ViaSite(c.Id), "192.168.0.40"), (DeviceRoute.Local, "192.168.0.40"), (DeviceRoute.ViaSite(b.Id), "192.168.0.40")], order);
        }
    }

    [Fact]
    public async Task DevicesOfASiteOfflineForAnHourAreLookedForElsewhere()
    {
        var (sites, _, b, network) = Setup();
        var gone = sites.AddSite("Warehouse", online: false);
        Pinned(network.AtSite(b.Id).Add("192.168.0.40", CameraA, "pass"));
        b.Announced.Add(new SiteBrowseResult(CameraA, "Cam - " + CameraA, [IPAddress.Parse("192.168.0.40")], 80, "axis-a.local"));
        await using var host = await StartAsync(sites, network);
        var id = await AddDeviceAsync(host, CameraA, "192.168.0.20", gone.Id, SdkStatus.Ok, pinned: true);
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var relocation = Relocation(host, time);

        Assert.Equal(0, await relocation.RunOnceAsync(CancellationToken.None)); // offline just now
        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(0, await relocation.RunOnceAsync(CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await relocation.RunOnceAsync(CancellationToken.None));

        Assert.Equal(b.Id, (await host.Get<DeviceRepository>().GetAsync(id, CancellationToken.None))!.SiteId);
    }

    [Fact]
    public async Task ADhcpLeaseMovesASiteDeviceToTheServersOwnNetwork()
    {
        var (sites, a, _, network) = Setup();
        Pinned(network.Add("10.0.0.20", CameraA, "pass"));
        await using var host = await StartAsync(sites, network);
        var id = await AddDeviceAsync(host, CameraA, "192.168.0.20", a.Id, SdkStatus.Ok, pinned: true);

        var result = await host.Get<DeviceAutoAddService>().FollowAsync(CameraA, "10.0.0.20", "DHCP server", CancellationToken.None);

        Assert.Equal(Sdk.Devices.DeviceFollowResult.Moved, result);
        var after = (await host.Get<DeviceRepository>().GetAsync(id, CancellationToken.None))!;
        Assert.Equal(((Guid?)null, "10.0.0.20"), (after.SiteId, after.Address));
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Single(e => e.Action == AuditActions.DeviceMoved);
        Assert.Equal("DHCP server: from Building A 192.168.0.20 to This server 10.0.0.20", audit.Detail);
    }

    [Fact]
    public async Task ADeviceOverHttpNeverMovesToAnotherRouteButWithinItsOwnSite()
    {
        var (sites, a, b, network) = Setup();
        // Over HTTP a node of site B could answer the login itself: that answer never moves a device without a pin.
        network.AtSite(b.Id).Add("192.168.0.40", CameraA, "pass");
        b.Announced.Add(new SiteBrowseResult(CameraA, "Cam - " + CameraA, [IPAddress.Parse("192.168.0.40")], 80, "axis-a.local"));
        await using var host = await StartAsync(sites, network);
        var id = await AddDeviceAsync(host, CameraA, "192.168.0.20", a.Id, SdkStatus.Unreachable);
        var relocation = Relocation(host, new FakeTimeProvider());

        Assert.Equal(0, await relocation.RunOnceAsync(CancellationToken.None));
        Assert.Equal(DeviceAddressChangeResult.NotPinned, await host.Get<DeviceAddressService>().TryRelocateAsync(
            CameraA, "192.168.0.40", DeviceRoute.ViaSite(b.Id), "test", requireUnreachable: true, CancellationToken.None));
        var after = (await host.Get<DeviceRepository>().GetAsync(id, CancellationToken.None))!;
        Assert.Equal((a.Id, "192.168.0.20"), (after.SiteId, after.Address));

        // Its own site announces it at a new address: that move stays allowed. Site B no longer announces it: the browse
        // ends a short real-time grace after the first find, so a second route could be missed on a busy machine.
        b.Announced.Clear();
        network.AtSite(a.Id).Add("192.168.0.41", CameraA, "pass");
        a.Announced.Add(new SiteBrowseResult(CameraA, "Cam - " + CameraA, [IPAddress.Parse("192.168.0.41")], 80, "axis-a.local"));
        Assert.Equal(1, await relocation.RunOnceAsync(CancellationToken.None));
        after = (await host.Get<DeviceRepository>().GetAsync(id, CancellationToken.None))!;
        Assert.Equal((a.Id, "192.168.0.41"), (after.SiteId, after.Address));
    }

    [Fact]
    public async Task APinnedDeviceIsNotMovedToAnotherSiteWhereTheAnswerHasAnotherCertificate()
    {
        var (sites, a, b, network) = Setup();
        using var other = MakeCertificate("imitation");
        var imitation = network.AtSite(b.Id).Add("192.168.0.40", CameraA, "pass");
        imitation.ServesHttps = true;
        imitation.Certificate = other;
        b.Announced.Add(new SiteBrowseResult(CameraA, "Cam - " + CameraA, [IPAddress.Parse("192.168.0.40")], 80, "axis-a.local"));
        await using var host = await StartAsync(sites, network);
        var id = await AddDeviceAsync(host, CameraA, "192.168.0.20", a.Id, SdkStatus.Unreachable, pinned: true);

        Assert.Equal(0, await Relocation(host, new FakeTimeProvider()).RunOnceAsync(CancellationToken.None));

        var after = (await host.Get<DeviceRepository>().GetAsync(id, CancellationToken.None))!;
        Assert.Equal((a.Id, "192.168.0.20"), (after.SiteId, after.Address));
    }

    [Fact]
    public async Task ADhcpLeaseNeverMovesASiteDeviceOverHttpToTheServersNetwork()
    {
        var (sites, a, _, network) = Setup();
        network.Add("10.0.0.20", CameraA, "pass");
        await using var host = await StartAsync(sites, network);
        var id = await AddDeviceAsync(host, CameraA, "192.168.0.20", a.Id, SdkStatus.Ok);

        var result = await host.Get<DeviceAutoAddService>().FollowAsync(CameraA, "10.0.0.20", "DHCP server", CancellationToken.None);

        Assert.Equal(Sdk.Devices.DeviceFollowResult.NotVerified, result);
        var after = (await host.Get<DeviceRepository>().GetAsync(id, CancellationToken.None))!;
        Assert.Equal((a.Id, "192.168.0.20"), (after.SiteId, after.Address));
        Assert.DoesNotContain((await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.DeviceMoved);
    }

    // ---------- helpers ----------

    /// <summary>The certificate every real camera of these tests presents; devices added with <c>pinned</c> pin it.</summary>
    private static readonly X509Certificate2 DeviceCertificate = MakeCertificate("camera");

    private static X509Certificate2 MakeCertificate(string cn)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
    }

    /// <summary>The real camera: serves HTTPS with <see cref="DeviceCertificate"/>.</summary>
    private static void Pinned(FakeAxisDevice device)
    {
        device.ServesHttps = true;
        device.Certificate = DeviceCertificate;
    }

    internal sealed record Watched(IReadOnlyList<Proto.DiscoveredDevice> Devices, Proto.DiscoveredDevice? Finished);

    /// <summary>Every device (latest message per serial) and the final message of a session, until its stream ends.</summary>
    internal static async Task<Watched> WatchAsync(Proto.DiscoveryService.DiscoveryServiceClient discovery, string sessionId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var devices = new Dictionary<string, Proto.DiscoveredDevice>();
        Proto.DiscoveredDevice? finished = null;
        using var call = discovery.WatchDiscovered(new Proto.DiscoverySession { SessionId = sessionId }, cancellationToken: cts.Token);
        while (await call.ResponseStream.MoveNext(cts.Token))
        {
            var message = call.ResponseStream.Current;
            if (message.DiscoveredId.Length > 0)
            {
                devices[message.Serial] = message;
            }

            if (message.ScanFinished)
            {
                finished = message;
            }
        }

        return new Watched([.. devices.Values], finished);
    }

    private static (FakeSiteTransport Sites, FakeSiteTransport.FakeSite A, FakeSiteTransport.FakeSite B, FakeAxisNetwork Network) Setup()
    {
        var sites = new FakeSiteTransport();
        return (sites, sites.AddSite("Building A"), sites.AddSite("Building B"), new FakeAxisNetwork());
    }

    private static Task<TestServerHost> StartAsync(FakeSiteTransport sites, FakeAxisNetwork network) =>
        TestServerHost.StartAsync(network, configureServices: s =>
        {
            s.AddSingleton<ISiteStates>(sites);
            s.AddSingleton<ISiteNetwork>(sites);
        });

    private static async Task<Guid> AddDeviceAsync(TestServerHost host, string serial, string address, Guid? siteId, SdkStatus status, bool pinned = false)
    {
        var device = await host.Get<DeviceRepository>().AddAsync(new Device
        {
            Serial = serial,
            Address = address,
            SiteId = siteId,
            Status = status,
            Scheme = pinned ? DeviceScheme.Https : DeviceScheme.Http,
            CertFingerprintSha256 = pinned ? Convert.ToHexString(SHA256.HashData(DeviceCertificate.RawData)) : null,
            Model = "P3265-V",
        }, CancellationToken.None);
        await host.Get<CredentialStore>().SetAsync(device.Id, "root", "pass", CancellationToken.None);
        return device.Id;
    }

    private static DeviceRelocationHostedService Relocation(TestServerHost host, TimeProvider time) =>
        new(host.Get<CoreDiscoveryService>(), host.Get<DeviceRepository>(), host.Get<DeviceAddressService>(),
            new DeviceRelocationOptions { BrowseDuration = TimeSpan.FromSeconds(20) }, NullLogger<DeviceRelocationHostedService>.Instance,
            host.Get<ISiteStates>(), host.Get<AuditLog>(), time, host.Get<ISiteNetwork>());
}
