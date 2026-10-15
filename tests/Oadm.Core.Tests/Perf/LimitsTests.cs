using System.Diagnostics;
using System.Net;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Tests.Hardware;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Tests.Vapix;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>
/// Where OADM stops scaling. Opt-in: set <c>OADM_LIMITS_DEVICES</c> to the device counts to try,
/// e.g. "5000,25000,50000". Nothing is asserted on time: the tests write what they measured.
/// </summary>
public static class Limits
{
    public const string EnvironmentVariable = "OADM_LIMITS_DEVICES";

    /// <summary>The device counts to try; empty when the variable is not set.</summary>
    public static IReadOnlyList<int> Sizes =>
        (Environment.GetEnvironmentVariable(EnvironmentVariable) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToList();

    /// <summary>Managed heap after a full collection, in MB.</summary>
    public static double HeapMb()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true) / 1024.0 / 1024.0;
    }

    public static async Task<TimeSpan> TimeAsync(ITestOutputHelper output, string what, Func<Task> action)
    {
        var watch = Stopwatch.StartNew();
        await action();
        watch.Stop();
        output.WriteLine($"  {what}: {watch.Elapsed.TotalMilliseconds:N0} ms");
        return watch.Elapsed;
    }
}

/// <summary>Polling throughput of OADM itself (instant fake devices), the database size and the memory of the clients.</summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
public sealed class PollingLimitsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PollRoundsDatabaseAndClientCacheAtEverySize()
    {
        foreach (var size in Limits.Sizes)
        {
            output.WriteLine($"== {size:N0} devices");
            await using var database = await TestDatabase.CreateAsync();
            var info = Fixtures.Read("basicdeviceinfo-getAllProperties.json");
            using var handler = new FakeHttpMessageHandler((_, _) => Fixtures.Json(info));
            var apis = Enumerable.Range(0, 100).Select(i => new Oadm.Sdk.Vapix.DeviceApi($"api-{i:D3}", "1.0", $"Some API number {i}", "official")).ToList();

            await Limits.TimeAsync(output, "Seed (one transaction, 100 APIs each)", async () =>
            {
                await using var db = await database.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
                for (var i = 0; i < size; i++)
                {
                    db.Devices.Add(new Device
                    {
                        Id = Guid.NewGuid(),
                        Serial = $"ACCC8E{i:X6}",
                        Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                        Scheme = DeviceScheme.Http,
                        Status = DeviceStatus.Ok,
                        Model = "P3265-V",
                        Apis = apis,
                    });
                }

                await db.SaveChangesAsync();
            });

            var devices = database.Get<DeviceRepository>();
            var credentials = database.Get<CredentialStore>();
            await Limits.TimeAsync(output, "Store a credential for every device (one transaction)", async () =>
                await credentials.SetManyAsync([.. await devices.ListDeviceIdsAsync(CancellationToken.None)], "root", "pass", CancellationToken.None));
            await Limits.TimeAsync(output, "ListDevicesAsync (every row, as a Watch snapshot reads them)", async () =>
                await devices.ListDevicesAsync(CancellationToken.None));

            var heapBefore = Limits.HeapMb();
            using var factory = new VapixClientFactory(devices, credentials, new Connector(handler));
            using var polling = new DevicePollingService(devices, factory, database.Get<ServerSettingsStore>()) { MaxParallelism = 16 };
            await Limits.TimeAsync(output, "Poll round 1 (16 at once, fake devices answer at once)", () => polling.PollAllAsync(CancellationToken.None));
            var round = await Limits.TimeAsync(output, "Poll round 2 (LastSeen only)", () => polling.PollAllAsync(CancellationToken.None));
            output.WriteLine($"  OADM's own poll ceiling: {size / round.TotalSeconds:N0} polls per second (60 s interval needs {size / 60.0:N0})");
            output.WriteLine($"  Cached VAPIX clients: {factory.CachedCount:N0}, heap growth {Limits.HeapMb() - heapBefore:N1} MB (without open connections)");

            SqliteFlush(database);
            output.WriteLine($"  Database file: {new FileInfo(database.Paths.DatabasePath).Length / 1024.0 / 1024.0:N1} MB");
        }
    }

    private static void SqliteFlush(TestDatabase database)
    {
        using var db = database.Get<IDbContextFactory<OadmDbContext>>().CreateDbContext();
        db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    private sealed class Connector(FakeHttpMessageHandler handler) : IVapixConnector
    {
        public VapixClient Connect(VapixConnectionOptions options) =>
            new(VapixClient.BuildBaseAddress(options.Scheme, options.Address), handler, new CertificatePinning(options.PinnedCertificateFingerprint), disposeHandler: false);
    }
}

