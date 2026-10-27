using System.Globalization;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.LoadTest.Checking;

/// <summary>
/// The state of one check while it runs and after: the streams with their phase results, the graphs (one point per second
/// over the whole run) and the steps. Not thread-safe on its own: <see cref="CheckRunner"/> changes it and every reader
/// takes <see cref="Gate"/>.
/// </summary>
internal sealed class CheckRun
{
    public static readonly CheckPhase[] AllPhases = Enum.GetValues<CheckPhase>();

    private readonly List<CheckPhase> _phases = [];
    private readonly List<double> _totalMbps = [];
    private readonly List<double> _averageLoss = [];
    private readonly List<double> _worstLoss = [];
    private readonly List<double> _averageJitter = [];
    private readonly List<StepResult> _steps = [];
    private readonly HashSet<int> _changed = [];
    private readonly List<string> _restoreWarnings = [];
    private int _publishedPoints;

    public CheckRun(Guid id, string startedBy, string owner, DateTimeOffset startedUtc, PlannedCheck plan, int phaseSeconds)
    {
        Id = id;
        StartedBy = startedBy;
        Owner = owner;
        StartedUtc = startedUtc;
        Plan = plan;
        PhaseSeconds = phaseSeconds;
        Streams = [.. plan.Streams.Select((s, i) => new StreamState(s, i))];
        ById = Streams.ToDictionary(s => s.Planned.StreamId, StringComparer.Ordinal);
        TotalSeconds = plan.BatchList.Count * AllPhases.Length * phaseSeconds;
    }

    public Lock Gate { get; } = new();

    public Guid Id { get; }

    public string StartedBy { get; }

    /// <summary>The device scope that ran the check (its owner).</summary>
    public string Owner { get; }

    public DateTimeOffset StartedUtc { get; }

    public DateTimeOffset? EndedUtc { get; set; }

    public PlannedCheck Plan { get; }

    public int PhaseSeconds { get; }

    public int TotalSeconds { get; }

    public IReadOnlyList<StreamState> Streams { get; }

    public IReadOnlyDictionary<string, StreamState> ById { get; }

    public RunState State { get; set; } = RunState.Running;

    /// <summary>The verdict of a run that broke off (Failed); null = computed.</summary>
    public string? FailedVerdict { get; set; }

    /// <summary>The batch running now, 1-based.</summary>
    public int Batch { get; set; } = 1;

    public CheckPhase Phase { get; set; } = CheckPhase.Ramp25;

    /// <summary>Seconds sampled so far over the whole run.</summary>
    public int Seconds => _phases.Count;

    /// <summary>The phases of the current batch's seconds (its per-stream series).</summary>
    public List<CheckPhase> BatchPhases { get; } = [];

    public void AddRestoreWarnings(IEnumerable<string> warnings) => _restoreWarnings.AddRange(warnings);

    public void MarkChanged(StreamState stream) => _changed.Add(stream.Index);

    /// <summary>Appends one second of the graphs.</summary>
    public void AddPoint(CheckPhase phase, double totalMbps, double averageLoss, double worstLoss, double averageJitter)
    {
        _phases.Add(phase);
        _totalMbps.Add(Math.Round(totalMbps, 2));
        _averageLoss.Add(Math.Round(averageLoss, 3));
        _worstLoss.Add(Math.Round(worstLoss, 3));
        _averageJitter.Add(Math.Round(averageJitter, 2));
        BatchPhases.Add(phase);
    }

    public void AddStep(StepResult step) => _steps.Add(step);

    /// <summary>The graphs of the whole run.</summary>
    public RunGraphs Graphs() => new([.. _phases], [.. _totalMbps], [.. _averageLoss], [.. _worstLoss], [.. _averageJitter], [.. _steps]);

    /// <summary>The live event: the seconds since the last one, every step so far and the changed rows (marks them published).</summary>
    public (int FromSecond, RunGraphs Points, IReadOnlyList<StreamRow> Rows) TakeNew()
    {
        var from = _publishedPoints;
        var count = _phases.Count - from;
        var points = new RunGraphs(
            _phases.GetRange(from, count),
            _totalMbps.GetRange(from, count),
            _averageLoss.GetRange(from, count),
            _worstLoss.GetRange(from, count),
            _averageJitter.GetRange(from, count),
            [.. _steps]); // the page replaces its bars with the full list
        _publishedPoints = _phases.Count;
        var rows = _changed.Order().Select(i => Streams[i].Row()).ToList();
        _changed.Clear();
        return (from, points, rows);
    }

