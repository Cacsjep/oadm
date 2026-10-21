using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;

using Oadm.Core.Devices;
using Oadm.Core.Sites;
using Oadm.Core.Vapix;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Sites;

/// <summary>
/// What a slow or busy site link must never turn into (docs/spec/sites.md, Large transfers, Timeouts, Site errors are not
/// device errors): a page relayed by Web View holds no large-transfer slot, a wait for a slot ends inside the request's
/// timeout as "busy" (a task waits and says so), status reads never wait behind long requests, a dropped answer frees its
/// slot, and a device copy read before a newer change never overwrites it.
/// </summary>
public sealed class SiteLinkTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ARelayedAnswerTakesNoSlotAndAnotherWaitIsBusyInsideItsTimeout()
    {
        await using var device = LoopbackDevice.StartHttp();
        using var sites = new GatedSites(slots: 1);
        var site = sites.Fake.AddSite("Building B").Map("192.168.0.20", 80, device.EndPoint);
        var router = new DeviceRouter(sites, sites);
        using var client = VapixClient.Create(Options(site.Id), router);
        using var held = await router.EnterLargeTransferAsync(DeviceRoute.ViaSite(site.Id), CancellationToken.None); // every slot taken

        // Web View: a page (or an MJPEG stream, a long poll) is passed on as it arrives and needs no slot.
        using (var relayed = Request("/axis-cgi/page.cgi"))
        {
            relayed.Options.Set(VapixRequestOptions.StreamResponse, true);
            relayed.Options.Set(VapixClient.RelayedOption, true);
            using var response = await client.SendAsync(relayed, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Anyone else outside a task waits inside the request's timeout and then hears that the site is busy.
        using var report = Request("/axis-cgi/serverreport.cgi");
        report.Options.Set(VapixRequestOptions.StreamResponse, true);
        report.Options.Set(VapixRequestOptions.Timeout, TimeSpan.FromMilliseconds(300));
        var busy = await Assert.ThrowsAsync<SiteException>(() => client.SendAsync(report, CancellationToken.None));
        Assert.Equal(SiteProblem.Busy, busy.Problem);
        Assert.Equal("Site Building B is busy.", busy.Message);
    }

    [Fact]
    public async Task ATaskWaitsForTheSiteLinkAndSaysSo()
    {
        await using var device = LoopbackDevice.StartHttp();
        using var sites = new GatedSites(slots: 1);
        var site = sites.Fake.AddSite("Building B").Map("192.168.0.20", 80, device.EndPoint);
        var router = new DeviceRouter(sites, sites);
        using var client = VapixClient.Create(Options(site.Id), router);
        var held = await router.EnterLargeTransferAsync(DeviceRoute.ViaSite(site.Id), CancellationToken.None);
        var reports = new System.Collections.Concurrent.ConcurrentQueue<bool>();

        Task<HttpResponseMessage> sending;
        using (SiteLinkWait.Enter(reports.Enqueue))
        {
            var upload = Request("/axis-cgi/firmwaremanagement.cgi");
            upload.Options.Set(VapixRequestOptions.StreamResponse, true);
            upload.Options.Set(VapixRequestOptions.Timeout, Generous);
            sending = client.SendAsync(upload, CancellationToken.None);
        }

        await Eventually.TrueAsync(() => reports.Contains(true), "the task told it waits for the site link");
        Assert.False(sending.IsCompleted);
        held!.Dispose();
        using var response = await sending;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([true, false], reports);
        Assert.Equal(1, sites.Running); // the streamed answer keeps its slot until it is disposed
        response.Dispose();
        Assert.Equal(0, sites.Running);
    }

    [Fact]
    public async Task StatusReadsNeverWaitBehindLongRequestsOfTheDevice()
    {
        await using var device = LoopbackDevice.StartHttp();
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B").Map("192.168.0.20", 80, device.EndPoint);
        using var client = VapixClient.Create(Options(site.Id), new DeviceRouter(sites, sites));

        // Two answers that never end (streamed downloads of tasks) take both shared connections of the device.
        var holds = new List<HttpResponseMessage>();
        for (var i = 0; i < DeviceRouter.SiteConnectionsPerDevice; i++)
        {
            var hold = Request("/hold");
            hold.Options.Set(VapixRequestOptions.StreamResponse, true);
            holds.Add(await client.SendAsync(hold, CancellationToken.None));
        }

        // The poll goes on the device's status connections: it gets the device's answer (401 here), never a timeout.
        await Assert.ThrowsAsync<VapixAuthenticationException>(() => client.GetBasicDeviceInfoAsync(Generous, CancellationToken.None));
        await Assert.ThrowsAsync<VapixAuthenticationException>(() => client.GetBasicDeviceInfoAsync(CancellationToken.None));
        foreach (var hold in holds)
        {
            hold.Dispose();
        }
    }

    [Fact]
    public async Task WebViewAnswersNeverTakeTheConnectionsOfTasksAndChecks()
    {
        await using var device = LoopbackDevice.StartHttp();
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B").Map("192.168.0.20", 80, device.EndPoint);
        using var client = VapixClient.Create(Options(site.Id), new DeviceRouter(sites, sites));

        // More endless relayed answers (MJPEG streams, long polls of a page) than the shared pool has connections.
        var holds = new List<HttpResponseMessage>();
        for (var i = 0; i < DeviceRouter.SiteConnectionsPerDevice + 1; i++)
        {
            var hold = Request("/hold");
            hold.Options.Set(VapixRequestOptions.StreamResponse, true);
            hold.Options.Set(VapixClient.RelayedOption, true);
            holds.Add(await client.SendAsync(hold, CancellationToken.None));
        }

        // A task's request still gets a connection of its own: the device's answer, never a timeout.
        using (var request = Request("/axis-cgi/param.cgi"))
        {
            request.Options.Set(VapixRequestOptions.Timeout, Generous);
            using var response = await client.SendAsync(request, CancellationToken.None);
            Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        }

        foreach (var hold in holds)
        {
            hold.Dispose();
        }
    }

    [Fact]
    public void AStreamedAnswerThatIsDroppedFreesItsSlotWhenCollected()
    {
        var freed = new Freed();
        Lease(freed);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.True(freed.Count == 1, "the slot was freed by the finalizer");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Lease(Freed freed) => _ = new VapixClient.SlotLease(freed);

    [Fact]
    public void TheMostRecentSendersAreKeptAndTheOldestDropped()
    {
        var senders = new RecentSenders<string>(capacity: 3);
        var a = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 123);
        var b = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 123);
        var c = new IPEndPoint(IPAddress.Parse("10.0.0.3"), 123);
        var d = new IPEndPoint(IPAddress.Parse("10.0.0.4"), 123);

        senders.Set(a, "v4");
        senders.Set(b, "v4");
        senders.Set(c, "v4");
        senders.Set(a, "v6"); // a sent again: the most recent now
        senders.Set(d, "v4"); // full: b, the oldest, is dropped

        Assert.Equal(3, senders.Count);
        Assert.False(senders.TryGet(b, out _));
        Assert.True(senders.TryGet(a, out var answer));
        Assert.Equal("v6", answer);
        Assert.True(senders.TryGet(c, out _));
        Assert.True(senders.TryGet(d, out _));
    }

    [Fact]
    public void ACopyReadBeforeANewerChangeNeverOverwritesIt()
    {
        var feed = new DeviceChangeFeed();
        using var subscription = feed.Subscribe();
        var moved = new Device { Serial = "ACCC8E000001", Address = "10.0.0.20" };
        var other = new Device { Serial = "ACCC8E000002", Address = "192.168.0.21" };

        var readAt = feed.Sequence; // the publisher reads the site's devices now ...
        var stale = moved.Clone();
        stale.Address = "192.168.0.20";
        feed.Publish(new DeviceChange(DeviceChangeKind.Updated, moved.Id, moved)); // ... a poll publishes a newer copy meanwhile

        var published = feed.PublishUnlessNewer(
            [new DeviceChange(DeviceChangeKind.Updated, moved.Id, stale), new DeviceChange(DeviceChangeKind.Updated, other.Id, other)],
            readAt);

        Assert.Equal(1, published);
        var seen = new List<DeviceChange>();
        while (subscription.Reader.TryRead(out var change))
        {
            seen.Add(change);
        }

        Assert.Equal(["10.0.0.20", "192.168.0.21"], seen.Select(c => c.Device!.Address));
    }

    [Fact]
    public void RemovedDevicesKeepTheirStampOnlyForTheLastRemovalsAndNoStaleCopyBringsThemBack()
    {
        var feed = new DeviceChangeFeed { TombstoneLimit = 10 };
        var devices = Enumerable.Range(0, 100)
            .Select(i => new Device { Id = Guid.NewGuid(), Serial = "ACCC8E" + i.ToString("X6", CultureInfo.InvariantCulture), Address = "10.0.0.1" })
            .ToList();
        var readAt = feed.Sequence;
        foreach (var device in devices)
        {
            feed.Publish(new DeviceChange(DeviceChangeKind.Added, device.Id, device));
            feed.Publish(new DeviceChange(DeviceChangeKind.Removed, device.Id, null));
        }

        Assert.Equal(10, feed.StampCount); // not 100: the table does not grow with every device ever removed
        using var subscription = feed.Subscribe();
        var last = devices[^1];
        Assert.Equal(0, feed.PublishUnlessNewer([new DeviceChange(DeviceChangeKind.Updated, last.Id, last)], readAt));
        Assert.False(subscription.Reader.TryRead(out _));
    }

    private static VapixConnectionOptions Options(Guid siteId) => new()
    {
        Address = "192.168.0.20",
        Scheme = Uri.UriSchemeHttp,
        Timeout = Generous,
        Route = DeviceRoute.ViaSite(siteId),
    };

    private static HttpRequestMessage Request(string path) => new(HttpMethod.Get, new Uri(path, UriKind.Relative));

    private sealed class Freed : IDisposable
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Dispose() => Interlocked.Increment(ref _count);
    }

    /// <summary>The fake sites with large-transfer slots like the site manager's.</summary>
    private sealed class GatedSites(int slots) : ISiteTransport, ISiteStates, ISiteLargeTransfers, IDisposable
    {
        private readonly SemaphoreSlim _slots = new(slots, slots);
        private int _running;

        public FakeSiteTransport Fake { get; } = new();

        public int Running => Volatile.Read(ref _running);

        public void Dispose() => _slots.Dispose();

        public event EventHandler<SiteStatesChangedEventArgs>? Changed
        {
            add => Fake.Changed += value;
            remove => Fake.Changed -= value;
        }

        public ValueTask<Stream> OpenStreamAsync(Guid siteId, string host, int port, SiteDialKind kind, TimeSpan timeout, CancellationToken ct) =>
            Fake.OpenStreamAsync(siteId, host, port, kind, timeout, ct);

        public string? NameOf(Guid siteId) => Fake.NameOf(siteId);

        public bool IsOnline(Guid siteId) => Fake.IsOnline(siteId);

        public TimeSpan RoundTrip(Guid siteId) => Fake.RoundTrip(siteId);

        public async ValueTask<IDisposable> EnterLargeTransferAsync(Guid siteId, CancellationToken ct)
        {
            await _slots.WaitAsync(ct);
            Interlocked.Increment(ref _running);
            return new Slot(this);
        }

        private sealed class Slot(GatedSites owner) : IDisposable
        {
            private int _done;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0)
                {
                    Interlocked.Decrement(ref owner._running);
                    owner._slots.Release();
                }
            }
        }
    }
}