/// <summary>
/// How long the real dev cameras take for a status poll and a full refresh (read-only). Opt-in like the other limits
/// tests: the polling capacity of OADM is about 16 / (time of one poll) devices per second.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class PollLatencyHardwareTests(ITestOutputHelper output)
{
    [HardwareFact]
    public async Task StatusPollAndFullRefreshTimesOfTheDevCameras()
    {
        if (Limits.Sizes.Count == 0)
        {
            return;
        }

        foreach (var camera in DevCameras.All)
        {
            output.WriteLine($"== {camera}");
            var options = new VapixConnectionOptions
            {
                Address = camera.Address,
                Scheme = camera.EffectiveScheme,
                Credentials = new NetworkCredential(camera.User, camera.Password),
                Timeout = TimeSpan.FromSeconds(15),
            };

            // A new client per poll: new TCP and TLS connection and a new login, as after an idle pool timeout.
            var cold = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                using var client = VapixClient.Create(options);
                var watch = Stopwatch.StartNew();
                await client.GetBasicDeviceInfoAsync(CancellationToken.None);
                cold.Add(watch.Elapsed.TotalMilliseconds);
            }

            // One client: the kept connection, as OADM's cached client inside the pool timeout.
            var warm = new List<double>();
            using (var client = VapixClient.Create(options))
            {
                await client.GetBasicDeviceInfoAsync(CancellationToken.None);
                for (var i = 0; i < 20; i++)
                {
                    var watch = Stopwatch.StartNew();
                    await client.GetBasicDeviceInfoAsync(CancellationToken.None);
                    warm.Add(watch.Elapsed.TotalMilliseconds);
                }
            }

            output.WriteLine($"  Status poll, new connection: median {Median(cold):N0} ms ({string.Join(", ", cold.Select(c => c.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)))})");
            output.WriteLine($"  Status poll, kept connection: median {Median(warm):N0} ms, max {warm.Max():N0} ms");

            // A full refresh as the poller does it, through a real database and the factory.
            await using var database = await TestDatabase.CreateAsync();
            var devices = database.Get<DeviceRepository>();
            var device = await devices.AddAsync(new Device
            {
                Id = Guid.NewGuid(),
                Serial = "ACCC8E000001",
                Address = camera.Address,
                Scheme = camera.EffectiveScheme == Uri.UriSchemeHttp ? DeviceScheme.Http : DeviceScheme.Https,
                Status = DeviceStatus.Unknown,
            }, CancellationToken.None);
            await database.Get<CredentialStore>().SetAsync(device.Id, camera.User, camera.Password, CancellationToken.None);
            using var factory = new VapixClientFactory(devices, database.Get<CredentialStore>(), new RealConnector());
            using var polling = new DevicePollingService(devices, factory, database.Get<ServerSettingsStore>());
            var full = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew();
                await polling.RefreshAsync(device.Id, CancellationToken.None);
                full.Add(watch.Elapsed.TotalMilliseconds);
            }

            output.WriteLine($"  Full refresh: {string.Join(", ", full.Select(c => c.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)))} ms");
        }
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private sealed class RealConnector : IVapixConnector
    {
        public VapixClient Connect(VapixConnectionOptions options) => VapixClient.Create(options);
    }
}

