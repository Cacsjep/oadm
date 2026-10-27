using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.LoadTest;

/// <summary>Ids, names and limits shared by the server part, the page and the tests (docs/spec/load-test.md).</summary>
public static class LoadTestInfo
{
    public const string PluginId = "oadm.load-test";
    public const string DisplayName = "Load test";
    public const string IconKey = "gauge";

    /// <summary>The plugin permission of Start, Cancel and Delete (Operator on, Viewer off); the page checks "oadm.load-test:run".</summary>
    public const string RunPermission = "run";

    /// <summary>Camera streams at once (one batch): default and maximum.</summary>
    public const int DefaultBatchSize = 50;
    public const int MaxBatchSize = 200;

    /// <summary>Camera streams of one check at most (in batches); more are refused with <see cref="TooManyStreams"/>.</summary>
    public const int MaxStreams = 1000;

    /// <summary>The refusal of a check over <see cref="MaxStreams"/>.</summary>
    public const string TooManyStreams = "A check takes at most 1,000 cameras. Choose fewer.";

    /// <summary>Minutes per phase the page offers; default 2.</summary>
    public static readonly IReadOnlyList<int> PhaseMinutes = [1, 2, 5];
    public const int DefaultPhaseMinutes = 2;

    /// <summary>Streams per page of <see cref="LoadTestMethods.GetRun"/> (every page stays well below the 16 MB of a message).</summary>
    public const int MaxStreamsPerPage = 5_000;

    /// <summary>Runs kept on the server; the next one deletes the oldest.</summary>
    public const int MaxRuns = 5;

    /// <summary>Pass limits per stream and phase (averages, in %), Installation Verifier's 1 % lost frames.</summary>
    public const double MaxPacketLossPercent = 1;
    public const double MaxFrameLossPercent = 1;
    public const double MaxLateFramePercent = 1;

    /// <summary>
    /// The jitter graphs' limit line (30 ms, the usual limit for real-time video on a network). Shown only, never failed
    /// on: jitter above it points to a congested network or switch.
    /// </summary>
    public const double JitterWarningMs = 30;

    /// <summary>A stream that sends nothing for this long, or does not start within <see cref="StartTimeout"/>, is Interrupted.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The client UDP ports of the RTP streams (two per stream) and the firewall rule over them.</summary>
    public const int UdpFirstPort = 50000;
    public const int UdpLastPort = 50799;
}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and live event topics.</summary>
public static class LoadTestMethods
{
    /// <summary><see cref="PlanRequest"/> -> <see cref="CheckPlan"/>: what a check of the chosen cameras would do (read only). Viewer.</summary>
    public const string Plan = "plan";

    /// <summary><see cref="StartRequest"/> -> <see cref="RunSummary"/>: starts a check (refused while one runs). Operator.</summary>
    public const string Start = "start";

    /// <summary>{} -> <see cref="RunSummary"/>?: cancels the running check (exposure restored, measured part kept). Operator.</summary>
    public const string Cancel = "cancel";

    /// <summary>{} -> <see cref="RunList"/>: the kept runs, newest first, and the running one. Viewer.</summary>
    public const string ListRuns = "listRuns";

    /// <summary><see cref="RunRequest"/> -> <see cref="RunDetail"/>: one run with its graphs and streams. Viewer.</summary>
    public const string GetRun = "getRun";

    /// <summary><see cref="StreamRequest"/> -> <see cref="StreamDetail"/>: one camera stream's own graphs. Viewer.</summary>
    public const string GetStream = "getStream";

    /// <summary><see cref="RunRequest"/> -> {}: deletes a kept run (not the running one). Operator.</summary>
    public const string DeleteRun = "deleteRun";

    /// <summary>Event: <see cref="LiveEvent"/> once per second while a check runs (new graph points, changed rows, progress).</summary>
    public const string LiveTopic = "live";

    /// <summary>Event: <see cref="RunList"/> when a run starts, ends or is deleted.</summary>
    public const string RunsTopic = "runs";
}

/// <summary>The phases of a batch, in order.</summary>
public enum CheckPhase
{
    /// <summary>25 % of the batch streaming.</summary>
    Ramp25 = 0,

    /// <summary>50 %.</summary>
    Ramp50 = 1,

    /// <summary>75 %.</summary>
    Ramp75 = 2,

    /// <summary>100 % of the batch, the cameras as they are.</summary>
    Normal = 3,

