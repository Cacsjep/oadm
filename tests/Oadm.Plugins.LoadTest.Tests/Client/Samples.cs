namespace Oadm.Plugins.LoadTest.Tests.Client;

/// <summary>
/// Realistic sample runs for the page tests and screenshots: 3 batches of 50 camera streams, 2 minutes per phase; a few
/// cameras fail in Low light, one is interrupted, one is at a site (TCP), one exposure was not restored.
/// </summary>
internal static class Samples
{
    public const int PhaseSeconds = 120;
    public const int BatchSeconds = PhaseSeconds * 5;

    public static readonly Guid RunId = Guid.Parse("6f1c2a10-0000-4000-8000-000000000001");

    private static readonly string[] Places = ["Entrance", "Parking", "Loading dock", "Lobby", "Warehouse", "Corridor", "Fence north", "Fence south", "Office", "Stairs"];
    private static readonly string[] Models = ["P3265-LVE", "Q3538-LVE", "M3088-V", "Q6135-LE", "P1468-LE", "M4317-PLVE"];

    public static CheckPlan Plan() => new(150, 3, TimeSpan.FromMinutes(31.5), 214.6, 318.2,
    [
        new LeftOutDevice(Guid.NewGuid(), "Speaker hall (10.0.3.20)", "No video"),
        new LeftOutDevice(Guid.NewGuid(), "Door station 4 (10.0.3.31)", "Not reachable"),
    ]);

    public static int Batch(int stream) => (stream / 50) + 1;

    /// <summary>The stream is in this ramp step (the first 25, 50, 75 % of its batch).</summary>
    private static bool InStep(int stream, CheckPhase phase) => phase switch
    {
        CheckPhase.Ramp25 => stream % 50 < 13,
        CheckPhase.Ramp50 => stream % 50 < 25,
        CheckPhase.Ramp75 => stream % 50 < 38,
        _ => true,
    };

    private static bool FailsLowLight(int i) => i is 57 or 61 or 66 or 72 or 80 or 88 or 93;

    private const int Interrupted = 104;
    private const int SiteCamera = 131;
    private const int NoExposure = 140;

    /// <summary>All 150 rows; with <paramref name="atSecond"/> the state at that second of the run (null = finished).</summary>
    public static List<StreamRow> Rows(int? atSecond = null)
    {
        var rows = new List<StreamRow>();
        for (var i = 0; i < 150; i++)
        {
            rows.Add(Row(i, atSecond));
        }

        return rows;
    }