/// <summary>
/// The poll ladder: one round of 25,000 devices that answer after a camera's typical time, 1 % that never answer, for
/// several numbers of polls at once and request timeouts. Shows where OADM itself (the database) stops scaling.
/// </summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
public sealed class PollLadderLimitsTests(ITestOutputHelper output)
{
    private static readonly string Info = Fixtures.Read("basicdeviceinfo-getAllProperties.json");

    [Fact]
    public async Task PollRoundOf25000DevicesForEachParallelismAndTimeout()
    {
        if (Limits.Sizes.Count == 0)
        {
            return;
        }

        const int size = 25_000;
        var latency = TimeSpan.FromMilliseconds(70);
        await using var database = await TestDatabase.CreateAsync();
        var apis = Enumerable.Range(0, 100).Select(i => new Oadm.Sdk.Vapix.DeviceApi($"api-{i:D3}", "1.0", $"Some API number {i}", "official")).ToList();
        await using (var db = await database.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            for (var i = 0; i < size; i++)
            {
                db.Devices.Add(new Device
                {
                    Id = Guid.NewGuid(),
                    Serial = $"ACCC8E{i:X6}",
                    Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                    Scheme = DeviceScheme.Http,
                    Status = i % 100 == 0 ? DeviceStatus.Unreachable : DeviceStatus.Ok,
                    Model = "P3265-V",
                    Apis = apis,
                });
            }

            await db.SaveChangesAsync();
        }

        var devices = database.Get<DeviceRepository>();
        await database.Get<CredentialStore>().SetManyAsync([.. await devices.ListDeviceIdsAsync(CancellationToken.None)], "root", "pass", CancellationToken.None);

        // Address 10.x.y.z with z % 100 == 0 ... the unreachable ones are every 100th device: the host part tells.
        var dead = (await devices.ListDevicesAsync(CancellationToken.None)).Where(d => d.Status == DeviceStatus.Unreachable).Select(d => d.Address).ToHashSet();
        using var answering = new DelayHandler(latency, dead);
        using var instant = new DelayHandler(TimeSpan.Zero, new HashSet<string>());

        output.WriteLine($"25,000 devices, {dead.Count} never answer, the others after {latency.TotalMilliseconds:N0} ms");
        foreach (var (handler, parallel, timeout) in new[]
        {
            (instant, 16, 3), (instant, 64, 3), (instant, 200, 3),
            (answering, 16, 3), (answering, 32, 3), (answering, 64, 3), (answering, 128, 3), (answering, 200, 3), (answering, 256, 3),
            (answering, 64, 15), (answering, 200, 15),
        })
        {
            using var factory = new VapixClientFactory(devices, database.Get<CredentialStore>(), new Connector(handler)) { RequestTimeout = TimeSpan.FromSeconds(timeout) };
            using var polling = new DevicePollingService(devices, factory, database.Get<ServerSettingsStore>()) { MaxParallelism = parallel };
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            await polling.PollAllAsync(CancellationToken.None);
            watch.Stop();
            var used = Process.GetCurrentProcess().TotalProcessorTime - cpu;
            var kind = handler == instant ? "instant" : $"{latency.TotalMilliseconds:N0} ms";
            output.WriteLine($"  {kind,-8} {parallel,3} at once, timeout {timeout,2} s: round {watch.Elapsed.TotalSeconds,6:N1} s, {size / watch.Elapsed.TotalSeconds,6:N0} polls/s, CPU {used.TotalSeconds / watch.Elapsed.TotalSeconds * 100 / Environment.ProcessorCount,3:N0} % of {Environment.ProcessorCount} cores");
        }
    }