    /// <summary>The summary as the run list shows it.</summary>
    public RunSummary Summary()
    {
        var failed = Streams.Count(s => s.HasFailure);
        var worst = 0.0;
        foreach (var stream in Streams)
        {
            worst = Math.Max(worst, stream.WorstLoss);
        }

        var running = State == RunState.Running;
        var remaining = Math.Max(0, TotalSeconds - Seconds);
        return new RunSummary(
            Id,
            StartedUtc,
            EndedUtc,
            StartedBy,
            State,
            Streams.Count,
            Plan.BatchList.Count,
            Plan.BatchSize,
            Plan.PhaseMinutes,
            Verdict(failed),
            !running && failed == 0 && _steps.Count > 0,
            failed,
            _totalMbps.Count == 0 ? 0 : Math.Round(_totalMbps.Max(), 1),
            Math.Round(worst, 2),
            running ? null : Verdicts.Headroom(_steps),
            TotalSeconds == 0 ? 1 : Math.Clamp((double)Seconds / TotalSeconds, 0, 1),
            Batch,
            running && Seconds == 0 ? null : Phase, // null while the first streams connect
            running ? TimeSpan.FromSeconds(remaining) : null,
            [.. _restoreWarnings]);
    }

    private string Verdict(int failed)
    {
        if (State == RunState.Failed && FailedVerdict is not null)
        {
            return FailedVerdict;
        }

        if (Verdicts.Failure(_steps, Streams) is { } failure)
        {
            return failure;
        }

        return State switch
        {
            RunState.Running => string.Create(CultureInfo.InvariantCulture, $"Checking batch {Batch} of {Plan.BatchList.Count}"),
            RunState.Cancelled => Verdicts.Cancelled,
            _ => failed == 0 ? Verdicts.Carries : Verdicts.Cancelled,
        };
    }
}

/// <summary>The verdict and headroom texts (docs/spec/load-test.md, The page).</summary>
internal static class Verdicts
{
    public const string Carries = "The network and cameras carry this load";

    public const string Cancelled = "The check was cancelled";

    /// <summary>A run that was running when the server stopped (found at the next start).</summary>
    public const string ServerStopped = "The server stopped during the check";

    /// <summary>A run stopped because the plugin stopped (turned off, or the server stops now).</summary>
    public const string PluginStopped = "The check stopped when the plugin stopped";

    /// <summary>
    /// "Loss starts at 150 cameras" (the first ramp step or Normal with a failed camera, over all batches: the fewest
    /// streams at once of that phase's failing steps), else "3 cameras fail in Low light", else null.
    /// </summary>
    public static string? Failure(IReadOnlyList<StepResult> steps, IReadOnlyList<StreamState> streams)
    {
        foreach (var phase in CheckRun.AllPhases.Where(p => p != CheckPhase.LowLight))
        {
            var failing = steps.Where(s => s.Phase == phase && s.FailedStreams > 0).ToList();
            if (failing.Count > 0)
            {
                var at = failing.Min(s => s.StreamsAtOnce);
                return string.Create(CultureInfo.InvariantCulture, $"Loss starts at {at:N0} {(at == 1 ? "camera" : "cameras")}");
            }
        }

        var lowLight = streams.Count(s => s.Phases[(int)CheckPhase.LowLight].Result is PhaseResult.Failed or PhaseResult.Interrupted);
        return lowLight == 0 ? null
            : lowLight == 1 ? "1 camera fails in Low light"
            : string.Create(CultureInfo.InvariantCulture, $"{lowLight:N0} cameras fail in Low light");
    }

    /// <summary>"100 %" when Normal passed in every batch that reached it, else the last ramp step that passed ("75 %"), else "0 %".</summary>
    public static string Headroom(IReadOnlyList<StepResult> steps)
    {
        bool Passed(CheckPhase phase) => steps.Any(s => s.Phase == phase) && steps.Where(s => s.Phase == phase).All(s => s.FailedStreams == 0);
        if (Passed(CheckPhase.Ramp25) && Passed(CheckPhase.Ramp50) && Passed(CheckPhase.Ramp75) && Passed(CheckPhase.Normal))
        {
            return "100 %";
        }

        var last = 0;
        foreach (var (phase, percent) in new[] { (CheckPhase.Ramp25, 25), (CheckPhase.Ramp50, 50), (CheckPhase.Ramp75, 75) })
        {
            if (!Passed(phase))
            {
                break;
            }

            last = percent;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{last} %");
    }
}

/// <summary>What one stream measured in one phase (sums of the per-second deltas).</summary>
internal sealed class PhaseAcc
{
    public PhaseResult Result { get; set; } = PhaseResult.Pending;

    public long Packets { get; set; }

    public long Lost { get; set; }

    public long Frames { get; set; }

    public long Expected { get; set; }

    public long Late { get; set; }

    public long Bytes { get; set; }

    public double JitterSum { get; set; }

    /// <summary>Seconds with data.</summary>
    public int Seconds { get; set; }

    public string? Problem { get; set; }

    public bool Interrupted { get; set; }

    public double? PacketLossPercent(bool udp) => !udp ? null : Packets + Lost == 0 ? 0 : 100.0 * Lost / (Packets + Lost);

    public double FrameLossPercent => Expected <= 0 ? 0 : 100.0 * Math.Max(0, Expected - Frames) / Expected;

