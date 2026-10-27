using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.LoadTest.Checking;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LoadTest.Tests.Server;

/// <summary>
/// The plugin on a <see cref="FakeCoreContext"/> with a <see cref="FakeTimeProvider"/>, fake stream probes on that clock,
/// a unique data folder and short phases. <see cref="AdvanceAsync"/> steps the check one second at a time, each only once
/// the run waits for it and every open that is not deliberately slow has completed (never a race with a phase change).
/// </summary>
internal sealed class Rig : IAsyncDisposable
{
    public const string Exposure = "50";

    private int _serial;

    public Rig(int phaseSeconds = 3, int maxRowsPerEvent = 200, string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "oadm-ic-tests", Guid.NewGuid().ToString("N"));
        Ctx = new FakeCoreContext { FakeStreamProbes = new FakeStreamProbes(Time), DataDirectory = DataDirectory, Firewall = Firewall };
        Options = new LoadTestOptions { Time = Time, PhaseSeconds = phaseSeconds, MaxRowsPerEvent = maxRowsPerEvent };
        Plugin = new LoadTestPlugin(Options);
    }

    public Oadm.Tests.Shared.RecordingFirewall Firewall { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));

    public string DataDirectory { get; }

    public FakeCoreContext Ctx { get; }

    public FakeStreamProbes Probes => Ctx.FakeStreamProbes;

    public LoadTestOptions Options { get; }

    public LoadTestPlugin Plugin { get; }

    public CheckService Service => Plugin.Service ?? throw new InvalidOperationException("Not started.");

    public FakeCallContext Admin => field ??= Ctx.CallAsAllDevices("admin");

    public async Task<Rig> StartAsync()
    {
        await Plugin.StartAsync(Ctx, CancellationToken.None);
        return this;
    }

    /// <summary>A camera with <paramref name="sources"/> (default one 1080p source) and exposure 50 on every source.</summary>
    public FakeDevice Camera(string address, params StreamProbeSource[] sources)
    {
        var device = Ctx.Add(new FakeDevice { Address = address, Serial = "B8A44F" + (++_serial).ToString("X6", System.Globalization.CultureInfo.InvariantCulture) });
        Probes.Add(device.Id, sources);
        foreach (var source in Probes.Sources[device.Id])
        {
            Ctx.Cameras[device.Id].Parameters[ExposureKeeper.ParameterOf(source.Camera)] = Exposure;
        }

        return device;
    }

    public List<FakeDevice> Cameras(int count) => [.. Enumerable.Range(1, count).Select(i => Camera($"10.0.{i / 250}.{(i % 250) + 1}"))];

    public static string Json<T>(T value) => LoadTestJson.Serialize(value);

    public async Task<T> CallAsync<T>(ICallContext call, string method, object? payload = null) =>
        LoadTestJson.Deserialize<T>(await Ctx.InvokeAsync(Plugin, call, method, payload is null ? null : LoadTestJson.Serialize(payload)));

    public Task<RunSummary> StartCheckAsync(IEnumerable<IDeviceInfo> devices, int batchSize = 50, ICallContext? call = null) =>
        CallAsync<RunSummary>(call ?? Admin, LoadTestMethods.Start, new StartRequest([.. devices.Select(d => d.Id)], batchSize, 1));

    public CheckRunner Runner => Service.Runner ?? throw new InvalidOperationException("No check runs.");

    /// <summary>Steps the clock <paramref name="seconds"/> seconds, one at a time; stops early when the run ended.</summary>
    public async Task AdvanceAsync(int seconds)
    {
        var runner = Service.Runner;
        if (runner is null)
        {
            return;
        }

        for (var i = 0; i < seconds; i++)
        {
            var done = runner.Ticks;
            if (!await SettledAsync(runner, done))
            {
                return;
            }

            // Two halves: a probe whose start delay ends between two seconds starts before the next sample.
            Time.Advance(TimeSpan.FromMilliseconds(500));
            await Eventually.TrueAsync(() => Ended(runner) || runner.PendingOpens == Probes.OpensWaiting, "the opens due at the half second");
            Time.Advance(TimeSpan.FromMilliseconds(500));
            await Eventually.TrueAsync(() => Ended(runner) || runner.Ticks > done, "the next second sampled");
        }
    }

    /// <summary>Waits until the run waits for its next second (the next phase has begun), or ended.</summary>
    public async Task SettleAsync()
    {
        if (Service.Runner is { } runner)
        {
            await SettledAsync(runner, runner.Ticks);
        }
    }

    /// <summary>The run as the page opens it.</summary>
    public Task<RunDetail> DetailAsync(Guid runId, ICallContext? call = null) =>
        CallAsync<RunDetail>(call ?? Admin, LoadTestMethods.GetRun, new RunRequest(runId));

    /// <summary>Runs the check to its end and waits until it is kept.</summary>
    public async Task RunToEndAsync()
    {
        var runner = Service.Runner;
        if (runner is not null)
        {
            await AdvanceAsync(runner.Run.TotalSeconds - runner.Ticks);
        }

        await Service.Current;
    }

    /// <summary>Waits until the run waits for second <paramref name="done"/> + 1 with no open pending; false when it ended.</summary>
    private async Task<bool> SettledAsync(CheckRunner runner, int done)
    {
        await Eventually.TrueAsync(() => Ended(runner) || (runner.WaitingAfter == done && runner.PendingOpens == Probes.OpensWaiting), "the run waiting for the next second");
        return !Ended(runner);
    }

    private bool Ended(CheckRunner runner) => runner.Completion.IsCompleted || !ReferenceEquals(Service.Runner, runner);

    /// <summary>The live events published so far, as the watcher sees them.</summary>
    public List<LiveEvent> LiveEvents(ICallContext? watcher = null) =>
        [.. Ctx.EventsSeenBy(watcher ?? Admin).Where(e => e.Topic == LoadTestMethods.LiveTopic).Select(e => LoadTestJson.Deserialize<LiveEvent>(e.PayloadJson))];

    public async ValueTask DisposeAsync()
    {
        await Plugin.DisposeAsync();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

/// <summary>Scripts of the fake probes.</summary>
internal static class Scripts
{
    /// <summary>Every second loses <paramref name="percent"/> % of 300 packets.</summary>
    public static Action<FakeStreamProbe> PacketLoss(double percent) => p =>
    {
        var lost = (long)Math.Round(300 * percent / 100);
        p.Script = _ => FakeStreamSecond.Healthy() with { Packets = 300 - lost, LostPackets = lost };
    };

    /// <summary>Loses packets only while at least <paramref name="streams"/> probes are open (the network's limit).</summary>
    public static Action<FakeStreamProbe> LossFrom(FakeStreamProbes probes, int streams) => p =>
        p.Script = _ => probes.Open.Count >= streams ? FakeStreamSecond.Healthy() with { Packets = 280, LostPackets = 20 } : FakeStreamSecond.Healthy();
}