    private sealed class DelayHandler(TimeSpan latency, IReadOnlySet<string> dead) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (dead.Contains(request.RequestUri!.Host))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (latency > TimeSpan.Zero)
            {
                await Task.Delay(latency, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(Info, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class Connector(HttpMessageHandler handler) : IVapixConnector
    {
        public VapixClient Connect(VapixConnectionOptions options) =>
            new(VapixClient.BuildBaseAddress(options.Scheme, options.Address), handler, new CertificatePinning(options.PinnedCertificateFingerprint), options.Timeout, disposeHandler: false);
    }
}

/// <summary>
/// How long the dev cameras keep an idle HTTPS connection open (read-only, anonymous getAllUnrestrictedProperties). A poll
/// after the camera closed it needs a new connection and TLS handshake.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class KeepAliveHardwareTests(ITestOutputHelper output)
{
    [HardwareFact]
    public async Task IdleConnectionLifetimeOfTheDevCameras()
    {
        if (Limits.Sizes.Count == 0)
        {
            return;
        }

        var results = await Task.WhenAll(DevCameras.All.Select(MeasureAsync));
        foreach (var line in results.SelectMany(r => r))
        {
            output.WriteLine(line);
        }

        output.WriteLine($".NET default idle pool timeout: {new SocketsHttpHandler().PooledConnectionIdleTimeout.TotalSeconds:N0} s");
    }

    private static async Task<List<string>> MeasureAsync(DevCamera camera)
    {
        var lines = new List<string> { $"== {camera}" };
        var connects = 0;
        using var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
#pragma warning disable CA5359 // Anonymous read-only request to a dev camera with a self-signed certificate.
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
#pragma warning restore CA5359
            ConnectCallback = async (context, ct) =>
            {
                Interlocked.Increment(ref connects);
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(context.DnsEndPoint, ct);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var uri = new Uri($"https://{camera.Address}/axis-cgi/basicdeviceinfo.cgi");
        async Task<double> AskAsync()
        {
            var watch = Stopwatch.StartNew();
            using var content = new StringContent("""{"apiVersion":"1.0","method":"getAllUnrestrictedProperties"}""", System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(uri, content);
            await response.Content.ReadAsByteArrayAsync();
            return watch.Elapsed.TotalMilliseconds;
        }

        await AskAsync();
        foreach (var idle in new[] { 2, 5, 10, 20, 40, 65 })
        {
            var before = connects;
            await Task.Delay(TimeSpan.FromSeconds(idle));
            var ms = await AskAsync();
            lines.Add($"  after {idle,2} s idle: {(connects > before ? "new connection" : "same connection")}, {ms:N0} ms");
        }

        return lines;
    }
}

/// <summary>
/// The task engine alone (database store, devices in memory, no polling): is the overhead per task constant when the
/// run grows? Compare with the full server (ServerLimitsTests), where every finished task also queues a full refresh.
/// </summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
public sealed class TaskEngineLimitsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OverheadPerTaskAtEverySize()
    {
        foreach (var size in Limits.Sizes)
        {
            await using var database = await TestDatabase.CreateAsync();
            var devices = new Oadm.Core.Tests.Tasks.FakeDeviceRepository();
            var registry = new Oadm.Core.Plugins.PluginRegistry();
            registry.RegisterTaskPlugin(new Oadm.Core.Tests.Tasks.DelegateTaskPlugin("perf.ok", (ctx, _, _) =>
            {
                ctx.ReportProgress(50, "half");
                return Task.CompletedTask;
            }), new Oadm.Core.Plugins.PluginOrigin("test", "1.0.0", null));
            var ids = devices.AddMany(size);
            await using var engine = new Oadm.Core.Tasks.TaskEngine(database.Get<Oadm.Core.Persistence.EfTaskStore>(), registry, devices, new Oadm.Core.Tests.Tasks.FakeVapixClientFactory());
            IReadOnlyList<Guid> taskIds = [];
            var submit = await Limits.TimeAsync(output, $"{size:N0} devices: Run (submission)", async () =>
                taskIds = await engine.RunAsync("perf.ok", ids, null, "tester", CancellationToken.None));
            var run = await Limits.TimeAsync(output, $"{size:N0} devices: every task finished", async () =>
            {
                foreach (var id in taskIds)
                {
                    await engine.WaitForCompletionAsync(id, CancellationToken.None);
                }
            });
            output.WriteLine($"  {run.TotalMilliseconds / size:N2} ms per task");
        }
    }
}

/// <summary>Polling and full refresh at the proposed settings for 30,000 devices: status poll 200 at once, 3 s timeout; full refresh 64 at once.</summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
public sealed class ProposedPollingLimitsTests(ITestOutputHelper output)
{
    private static readonly string Info = Fixtures.Read("basicdeviceinfo-getAllProperties.json");

    [Fact]
    public async Task ThirtyThousandDevicesWithTheProposedSettings()
    {
        if (Limits.Sizes.Count == 0)
        {
            return;
        }

        const int size = 30_000;
        await using var database = await TestDatabase.CreateAsync();
        var apis = Enumerable.Range(0, 100).Select(i => new Oadm.Sdk.Vapix.DeviceApi($"api-{i:D3}", "1.0", $"Some API number {i}", "official")).ToList();
        await using (var db = await database.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            for (var i = 0; i < size; i++)
            {
                db.Devices.Add(new Device
                {
                    Id = Guid.NewGuid(),
                    Serial = $"ACCC8E{i:X6}",
                    Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                    Scheme = DeviceScheme.Http,
                    Status = i % 100 == 0 ? DeviceStatus.Unreachable : DeviceStatus.Ok,
                    Model = "P3265-V",
                    Apis = apis,
                });
            }

            await db.SaveChangesAsync();
        }

        var devices = database.Get<DeviceRepository>();
        await database.Get<CredentialStore>().SetManyAsync([.. await devices.ListDeviceIdsAsync(CancellationToken.None)], "root", "pass", CancellationToken.None);
        var all = await devices.ListDevicesAsync(CancellationToken.None);
        var dead = all.Where(d => d.Status == DeviceStatus.Unreachable).Select(d => d.Address).ToHashSet();

        using (var poll = new LatencyHandler(TimeSpan.FromMilliseconds(70), dead))
        using (var factory = new VapixClientFactory(devices, database.Get<CredentialStore>(), new Connector(poll)) { RequestTimeout = TimeSpan.FromSeconds(3) })
        using (var polling = new DevicePollingService(devices, factory, database.Get<ServerSettingsStore>()) { MaxParallelism = 200 })
        {
            var round = await Limits.TimeAsync(output, "Status poll round, 30,000 devices, 70 ms, 1 % dead, 200 at once, 3 s", () => polling.PollAllAsync(CancellationToken.None));
            output.WriteLine($"  {size / round.TotalSeconds:N0} polls/s (60 s interval needs {size / 54.0:N0})");
        }

        // The full refresh of the OK devices only: about 4 requests of 70 ms each, 64 at once.
        var ok = all.Where(d => d.Status == DeviceStatus.Ok).Select(d => d.Id).ToList();
        using (var refresh = new LatencyHandler(TimeSpan.FromMilliseconds(70), dead))
        using (var factory = new VapixClientFactory(devices, database.Get<CredentialStore>(), new Connector(refresh)) { RequestTimeout = TimeSpan.FromSeconds(3) })
        using (var polling = new DevicePollingService(devices, factory, database.Get<ServerSettingsStore>()))
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = 64 };
            var watch = Stopwatch.StartNew();
            await Parallel.ForEachAsync(ok, options, async (id, ct) => await polling.RefreshAsync(id, ct));
            watch.Stop();
            output.WriteLine($"  Full refresh of {ok.Count:N0} OK devices, 64 at once: {watch.Elapsed.TotalSeconds:N1} s, {ok.Count / watch.Elapsed.TotalSeconds:N0}/s, {refresh.Requests / (double)ok.Count:N1} requests per device");
            output.WriteLine($"  Needed: {size / 600.0:N0}/s at 10 min, {size / 1800.0:N0}/s at 30 min");
        }
    }

    [Fact]
    public async Task WorstCaseEveryDeviceDeadThenBack()
    {
        if (Limits.Sizes.Count == 0)
        {
            return;
        }

        var size = Limits.Sizes.Max();
        await using var database = await TestDatabase.CreateAsync();
        await using (var db = await database.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            for (var i = 0; i < size; i++)
            {
                db.Devices.Add(new Device
                {
                    Id = Guid.NewGuid(),
                    Serial = $"ACCC8E{i:X6}",
                    Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                    Scheme = DeviceScheme.Http,
                    Status = DeviceStatus.Ok,
                    Model = "P3265-V",
                });
            }

            await db.SaveChangesAsync();
        }

        var devices = database.Get<DeviceRepository>();
        var ids = await devices.ListDeviceIdsAsync(CancellationToken.None);
        await database.Get<CredentialStore>().SetManyAsync([.. ids], "root", "pass", CancellationToken.None);
        var everyone = (await devices.ListDevicesAsync(CancellationToken.None)).Select(d => d.Address).ToHashSet();
        using var feed = database.Get<IDeviceChangeFeed>().Subscribe();

        output.WriteLine($"== {size:N0} devices, all of them stop answering (3 s timeout, 200 at once)");
        using (var dead = new LatencyHandler(TimeSpan.FromMilliseconds(70), everyone))
        using (var factory = new VapixClientFactory(devices, database.Get<CredentialStore>(), new Connector(dead)) { RequestTimeout = TimeSpan.FromSeconds(3) })
        using (var polling = new DevicePollingService(devices, factory, database.Get<ServerSettingsStore>()) { MaxParallelism = 200 })
        {
            var heap = Limits.HeapMb();
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var round = await Limits.TimeAsync(output, "Poll round, every device times out", () => polling.PollAllAsync(CancellationToken.None));
            process.Refresh();
            output.WriteLine($"  CPU {(process.TotalProcessorTime - cpu).TotalSeconds / round.TotalSeconds:N2} cores, heap +{Limits.HeapMb() - heap:N0} MB, {Drain(feed):N0} devices changed to Unreachable");
            output.WriteLine($"  With a second try for every failed device the round would take {round.TotalSeconds * 2:N0} s");
        }

        output.WriteLine("== the network is back: every device answers again (70 ms)");
        using (var back = new LatencyHandler(TimeSpan.FromMilliseconds(70), new HashSet<string>()))
        using (var factory = new VapixClientFactory(devices, database.Get<CredentialStore>(), new Connector(back)) { RequestTimeout = TimeSpan.FromSeconds(3) })
        using (var polling = new DevicePollingService(devices, factory, database.Get<ServerSettingsStore>()) { MaxParallelism = 200 })
        {
            await Limits.TimeAsync(output, "Poll round, every device back to Ok", () => polling.PollAllAsync(CancellationToken.None));
            output.WriteLine($"  {Drain(feed):N0} devices changed to Ok; each queues a full refresh (64 at once: about {size * 5 * 0.07 / 64 / 60:N1} min at 70 ms per request, measured above)");
        }
    }

    private static int Drain(DeviceChangeSubscription subscription)
    {
        var count = 0;
        while (subscription.Reader.TryRead(out _))
        {
            count++;
        }

        return count;
    }

    private sealed class LatencyHandler(TimeSpan latency, IReadOnlySet<string> dead) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (dead.Contains(request.RequestUri!.Host))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            await Task.Delay(latency, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(Info, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class Connector(HttpMessageHandler handler) : IVapixConnector
    {
        public VapixClient Connect(VapixConnectionOptions options) =>
            new(VapixClient.BuildBaseAddress(options.Scheme, options.Address), handler, new CertificatePinning(options.PinnedCertificateFingerprint), options.Timeout, disposeHandler: false);
    }
}