    /// <summary>100 %, the exposure value of each source at its maximum.</summary>
    LowLight = 4,
}

/// <summary>The result of one stream in one phase.</summary>
public enum PhaseResult
{
    /// <summary>Not reached yet.</summary>
    Pending = 0,

    /// <summary>Streaming now.</summary>
    Running = 1,

    /// <summary>Within every limit.</summary>
    Passed = 2,

    /// <summary>Above a limit (<see cref="StreamPhase.Problem"/> says which).</summary>
    Failed = 3,

    /// <summary>Did not start or stopped sending.</summary>
    Interrupted = 4,

    /// <summary>Did not run for this stream (not in this ramp step, no exposure value for low light).</summary>
    NotApplicable = 5,
}

/// <summary>How a run ended.</summary>
public enum RunState
{
    Running = 0,
    Completed = 1,
    Cancelled = 2,

    /// <summary>The run broke off (server stop, error); <see cref="RunSummary.Verdict"/> says why.</summary>
    Failed = 3,
}

/// <summary>Request of <see cref="LoadTestMethods.Plan"/>.</summary>
/// <param name="AllDevices">True: every video device of the caller's devices, <paramref name="DeviceIds"/> ignored.</param>
public sealed record PlanRequest(IReadOnlyList<Guid> DeviceIds, int BatchSize, int PhaseMinutes, bool AllDevices = false);

/// <summary>Request of <see cref="LoadTestMethods.Start"/>.</summary>
/// <param name="AllDevices">True: every video device of the caller's devices, <paramref name="DeviceIds"/> ignored.</param>
public sealed record StartRequest(IReadOnlyList<Guid> DeviceIds, int BatchSize, int PhaseMinutes, bool AllDevices = false);

/// <summary>A run by id; for <see cref="LoadTestMethods.GetRun"/> one page of its streams.</summary>
/// <param name="Skip">Streams to skip (worst first).</param>
/// <param name="Take">Streams of the page; 0 = <see cref="LoadTestInfo.MaxStreamsPerPage"/>, at most that.</param>
public sealed record RunRequest(Guid RunId, int Skip = 0, int Take = 0);

/// <summary>One stream of a run.</summary>
public sealed record StreamRequest(Guid RunId, string StreamId);

/// <summary>What a check of the chosen cameras would do.</summary>
/// <param name="Streams">Camera streams (one per video source that is not disabled).</param>
/// <param name="Batches">Batches one after the other.</param>
/// <param name="Duration">Expected time of the whole check.</param>
/// <param name="PeakMbps">Expected rate of the largest batch in Normal (estimate per resolution and codec).</param>
/// <param name="LowLightMbps">Expected rate of the largest batch in Low light.</param>
/// <param name="LeftOut">Chosen devices without video or not reachable, with the reason.</param>
/// <param name="Refusal">Why Start would be refused (<see cref="LoadTestInfo.TooManyStreams"/>); null when it can start.</param>
public sealed record CheckPlan(int Streams, int Batches, TimeSpan Duration, double PeakMbps, double LowLightMbps, IReadOnlyList<LeftOutDevice> LeftOut, string? Refusal = null);

/// <summary>A chosen device the check leaves out.</summary>
public sealed record LeftOutDevice(Guid DeviceId, string Name, string Reason);

/// <summary>The kept runs and the running one.</summary>
public sealed record RunList(IReadOnlyList<RunSummary> Runs, Guid? RunningId);

/// <summary>One run as the run list shows it.</summary>
/// <param name="Verdict">One line: "The network and cameras carry this load", "Loss starts at 150 cameras", ...</param>
/// <param name="Passed">Every stream passed every phase it ran.</param>
/// <param name="PeakMbps">The highest summed rate of a second.</param>
/// <param name="WorstLossPercent">The worst average packet or frame loss of a stream in a phase.</param>
/// <param name="Headroom">The last ramp step every stream passed: "100 %", "75 %", ...; null while running.</param>
/// <param name="Progress">0..1 while running.</param>
/// <param name="Batch">The running batch, 1-based.</param>
/// <param name="Phase">The running phase; null while the first streams connect.</param>
/// <param name="Remaining">Time left while running.</param>
/// <param name="RestoreWarnings">Cameras whose exposure could not be written back (name and original value).</param>
public sealed record RunSummary(
    Guid RunId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    string StartedBy,
    RunState State,
    int Streams,
    int Batches,
    int BatchSize,
    int PhaseMinutes,
    string Verdict,
    bool Passed,
    int FailedStreams,
    double PeakMbps,
    double WorstLossPercent,
    string? Headroom,
    double Progress,
    int Batch,
    CheckPhase? Phase,
    TimeSpan? Remaining,
    IReadOnlyList<string> RestoreWarnings);