    public double LatePercent => Frames <= 0 ? 0 : 100.0 * Late / Frames;

    public double Mbps => Seconds == 0 ? 0 : Bytes * 8 / 1_000_000.0 / Seconds;

    public double Jitter => Seconds == 0 ? 0 : JitterSum / Seconds;
}

/// <summary>One camera stream of a run: its probe while it streams, its phases and its per-second values in its batch.</summary>
internal sealed class StreamState(PlannedStream planned, int index)
{
    public PlannedStream Planned { get; } = planned;

    /// <summary>The position in the run's stream list.</summary>
    public int Index { get; } = index;

    public PhaseAcc[] Phases { get; } = [.. CheckRun.AllPhases.Select(_ => new PhaseAcc())];

    public IDeviceStreamProbe? Probe { get; set; }

    public Task<IDeviceStreamProbe>? Opening { get; set; }

    /// <summary>The counters of the last read (deltas are taken against it); reset with a new probe.</summary>
    public StreamProbeCounters Last { get; set; }

    /// <summary>Seconds of this phase without a first packet.</summary>
    public int WaitingSeconds { get; set; }

    /// <summary>Seconds in a row without a packet.</summary>
    public int SilentSeconds { get; set; }

    /// <summary>The phase's stream: measured now.</summary>
    public bool Active { get; set; }

    /// <summary>The exposure is at its maximum for Low light (else Low light is N/A).</summary>
    public bool ExposureRaised { get; set; }

    /// <summary>"UDP" or "TCP" once known; until then the route's (sites stream over TCP).</summary>
    public string? Transport { get; set; }

    public string? StreamText { get; set; }

    /// <summary>UDP was blocked and the probe fell back to TCP: a firewall finding, no failure.</summary>
    public bool UdpBlocked { get; set; }

    /// <summary>The note of a stream whose UDP was blocked.</summary>
    public const string UdpBlockedNote = "UDP is blocked between the camera and the server, packet loss not measured";

    public List<float> Mbps { get; } = [];

    public List<float> Loss { get; } = [];

    public List<float> Jitter { get; } = [];

    public bool Udp => (Transport ?? DefaultTransport) == "UDP";

    private string DefaultTransport => Planned.Device.SiteId is null ? "UDP" : "TCP";

    public bool HasFailure => Phases.Any(p => p.Result is PhaseResult.Failed or PhaseResult.Interrupted);

    /// <summary>The worst average packet or frame loss of a phase that has values.</summary>
    public double WorstLoss
    {
        get
        {
            var worst = 0.0;
            foreach (var p in Phases.Where(p => p.Seconds > 0))
            {
                worst = Math.Max(worst, Math.Max(p.PacketLossPercent(Udp) ?? 0, p.FrameLossPercent));
            }

            return worst;
        }
    }

    public StreamPhase PhaseOf(CheckPhase phase)
    {
        var p = Phases[(int)phase];
        return new StreamPhase(phase, p.Result, Round(p.PacketLossPercent(Udp)), Round(p.FrameLossPercent), Round(p.LatePercent), Round(p.Mbps), Round(p.Jitter), p.Problem);
    }

    public StreamRow Row()
    {
        var phases = CheckRun.AllPhases.Select(PhaseOf).ToList();
        var measured = Phases.Where(p => p.Seconds > 0).ToList();
        var normal = Phases[(int)CheckPhase.Normal];
        var rate = normal.Seconds > 0 ? normal.Mbps : measured.Count > 0 ? measured[^1].Mbps : 0;
        var device = Planned.Device;
        return new StreamRow(
            Planned.StreamId,
            device.Id,
            Planned.Source.Camera,
            CheckPlanner.NameOf(device),
            device.Address,
            device.Model,
            Planned.Source.Name,
            Planned.Batch,
            Transport ?? DefaultTransport,
            StreamText ?? Describe(Planned.Codec, Planned.Source.Width, Planned.Source.Height, Planned.Source.MaxFps),
            phases,
            Udp ? Round(measured.Count == 0 ? 0 : measured.Max(p => p.PacketLossPercent(true) ?? 0)) : null,
            Round(measured.Count == 0 ? 0 : measured.Max(p => p.FrameLossPercent)),
            Round(rate),
            Round(measured.Count == 0 ? 0 : measured.Max(p => p.Jitter)),
            Phases.FirstOrDefault(p => p.Problem is not null && p.Result is PhaseResult.Failed or PhaseResult.Interrupted)?.Problem,
            UdpBlocked ? UdpBlockedNote : null);
    }

    /// <summary>"H.265 1920x1080 30 fps".</summary>
    public static string Describe(string codec, int width, int height, int fps) =>
        string.Create(CultureInfo.InvariantCulture, $"{(codec == "h265" ? "H.265" : "H.264")} {width}x{height} {fps} fps");

    private static double Round(double value) => Math.Round(value, 2);

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 2) : null;
}