    public static StreamRow Row(int i, int? atSecond = null)
    {
        var random = new Random((i * 7919) + 104729);
        var batch = Batch(i);
        var batchStart = (batch - 1) * BatchSeconds;
        var tcp = i == SiteCamera;
        var phases = new List<StreamPhase>();
        string? problem = null;
        foreach (var phase in Enum.GetValues<CheckPhase>())
        {
            var start = batchStart + ((int)phase * PhaseSeconds);
            var mbps = 3.2 + (random.NextDouble() * 2.4);
            var loss = random.NextDouble() * 0.12;
            var jitter = 1.2 + (random.NextDouble() * 2.5);
            PhaseResult result;
            string? phaseProblem = null;
            if (!InStep(i, phase))
            {
                result = PhaseResult.NotApplicable;
            }
            else if (atSecond is { } now && now < start)
            {
                result = PhaseResult.Pending;
            }
            else if (atSecond is { } then && then < start + PhaseSeconds)
            {
                result = PhaseResult.Running;
            }
            else if (i == Interrupted && phase >= CheckPhase.Normal)
            {
                result = PhaseResult.Interrupted;
                phaseProblem = "The stream stopped for 5 s in Normal";
                mbps = 1.1;
            }
            else if (phase == CheckPhase.LowLight && i == NoExposure)
            {
                result = PhaseResult.NotApplicable;
            }
            else if (phase == CheckPhase.LowLight && FailsLowLight(i))
            {
                result = PhaseResult.Failed;
                loss = 1.6 + (random.NextDouble() * 4.5);
                mbps *= 2.1;
                phaseProblem = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Packet loss {loss:0.0} % in Low light");
            }
            else
            {
                result = PhaseResult.Passed;
                if (phase == CheckPhase.LowLight)
                {
                    mbps *= 1.5;
                }
            }

            problem ??= phaseProblem;
            phases.Add(new StreamPhase(phase, result, tcp ? null : loss, loss * 0.8, 0.1, mbps, jitter, phaseProblem));
        }

        var ran = phases.Where(p => p.Result is not PhaseResult.NotApplicable and not PhaseResult.Pending).ToList();
        var place = Places[i % Places.Length];
        return new StreamRow(
            $"{Guid.Empty:N}/{i}",
            Guid.NewGuid(),
            1,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{place} {(i / Places.Length) + 1:00}"),
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"10.0.{i / 100}.{(i % 100) + 10}"),
            Models[i % Models.Length],
            null,
            batch,
            tcp ? "TCP" : "UDP",
            i % 3 == 0 ? "H.265 3840x2160 30 fps" : "H.264 1920x1080 30 fps",
            phases,
            tcp ? null : ran.Count == 0 ? 0 : ran.Max(p => p.PacketLossPercent ?? 0),
            ran.Count == 0 ? 0 : ran.Max(p => p.FrameLossPercent),
            phases[(int)CheckPhase.Normal].AverageMbps,
            ran.Count == 0 ? 0 : ran.Max(p => p.JitterMs),
            problem);
    }

    /// <summary>The run's graphs up to <paramref name="seconds"/>.</summary>
    public static RunGraphs Graphs(int seconds)
    {
        var random = new Random(7);
        var phases = new List<CheckPhase>();
        var total = new List<double>();
        var average = new List<double>();
        var worst = new List<double>();
        var jitter = new List<double>();
        for (var s = 0; s < seconds; s++)
        {
            var batch = (s / BatchSeconds) + 1;
            var phase = (CheckPhase)(s % BatchSeconds / PhaseSeconds);
            var share = phase switch { CheckPhase.Ramp25 => 13, CheckPhase.Ramp50 => 25, CheckPhase.Ramp75 => 38, _ => 50 };
            var rate = share * 4.4 * (phase == CheckPhase.LowLight ? 1.5 : 1) * (0.94 + (random.NextDouble() * 0.12));
            var bad = phase == CheckPhase.LowLight && batch == 2;
            phases.Add(phase);
            total.Add(rate);
            average.Add(bad ? 0.35 + (random.NextDouble() * 0.6) : random.NextDouble() * 0.06);
            worst.Add(bad ? 1.8 + (random.NextDouble() * 4.2) : random.NextDouble() * 0.25);
            jitter.Add(1.6 + (share / 25.0) + (random.NextDouble() * 0.8));
        }

        var steps = new List<StepResult>();
        for (var b = 0; b < Math.Min(3, (seconds + BatchSeconds - 1) / BatchSeconds); b++)
        {
            foreach (var phase in Enum.GetValues<CheckPhase>())
            {
                if ((b * BatchSeconds) + ((int)phase * PhaseSeconds) >= seconds)
                {
                    break;
                }

                var share = phase switch { CheckPhase.Ramp25 => 13, CheckPhase.Ramp50 => 25, CheckPhase.Ramp75 => 38, _ => 50 };
                var failed = phase == CheckPhase.LowLight && b == 1 ? 7 : phase >= CheckPhase.Normal && b == 2 ? 1 : 0;
                steps.Add(new StepResult(phase, share, failed > 0 && phase == CheckPhase.LowLight ? 0.62 : 0.03 + (0.01 * (int)phase), failed));
            }
        }

        return new RunGraphs(phases, total, average, worst, jitter, steps);
    }

    public static RunSummary Completed() => new(
        RunId, new DateTimeOffset(2026, 10, 27, 9, 12, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 27, 9, 44, 30, TimeSpan.Zero),
        "anna@ops-laptop", RunState.Completed, 150, 3, 50, 2, "7 cameras fail in Low light, 1 in Normal", false, 8, 331.4, 6.07, "75 %", 1, 3, null, null,
        ["Loading dock 08 (10.0.0.82): the exposure was not restored, it was 0"]);

    public static RunSummary Running(int second) => new(
        RunId, new DateTimeOffset(2026, 10, 27, 9, 12, 0, TimeSpan.Zero), null,
        "anna@ops-laptop", RunState.Running, 150, 3, 50, 2, "No loss so far", true, 0, 228.9, 0.24, null,
        second / (3.0 * BatchSeconds), (second / BatchSeconds) + 1, (CheckPhase)(second % BatchSeconds / PhaseSeconds),
        TimeSpan.FromSeconds((3 * BatchSeconds) - second), []);

    public static RunDetail CompletedDetail() => new(Completed(), Graphs(3 * BatchSeconds), Rows());

    public static RunDetail RunningDetail(int second) => new(Running(second), Graphs(second), Rows(second));

    public static StreamDetail Stream(StreamRow row)
    {
        var random = new Random(3);
        var seconds = BatchSeconds;
        var phases = new List<CheckPhase>();
        var mbps = new List<double>();
        var loss = new List<double>();
        var jitter = new List<double>();
        for (var s = 0; s < seconds; s++)
        {
            var phase = (CheckPhase)(s / PhaseSeconds);
            phases.Add(phase);
            mbps.Add((phase == CheckPhase.LowLight ? 9.4 : 4.3) * (0.85 + (random.NextDouble() * 0.3)));
            loss.Add(phase == CheckPhase.LowLight ? 1.2 + (random.NextDouble() * 4) : random.NextDouble() * 0.1);
            jitter.Add(1.8 + (random.NextDouble() * 1.4));
        }

        return new StreamDetail(row, phases, mbps, loss, jitter);
    }

    public static RunList RunList() => new(
    [
        Completed(),
        Completed() with { RunId = Guid.NewGuid(), StartedUtc = new DateTimeOffset(2026, 10, 24, 15, 3, 0, TimeSpan.Zero), StartedBy = "tom@site-pc", Streams = 48, Batches = 1, Verdict = "The network and cameras carry this load", Passed = true, FailedStreams = 0, RestoreWarnings = [] },
        Completed() with { RunId = Guid.NewGuid(), StartedUtc = new DateTimeOffset(2026, 10, 22, 11, 40, 0, TimeSpan.Zero), State = RunState.Cancelled, Streams = 200, Batches = 1, BatchSize = 200, Verdict = "Loss starts at 150 cameras", RestoreWarnings = [] },
    ], null);
}