/// <summary>A run: the summary, the graphs of all batches (only in the page with Skip 0) and one page of its streams.</summary>
/// <param name="Streams">The page of streams, worst first.</param>
/// <param name="TotalStreams">All streams of the run the caller sees.</param>
public sealed record RunDetail(RunSummary Summary, RunGraphs Graphs, IReadOnlyList<StreamRow> Streams, int TotalStreams = 0);

/// <summary>
/// The page's graphs, one value per second over the whole run (batches one after the other), with the phase of every
/// second so the page can mark the phases.
/// </summary>
/// <param name="Phases">The phase of each second.</param>
/// <param name="TotalMbps">Summed received rate.</param>
/// <param name="AverageLossPercent">Average packet loss over the streams (frame loss for TCP streams).</param>
/// <param name="WorstLossPercent">The worst stream of that second.</param>
/// <param name="AverageJitterMs">Average jitter.</param>
/// <param name="Steps">The ramp and phases of all batches: streams at once and the average loss, for the bar graph.</param>
public sealed record RunGraphs(
    IReadOnlyList<CheckPhase> Phases,
    IReadOnlyList<double> TotalMbps,
    IReadOnlyList<double> AverageLossPercent,
    IReadOnlyList<double> WorstLossPercent,
    IReadOnlyList<double> AverageJitterMs,
    IReadOnlyList<StepResult> Steps);

/// <summary>One bar of the ramp graph.</summary>
public sealed record StepResult(CheckPhase Phase, int StreamsAtOnce, double AverageLossPercent, int FailedStreams);

/// <summary>One camera stream of a run (one table row).</summary>
/// <param name="StreamId">"&lt;device id&gt;/&lt;camera&gt;".</param>
/// <param name="Transport">"UDP" or "TCP".</param>
/// <param name="Phases">The result per phase, in <see cref="CheckPhase"/> order (5 entries).</param>
/// <param name="PacketLossPercent">Worst phase average; null for TCP.</param>
/// <param name="FrameLossPercent">Worst phase average.</param>
/// <param name="AverageMbps">Average rate in Normal.</param>
/// <param name="JitterMs">Worst phase average.</param>
/// <param name="Problem">The first problem in plain words, null when every phase passed.</param>
/// <param name="Note">A finding that is no failure in plain words, e.g. UDP blocked by a firewall; null for none.</param>
public sealed record StreamRow(
    string StreamId,
    Guid DeviceId,
    int Camera,
    string Name,
    string Address,
    string? Model,
    string? SourceName,
    int Batch,
    string Transport,
    string? Stream,
    IReadOnlyList<StreamPhase> Phases,
    double? PacketLossPercent,
    double FrameLossPercent,
    double AverageMbps,
    double JitterMs,
    string? Problem,
    string? Note = null);

/// <summary>One stream in one phase.</summary>
public sealed record StreamPhase(CheckPhase Phase, PhaseResult Result, double? PacketLossPercent, double FrameLossPercent, double LateFramePercent, double AverageMbps, double JitterMs, string? Problem);

/// <summary>One stream's own graphs (per second over its batch).</summary>
public sealed record StreamDetail(StreamRow Row, IReadOnlyList<CheckPhase> Phases, IReadOnlyList<double> Mbps, IReadOnlyList<double> LossPercent, IReadOnlyList<double> JitterMs);

/// <summary>Once per second while a check runs: the summary, the new graph points since the last event and the changed rows.</summary>
/// <param name="FromSecond">Index of the first new point in the run's graphs.</param>
/// <param name="NewPoints">The new seconds since the last event; its <see cref="RunGraphs.Steps"/> is the full step list so far.</param>
public sealed record LiveEvent(RunSummary Summary, int FromSecond, RunGraphs NewPoints, IReadOnlyList<StreamRow> ChangedRows);

/// <summary>JSON of the page contract (camelCase, enums as strings).</summary>
public static class LoadTestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string? json) =>
        JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Options)
        ?? throw new ArgumentException("Missing payload.", nameof(json));
}
