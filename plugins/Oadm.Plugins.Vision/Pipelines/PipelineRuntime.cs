using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Shared;
using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Events;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision.Pipelines;

/// <summary>
/// One frame's way through a pipeline: its result (null when it failed), the picture it came from and its size, and the
/// evaluator that ran it (a preview drops results of an older draft).
/// </summary>
internal sealed record FrameOutcome(FrameResult? Result, ReadOnlyMemory<byte> Jpeg, Resolution Size, DateTimeOffset At, string? Failure, object? Evaluator)
{
    /// <summary>The work was stopped: nothing counts.</summary>
    public bool Cancelled { get; init; }

    public bool Failed => Result is null || Failure is not null;
}

/// <summary>
/// The pipelines at work (docs/spec/vision-center.md "Running", "Load", "Events and the rule actions"):
/// <list type="bullet">
/// <item>one loop per saved watching pipeline whose check passes, on <see cref="IFrameSource.WatchAsync"/> at its rate: the
/// newest frame wins (older ones are dropped, never queued), one frame in work, up to <see cref="VisionLimits.MaxFramesInWork"/>
/// while <see cref="IInference.HasRoom"/>; each frame decoded once (<see cref="IInference.Decode"/>) beside the frames before
/// it, then through the pipeline's evaluator one at a time in frame order (<see cref="SerialWork"/>), its emissions published;</item>
/// <item>the action Run pipeline (<see cref="RunOnceAsync"/>) and Reset counter, the daily reset of Count nodes;</item>
/// <item>the watching limits (by the cores and <see cref="VisionLimits.MaxWatchingPerSite"/> per site), the problems, the
/// previews of the page (PipelineRuntime.Previews.cs) and the runtime stats (PipelineRuntime.Stats.cs).</item>
/// </list>
/// A pipeline runs only while its camera is in its owner's devices (fail closed). All work runs without the user's call.
/// </summary>
internal sealed partial class PipelineRuntime : IAsyncDisposable
{
    /// <summary>How often the pipelines' cameras are checked again.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>How often the daily resets of the Count nodes are looked for.</summary>
    public static readonly TimeSpan ResetCheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>The problem of a pipeline whose evaluator cannot be built.</summary>
    public const string CannotRun = "The pipeline cannot run. The server log has the details.";

    private readonly ICorePluginContext _ctx;
    private readonly VisionParts _parts;
    private readonly IModelLibrary _models;
    private readonly IInference _inference;
    private readonly IFrameSource _frames;
    private readonly VisionEventSink _events;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<IReadOnlyList<Pipeline>> _pipelines;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, Watch> _loops = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Runner> _runners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _streamProblems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _failing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _actionProblems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _singles = new();
    private readonly ConditionalWeakTable<Pipeline, string> _prints = new();
    private readonly StatsWindow _stats;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ITimer _recheck;
    private readonly ITimer _resets;
    private readonly ITimer _live;
    private readonly ITimer _sweep;
    private readonly ITimer _statsTimer;
    private volatile Dictionary<string, string> _problems = [];
    private volatile bool _stopped;
    private volatile Task _engineStart = Task.CompletedTask;
    private int _liveChanged;

    public PipelineRuntime(ICorePluginContext ctx, VisionParts parts, IModelLibrary models, IInference inference, IFrameSource frames, VisionEventSink events, Func<IReadOnlyList<Pipeline>> pipelines)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _parts = parts ?? throw new ArgumentNullException(nameof(parts));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _inference = inference ?? throw new ArgumentNullException(nameof(inference));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _pipelines = pipelines ?? throw new ArgumentNullException(nameof(pipelines));
        _logger = ctx.Logger;
        _time = parts.Time;
        _stats = new StatsWindow(_time);
        _statsLeases = new StatsLeases(_time);
        _previews = new PreviewRegistry<Preview>(_time, VisionLimits.MaxPreviews, VisionLimits.MaxPreviewsPerUser, VisionLimits.PreviewLease);
        MaxWatching = VisionLimits.MaxWatchingFor(parts.ProcessorCount);
        using (ExecutionContext.SuppressFlow())
        {
            _recheck = _time.CreateTimer(_ => _ = QuietlyAsync(SyncAsync, "the check of the pipelines"), null, RecheckInterval, RecheckInterval);
            _resets = _time.CreateTimer(_ => _ = QuietlyAsync(CheckResetsAsync, "the daily reset of the counters"), null, ResetCheckInterval, ResetCheckInterval);
            _live = _time.CreateTimer(_ => TickLive(), null, VisionLimits.LiveEvery, VisionLimits.LiveEvery);
            _sweep = _time.CreateTimer(_ => _ = QuietlyAsync(SweepPreviewsAsync, "the end of old previews"), null, PreviewSweepInterval, PreviewSweepInterval);
            _statsTimer = _time.CreateTimer(_ => TickStats(), null, PipelineStats.SampleInterval, PipelineStats.SampleInterval);
        }
    }

    /// <summary>Raised (without a caller) when the problems of the pipelines changed (<see cref="ProblemOf"/>).</summary>
    public event EventHandler? ProblemsChanged;

    /// <summary>Raised (without a caller, at most every <see cref="VisionLimits.LiveEvery"/>) when a pipeline's live line changed.</summary>
    public event EventHandler? LiveChanged;

    /// <summary>Watching pipelines at most on this server (its cores), previews' own streams included.</summary>
    public int MaxWatching { get; }

    /// <summary>The graphics card's test of a start runs: no pipeline runs yet.</summary>
    public bool Starting => !_engineStart.IsCompleted || SafeStarting();

    public IInference Inference => _inference;

    /// <summary>The emissions published so far (tests wait on it).</summary>
    public int PublishedEmissions => _events.Published;

    /// <summary>The ids of the pipelines with a loop now (tests).</summary>
    public IReadOnlyList<string> Watching
    {
        get
        {
            lock (_loops)
            {
                return [.. _loops.Keys];
            }
        }
    }

    /// <summary>Frames the saved pipelines' loops delivered so far (tests wait on it).</summary>
    public int DeliveredFrames => Volatile.Read(ref _delivered);

    private int _delivered;

    /// <summary>
    /// Why the pipeline does not run as of the last check (its camera, its graph, the watching limits), else why its stream
    /// gives no pictures, else why its models fail, else why the action's last run of an On action pipeline failed; null while it runs.
    /// </summary>
    public string? ProblemOf(string pipelineId) =>
        _problems.GetValueOrDefault(pipelineId) ?? _streamProblems.GetValueOrDefault(pipelineId)
        ?? _failing.GetValueOrDefault(pipelineId) ?? _actionProblems.GetValueOrDefault(pipelineId);

    /// <summary>The latest result of a saved pipeline (its stream's, or its last run's); null without one.</summary>
    public FrameOutcome? LastOf(string pipelineId)
    {
        lock (_loops)
        {
            if (_loops.TryGetValue(pipelineId, out var watch))
            {
                return watch.Last;
            }
        }

        return _runners.TryGetValue(pipelineId, out var runner) ? runner.Last : null;
    }

    /// <summary>The live line of a pipeline (the first Count node's, else the first Event node's); empty without one.</summary>
    public string LiveOf(Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return LastOf(pipeline.Id)?.Result is { } result ? LiveLine(pipeline, result) : string.Empty;
    }

    /// <summary>
    /// The plugin's start: hands "Use GPU" over without waiting. While the card's test runs no pipeline starts and the action
    /// waits; <paramref name="started"/> runs after the pipelines started.
    /// </summary>
    public void StartEngine(bool useGpu, Action started)
    {
        ArgumentNullException.ThrowIfNull(started);
        Task start;
        try
        {
            start = _inference.SetUseGpuAsync(useGpu);
        }
#pragma warning disable CA1031 // A card that cannot be tried leaves the CPU, which always works.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBackgroundFailed(_logger, "the test of the graphics card", ex);
            return;
        }

        if (start.IsCompletedSuccessfully)
        {
            return;
        }

        var ended = QuietlyAsync(() => start, "the test of the graphics card");
        _engineStart = ended;
        _ = QuietlyAsync(
            async () =>
            {
                await ended.ConfigureAwait(false);
                await SyncAsync().ConfigureAwait(false);
                started();
            },
            "starting the pipelines");
    }

    /// <summary>Applies "Use GPU" and waits for the card's try; the pipelines then go on where the models run.</summary>
    public async Task SetUseGpuAsync(bool useGpu)
    {
        await _inference.SetUseGpuAsync(useGpu).ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Brings the loops in line with the stored pipelines as they are when the lock is taken: a loop for every watching
    /// pipeline that may run (its camera, its check), within the watching limits (beyond them the limit is its problem); a
    /// pipeline whose graph, stream or models changed starts again, a new name goes on in the running loop. Runs without the caller.
    /// </summary>
    public Task SyncAsync() => Detached(SyncCoreAsync);

    /// <summary>Background work of a timer or an event: never throws, a failure is logged.</summary>
    internal async Task QuietlyAsync(Func<Task> work, string what)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopped)
        {
        }
        catch (ObjectDisposedException) when (_stopped)
        {
        }
#pragma warning disable CA1031 // A timer or event callback never throws; the next round tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBackgroundFailed(_logger, what, ex);
        }
    }

    /// <summary>The graph check of <paramref name="pipeline"/> with the stored models; a newer format is never run.</summary>
    public GraphCheck CheckOf(Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (pipeline.Version > Pipeline.CurrentVersion)
        {
            return new GraphCheck { GraphErrors = [PipelineRules.NewerVersion] };
        }

        try
        {
            return _parts.Check(pipeline, _models.Find);
        }
#pragma warning disable CA1031 // A check that fails is an error of the graph: the pipeline does not run.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBackgroundFailed(_logger, "the check of the pipeline " + pipeline.Name, ex);
            return new GraphCheck { GraphErrors = [CannotRun] };
        }
    }

    /// <summary>
    /// Runs one picture of <paramref name="pipeline"/> now (the action Run pipeline). An On action pipeline takes a picture and
    /// publishes its events; a second run beside it is refused with <see cref="PipelineRules.AlreadyRunning"/> (nothing is
    /// queued). A watching pipeline answers with its result of at most <see cref="VisionLimits.FreshFrame"/> ago, else with its
    /// frame in work once it ends, else with a picture of its own (its stream publishes the events, this run none). Runs
    /// without the caller.
    /// </summary>
    public Task<FrameResult> RunOnceAsync(Pipeline pipeline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (_stopped)
        {
            return Task.FromException<FrameResult>(new InvalidOperationException(PipelineRules.NotRunning));
        }

        var task = Detached(() => RunOnceCoreAsync(pipeline, ct));
        _singles.TryAdd(task, 0);
        _ = task.ContinueWith(t => _singles.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task<FrameResult> RunOnceCoreAsync(Pipeline pipeline, CancellationToken callerCt)
    {
        try
        {
            var result = await RunAndPublishAsync(pipeline, callerCt).ConfigureAwait(false);
            SetProblem(_actionProblems, pipeline.Id, null);
            return result;
        }
        catch (OperationCanceledException) when (!_stopped && !Starting && pipeline.Mode == PipelineMode.OnAction)
        {
            SetProblem(_actionProblems, pipeline.Id, DeviceMessages.TimedOut);
            throw;
        }
        catch (Exception ex) when (!_stopped && pipeline.Mode == PipelineMode.OnAction && ex is not OperationCanceledException
            && ex.Message is not (PipelineRules.AlreadyRunning or PipelineRules.Gone))
        {
            SetProblem(_actionProblems, pipeline.Id, ex.Message);
            throw;
        }
    }

    private async Task<FrameResult> RunAndPublishAsync(Pipeline pipeline, CancellationToken callerCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerCt, _stopping.Token);
        var ct = linked.Token;
        if ((await CameraAsync(pipeline, ct).ConfigureAwait(false)).Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        var check = CheckOf(pipeline);
        if (!check.Ok)
        {
            throw new InvalidOperationException(PipelineRules.FirstError(check, pipeline) ?? CannotRun);
        }

        await _engineStart.WaitAsync(ct).ConfigureAwait(false);
        var asked = _time.GetUtcNow();
        if (pipeline.Mode == PipelineMode.Watching)
        {
            if (WatchOf(pipeline.Id) is { } watch)
            {
                if (watch.Last is { Result: { } fresh } last && last.At >= asked - VisionLimits.FreshFrame)
                {
                    return fresh;
                }

                var next = watch.NextAsync;
                if (Volatile.Read(ref watch.InWork) > 0 && (await next.WaitAsync(ct).ConfigureAwait(false)) is { Result: { } inWork, Cancelled: false })
                {
                    return inWork;
                }
            }

            // No stream that ran a frame now: a picture of its own, through an evaluator of its own (no event: the stream
            // publishes the pipeline's events, its counts stay as they are).
            var own = Build(pipeline, preview: false) ?? throw new InvalidOperationException(CannotRun);
            var jpeg = await _frames.SingleAsync(FrameRequest.Of(pipeline), ct).ConfigureAwait(false);
            var outcome = await Process(new SerialWork(), own, jpeg, ct).ConfigureAwait(false);
            return outcome.Result ?? throw new InvalidOperationException(PipelineRules.ModelsFail);
        }

        var runner = RunnerOf(pipeline) ?? throw new InvalidOperationException(CannotRun);
        if (Interlocked.CompareExchange(ref runner.Busy, 1, 0) != 0)
        {
            throw new InvalidOperationException(PipelineRules.AlreadyRunning);
        }

        try
        {
            var jpeg = await _frames.SingleAsync(FrameRequest.Of(pipeline), ct).ConfigureAwait(false);
            var outcome = await Process(runner.Serial, runner.Evaluator, jpeg, ct).ConfigureAwait(false);
            if (StoredOf(pipeline.Id) is not { } stored)
            {
                throw new InvalidOperationException(PipelineRules.Gone); // deleted while it ran: no event
            }

            if (outcome.Result is not { } result)
            {
                throw new InvalidOperationException(outcome.Failure ?? PipelineRules.ModelsFail);
            }

            if (Print(stored) == runner.Print)
            {
                runner.Last = outcome;
                _events.Publish(stored, result.Emissions);
                MarkLive();
            }

            if (result.ModelFailure is not null)
            {
                throw new InvalidOperationException(PipelineRules.ModelsFail);
            }

            return result;
        }
        finally
        {
            Volatile.Write(ref runner.Busy, 0);
        }
    }

    /// <summary>
    /// The rule action Reset counter: the Count node <paramref name="nodeId"/> of a running pipeline starts again at 0 between
    /// two frames. False when the pipeline does not run now (its counts are 0 already).
    /// </summary>
    public async Task<bool> ResetCounterAsync(string pipelineId, string nodeId)
    {
        if (WatchOf(pipelineId) is { } watch)
        {
            await watch.Serial.RunAsync(() => watch.Evaluator.ResetCounter(nodeId)).ConfigureAwait(false);
            MarkLive();
            return true;
        }

        if (_runners.TryGetValue(pipelineId, out var runner))
        {
            await runner.Serial.RunAsync(() => runner.Evaluator.ResetCounter(nodeId)).ConfigureAwait(false);
            MarkLive();
            return true;
        }

        return false;
    }

    /// <summary>Stops every loop and waits for them; nothing starts afterwards.</summary>
    public async Task StopAsync()
    {
        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            await _stopping.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(_singles.Keys).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The cancelled runs end with their own errors; their callers see them.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            await _recheck.DisposeAsync().ConfigureAwait(false);
            await _resets.DisposeAsync().ConfigureAwait(false);
            await _live.DisposeAsync().ConfigureAwait(false);
            await _sweep.DisposeAsync().ConfigureAwait(false);
            await _statsTimer.DisposeAsync().ConfigureAwait(false);
            await StopLoopsAsync(_ => true).ConfigureAwait(false);
            await EndPreviewsAsync(_ => true).ConfigureAwait(false);
            foreach (var runner in _runners.Values)
            {
                await runner.Serial.IdleAsync().ConfigureAwait(false);
            }

            _runners.Clear();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Why the pipeline cannot run now, or null with its camera: <see cref="PipelineRules.Off"/> when the camera is not in its
    /// owner's devices, the device's refusal when OADM does not contact it (certificate changed, site offline).
    /// </summary>
    public async Task<(string? Problem, IDeviceInfo? Camera)> CameraAsync(Pipeline pipeline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var owner = OwnerScope.Of(pipeline.OwnerScopeKey);
        if (owner != ICallContext.AllDevicesScopeKey
            && (_ctx.DeviceScopes is not { } scopes || !scopes.Exists(owner) || !scopes.Contains(owner, pipeline.DeviceId)))
        {
            return (PipelineRules.Off, null);
        }

        if (await _ctx.System.Devices.FindAsync(pipeline.DeviceId, ct).ConfigureAwait(false) is not { } camera)
        {
            return (PipelineRules.Off, null);
        }

        return (DeviceMessages.Refusal(camera), camera);
    }

    private async Task SyncCoreAsync()
    {
        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            var pipelines = _pipelines();
            var problems = new Dictionary<string, string>(StringComparer.Ordinal);
            var wanted = new Dictionary<string, (Pipeline Pipeline, IDeviceInfo? Camera, string Print)>(StringComparer.Ordinal);
            var checks = new Dictionary<string, GraphCheck>(StringComparer.Ordinal);
            var perSite = new Dictionary<Guid, int>();
            foreach (var stored in pipelines)
            {
                var check = CheckOf(stored);
                checks[stored.Id] = check;
                var (problem, camera) = await CameraAsync(stored, CancellationToken.None).ConfigureAwait(false);
                if (problem is not null)
                {
                    problems[stored.Id] = problem;
                    continue;
                }

                if (!check.Ok)
                {
                    problems[stored.Id] = PipelineRules.FirstError(check, stored) ?? CannotRun; // rule 8: never run
                    continue;
                }

                if (stored.Mode != PipelineMode.Watching)
                {
                    continue;
                }

                if (wanted.Count >= MaxWatching)
                {
                    problems[stored.Id] = PipelineRules.TooManyWatching(MaxWatching);
                    continue;
                }

                if (camera?.SiteId is { } site)
                {
                    if (perSite.GetValueOrDefault(site) >= VisionLimits.MaxWatchingPerSite)
                    {
                        problems[stored.Id] = PipelineRules.TooManyWatchingAtSite(camera.SiteName);
                        continue;
                    }

                    perSite[site] = perSite.GetValueOrDefault(site) + 1;
                }

                wanted[stored.Id] = (stored, camera, Print(stored));
            }

            if (Starting)
            {
                foreach (var waiting in wanted.Keys)
                {
                    problems[waiting] = PipelineRules.Starting;
                }

                wanted.Clear();
            }

            // Only an On action pipeline keeps the result of its last action; a runner of a changed pipeline starts again.
            var actionGone = false;
            foreach (var id in _actionProblems.Keys)
            {
                if (!pipelines.Any(p => p.Id == id && p.Mode == PipelineMode.OnAction))
                {
                    actionGone |= _actionProblems.TryRemove(id, out _);
                }
            }

            foreach (var (id, runner) in _runners)
            {
                if (pipelines.FirstOrDefault(p => p.Id == id) is not { Mode: PipelineMode.OnAction } now || problems.ContainsKey(id) || Print(now) != runner.Print)
                {
                    _runners.TryRemove(id, out _);
                }
            }

            await StopLoopsAsync(loop => !wanted.TryGetValue(loop.Pipeline.Id, out var now) || now.Print != loop.Print
                || !string.Equals(now.Pipeline.OwnerScopeKey, loop.Pipeline.OwnerScopeKey, StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
            var started = new List<Watch>();
            lock (_loops)
            {
                foreach (var (pipeline, camera, print) in wanted.Values)
                {
                    if (_loops.TryGetValue(pipeline.Id, out var running))
                    {
                        running.Pipeline = pipeline; // a new name: the loop goes on with it
                        continue;
                    }

                    if (Build(pipeline, preview: false) is not { } evaluator)
                    {
                        problems[pipeline.Id] = CannotRun;
                        continue;
                    }

                    var watch = new Watch(pipeline, print, evaluator, new CancellationTokenSource())
                    {
                        Site = camera?.SiteId,
                        SiteName = camera?.SiteName,
                    };
                    watch.Resets = ResetsOf(pipeline);
                    _stats.Started(pipeline.Id);
                    var token = watch.Stop.Token;
                    watch.Run = Task.Run(() => WatchAsync(watch, token), CancellationToken.None);
                    _loops[pipeline.Id] = watch;
                    started.Add(watch);
                }
            }

            var before = _problems;
            _problems = problems;
            if (actionGone || before.Count != problems.Count || problems.Any(p => before.GetValueOrDefault(p.Key) != p.Value))
            {
                RaiseProblemsChanged();
            }

            // The on and off types: a started pipeline reports its evaluator's states (none), every other one is off.
            var types = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stored in pipelines)
            {
                var stateful = Events.VisionEventTypes.StatefulIdsOf(stored, checks[stored.Id]);
                types.UnionWith(stateful);
                if (started.Find(w => w.Pipeline.Id == stored.Id) is { } watch)
                {
                    _events.Report(stored, stateful, watch.Evaluator.CurrentStates);
                }
                else if (WatchOf(stored.Id) is null && !_runners.ContainsKey(stored.Id))
                {
                    _events.Report(stored, stateful, []);
                }
            }

            _events.Keep(types);

            // The previews follow: one may now share a pipeline's stream, or need its own again; the saved pipelines come first.
            await ReconcilePreviewsAsync().ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>The runner of an On action pipeline: its evaluator, kept while the pipeline stays the same (its counts go on).</summary>
    private Runner? RunnerOf(Pipeline pipeline)
    {
        var print = Print(pipeline);
        if (_runners.TryGetValue(pipeline.Id, out var kept) && kept.Print == print)
        {
            return kept;
        }

        if (Build(pipeline, preview: false) is not { } evaluator)
        {
            return null;
        }

        var runner = new Runner(print, evaluator) { Resets = ResetsOf(pipeline) };
        _runners[pipeline.Id] = runner;
        if (StoredOf(pipeline.Id) is { } stored)
        {
            _events.Report(stored, Events.VisionEventTypes.StatefulIdsOf(stored, CheckOf(stored)), evaluator.CurrentStates);
        }

        return runner;
    }

    /// <summary>The evaluator of <paramref name="pipeline"/>; a preview's gives every node's output. Null when it cannot be built.</summary>
    private IPipelineEvaluator? Build(Pipeline pipeline, bool preview)
    {
        try
        {
            var factory = preview ? _parts.PreviewEvaluator ?? _parts.Evaluator : _parts.Evaluator;
            return factory(pipeline, _models.Find, _inference, pipeline.Mode);
        }
#pragma warning disable CA1031 // A pipeline that cannot be built does not run; its problem says so.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBackgroundFailed(_logger, "building the pipeline " + pipeline.Name, ex);
            return null;
        }
    }

    /// <summary>
    /// What a running pipeline depends on: its graph and settings, its camera, stream and owner aside, and the stored models
    /// it names. Names, places on the canvas and the eye on the nodes do not count (they never restart a loop).
    /// </summary>
    internal string Print(Pipeline pipeline)
    {
        var own = _prints.GetValue(pipeline, static p => VisionJson.Serialize(p with
        {
            Id = string.Empty,
            Name = string.Empty,
            OwnerScopeKey = null,
            Nodes = [.. p.Nodes.Select(n => n with { Name = string.Empty, X = 0, Y = 0, ShowOnVideo = false })],
        }));
        var models = PipelineRules.ModelsOf(pipeline);
        return models.Count == 0 ? own : own + "\u001f" + VisionJson.Serialize(models.Select(_models.Find).ToList());
    }

    /// <summary>The stored pipeline of <paramref name="pipelineId"/>; null after a delete.</summary>
    private Pipeline? StoredOf(string pipelineId)
    {
        foreach (var pipeline in _pipelines())
        {
            if (pipeline.Id == pipelineId)
            {
                return pipeline;
            }
        }

        return null;
    }

    private Watch? WatchOf(string pipelineId)
    {
        lock (_loops)
        {
            return _loops.GetValueOrDefault(pipelineId);
        }
    }

    private async Task StopLoopsAsync(Func<Watch, bool> which)
    {
        List<Watch> stopping;
        lock (_loops)
        {
            stopping = [.. _loops.Values.Where(which)];
            foreach (var watch in stopping)
            {
                _loops.Remove(watch.Pipeline.Id);
                _stats.Stopped(watch.Pipeline.Id);
            }
        }

        foreach (var watch in stopping)
        {
            await watch.Stop.CancelAsync().ConfigureAwait(false);
        }

        var cleared = false;
        foreach (var watch in stopping)
        {
            await watch.Run.ConfigureAwait(false);
            await watch.Serial.IdleAsync().ConfigureAwait(false);
            watch.Stop.Dispose();
            cleared |= _streamProblems.TryRemove(watch.Pipeline.Id, out _);
            cleared |= _failing.TryRemove(watch.Pipeline.Id, out _);
        }

        if (cleared)
        {
            RaiseProblemsChanged();
        }
    }

    /// <summary>A saved watching pipeline: its stream through its evaluator, the results published (<see cref="DeliverSaved"/>).</summary>
    private Task WatchAsync(Watch watch, CancellationToken ct) => StreamAsync(
        watch.Pipeline,
        PipelineRules.StreamFps(watch.Pipeline),
        (jpeg, token) =>
        {
            Interlocked.Increment(ref watch.InWork);
            return Process(watch.Serial, watch.Evaluator, jpeg, token);
        },
        outcome =>
        {
            Interlocked.Decrement(ref watch.InWork);
            DeliverSaved(watch, outcome);
        },
        problem => SetProblem(_streamProblems, watch.Pipeline.Id, problem),
        ct);

    private void DeliverSaved(Watch watch, FrameOutcome outcome)
    {
        var id = watch.Pipeline.Id;
        try
        {
            if (outcome.Cancelled)
            {
                return;
            }

            if (outcome.Failed)
            {
                if (++watch.Failed == VisionLimits.FailedFramesForProblem)
                {
                    SetProblem(_failing, id, PipelineRules.ModelsFail);
                }
            }
            else if (watch.Failed > 0)
            {
                watch.Failed = 0;
                SetProblem(_failing, id, null);
            }

            if (outcome.Result is not { } result)
            {
                return;
            }

            _stats.Count(id);
            if (StoredOf(id) is not { } stored || Print(stored) != watch.Print)
            {
                return; // deleted or changed while the frame ran: the sync stops the loop, nothing is published
            }

            _events.Publish(stored, result.Emissions);
            watch.Last = outcome;
            foreach (var preview in SharingPreviews(id))
            {
                KeepPreview(preview, outcome, held: null);
            }

            MarkLive();
        }
#pragma warning disable CA1031 // A failed delivery is logged; the pipeline goes on with the next frame.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFrameFailed(_logger, watch.Pipeline.Name, ex);
        }
        finally
        {
            Interlocked.Increment(ref _delivered);
            watch.Delivered(outcome);
        }
    }

    /// <summary>
    /// Starts one frame: decoded once at once (beside the frames before it), then through <paramref name="evaluator"/> after
    /// every frame handed to <paramref name="serial"/> before it. A failure is logged and gives no result. Call it in the
    /// order of the frames.
    /// </summary>
    private Task<FrameOutcome> Process(SerialWork serial, IPipelineEvaluator evaluator, ReadOnlyMemory<byte> jpeg, CancellationToken ct, DateTimeOffset? at = null)
    {
        var time = at ?? _time.GetUtcNow();
        var decoded = Task.Run(() => _inference.Decode(jpeg), CancellationToken.None);
        return serial.RunAsync(async () =>
        {
            IVisionFrame frame;
            try
            {
                frame = await decoded.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A picture that cannot be decoded is a failed frame.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogDecodeFailed(_logger, ex);
                return new FrameOutcome(null, jpeg, Resolution.Default, time, ex.Message, evaluator);
            }

            using (frame)
            {
                var size = new Resolution(frame.Width, frame.Height);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await evaluator.RunAsync(frame, time, ct).ConfigureAwait(false);
                    return new FrameOutcome(result, jpeg, size, time, result.ModelFailure, evaluator);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return new FrameOutcome(null, jpeg, size, time, null, evaluator) { Cancelled = true };
                }
#pragma warning disable CA1031 // A failed frame is logged; the pipeline goes on with the next one.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogEvaluationFailed(_logger, ex);
                    return new FrameOutcome(null, jpeg, size, time, ex.Message, evaluator);
                }
            }
        });
    }

    /// <summary>
    /// One camera stream: a pump takes the frames into a slot of one (the newest replaces an unread one); one frame is in work,
    /// another starts beside it only while the models have room (<see cref="IInference.HasRoom"/>, at most
    /// <see cref="VisionLimits.MaxFramesInWork"/>). Results are delivered in the order of the frames; frames that find no room
    /// are dropped, newest wins, never queued. <paramref name="start"/> is called in the frames' order.
    /// </summary>
    private async Task StreamAsync(Pipeline stream, int fps, Func<ReadOnlyMemory<byte>, CancellationToken, Task<FrameOutcome>> start, Action<FrameOutcome> deliver, Action<string?> problem, CancellationToken ct)
    {
        var latest = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        var pump = Task.Run(() => PumpAsync(stream, fps, latest.Writer, problem, ct), CancellationToken.None);
        var inWork = new Queue<Task<FrameOutcome>>();
        try
        {
            var arrived = latest.Reader.WaitToReadAsync(ct).AsTask();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                while (inWork.TryPeek(out var head) && head.IsCompleted)
                {
                    deliver(await inWork.Dequeue().ConfigureAwait(false));
                }

                if (arrived.IsCompleted)
                {
                    if (!await arrived.ConfigureAwait(false))
                    {
                        break;
                    }

                    if (inWork.Count == 0 || (inWork.Count < VisionLimits.MaxFramesInWork && HasRoom()))
                    {
                        if (latest.Reader.TryRead(out var frame))
                        {
                            inWork.Enqueue(start(frame, ct));
                        }

                        arrived = latest.Reader.WaitToReadAsync(ct).AsTask();
                        continue;
                    }

                    // No room: the frame waits in the slot of one, a newer one replaces it; asked again when the oldest ends.
                    await Task.WhenAny(inWork.Peek(), Task.Delay(TimeSpan.FromSeconds(1.0 / Math.Max(1, fps)), _time, ct)).ConfigureAwait(false);
                    continue;
                }

                if (inWork.Count == 0)
                {
                    await arrived.ConfigureAwait(false);
                }
                else
                {
                    await Task.WhenAny(arrived, inWork.Peek()).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        finally
        {
            // Every frame in work ends before the loop does: the models are never freed under one.
            while (inWork.TryDequeue(out var left))
            {
                var done = await left.ConfigureAwait(false);
                deliver(ct.IsCancellationRequested ? done with { Cancelled = true } : done);
            }

            await pump.ConfigureAwait(false);
        }
    }

    private bool HasRoom()
    {
        try
        {
            return _inference.HasRoom;
        }
#pragma warning disable CA1031 // An inference that cannot tell has no room: one frame per pipeline.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private bool SafeStarting()
    {
        try
        {
            return _inference.Starting;
        }
#pragma warning disable CA1031 // An inference that cannot tell is not starting.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private async Task PumpAsync(Pipeline stream, int fps, ChannelWriter<ReadOnlyMemory<byte>> writer, Action<string?> problem, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _frames.WatchAsync(FrameRequest.Of(stream), fps, problem, ct).ConfigureAwait(false))
            {
                writer.TryWrite(frame);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
#pragma warning disable CA1031 // The frame source reopens by itself; one that fails for good ends the loop, logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStreamFailed(_logger, stream.Name, ex);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private void SetProblem(ConcurrentDictionary<string, string> problems, string pipelineId, string? problem)
    {
        var changed = problem is null
            ? problems.TryRemove(pipelineId, out _)
            : !string.Equals(problems.GetValueOrDefault(pipelineId), problem, StringComparison.Ordinal) && (problems[pipelineId] = problem) is not null;
        if (changed && !_stopped)
        {
            RaiseProblemsChanged();
        }
    }

    private void RaiseProblemsChanged()
    {
        try
        {
            ProblemsChanged?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // Telling the pages never stops the pipelines.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBackgroundFailed(_logger, "telling the pages of the pipelines' problems", ex);
        }
    }

    private void MarkLive() => Interlocked.Exchange(ref _liveChanged, 1);

    /// <summary>The live timer: the previews' new states to their users, and the pipeline list when a live line changed.</summary>
    private void TickLive()
    {
        try
        {
            PublishPreviews();
            if (Interlocked.Exchange(ref _liveChanged, 0) == 1)
            {
                LiveChanged?.Invoke(this, EventArgs.Empty);
            }
        }
#pragma warning disable CA1031 // A timer callback never throws; the next tick tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBackgroundFailed(_logger, "the live lines", ex);
        }
    }

    /// <summary>The live line of a result: the first Count node's, else the first Event node's.</summary>
    internal static string LiveLine(Pipeline pipeline, FrameResult result)
    {
        foreach (var kind in new[] { NodeKinds.Count, NodeKinds.Event })
        {
            foreach (var node in pipeline.Nodes.Where(n => n.Kind == kind))
            {
                if (result.Nodes.TryGetValue(node.Id, out var output) && output.Live.Length > 0)
                {
                    return output.Live;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>The Count nodes with a daily reset and the last day each was reset (today when its time has passed already).</summary>
    private Dictionary<string, (TimeOnly At, DateOnly Done)> ResetsOf(Pipeline pipeline)
    {
        var now = _time.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(now);
        var resets = new Dictionary<string, (TimeOnly, DateOnly)>(StringComparer.Ordinal);
        foreach (var node in pipeline.Nodes)
        {
            if (PipelineRules.ResetAt(node) is { } at)
            {
                resets[node.Id] = (at, TimeOnly.FromDateTime(now) >= at ? today : today.AddDays(-1));
            }
        }

        return resets;
    }

    /// <summary>The timer: every Count node whose daily time (server local time) has come resets once that day.</summary>
    private async Task CheckResetsAsync()
    {
        if (_stopped)
        {
            return;
        }

        var now = _time.GetLocalNow().DateTime;
        var today = DateOnly.FromDateTime(now);
        var time = TimeOnly.FromDateTime(now);
        List<(SerialWork Serial, IPipelineEvaluator Evaluator, Dictionary<string, (TimeOnly At, DateOnly Done)> Resets)> running;
        lock (_loops)
        {
            running = [.. _loops.Values.Select(w => (w.Serial, w.Evaluator, w.Resets)), .. _runners.Values.Select(r => (r.Serial, r.Evaluator, r.Resets))];
        }

        foreach (var (serial, evaluator, resets) in running)
        {
            List<string> due;
            lock (resets)
            {
                due = [.. resets.Where(r => time >= r.Value.At && r.Value.Done < today).Select(r => r.Key)];
                foreach (var node in due)
                {
                    resets[node] = (resets[node].At, today);
                }
            }

            foreach (var node in due)
            {
                await serial.RunAsync(() => evaluator.ResetCounter(node)).ConfigureAwait(false);
                MarkLive();
            }
        }
    }

    /// <summary>Work started from a user's call runs without it, like every other background work of the plugin.</summary>
    private static Task<T> Detached<T>(Func<Task<T>> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work, CancellationToken.None);
        }
    }

    private static Task Detached(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work, CancellationToken.None);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision Center: {What} failed")]
    private static partial void LogBackgroundFailed(ILogger logger, string what, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision Center: a frame of the pipeline {Pipeline} failed")]
    private static partial void LogFrameFailed(ILogger logger, string pipeline, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision Center: a picture could not be decoded")]
    private static partial void LogDecodeFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision Center: a frame failed in the pipeline's nodes")]
    private static partial void LogEvaluationFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision Center: the pictures of the pipeline {Pipeline} ended")]
    private static partial void LogStreamFailed(ILogger logger, string pipeline, Exception ex);

    /// <summary>A saved watching pipeline's stream and evaluator. Its name may change while it runs.</summary>
    private sealed class Watch(Pipeline pipeline, string print, IPipelineEvaluator evaluator, CancellationTokenSource stop)
    {
        private volatile Pipeline _pipeline = pipeline;
        private volatile FrameOutcome? _last;
        private TaskCompletionSource<FrameOutcome> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Frames started and not delivered yet.</summary>
        public int InWork;

        public Pipeline Pipeline
        {
            get => _pipeline;
            set => _pipeline = value;
        }

        public string Print { get; } = print;

        public IPipelineEvaluator Evaluator { get; } = evaluator;

        public SerialWork Serial { get; } = new();

        public CancellationTokenSource Stop { get; } = stop;

        public Task Run { get; set; } = Task.CompletedTask;

        public Guid? Site { get; init; }

        public string? SiteName { get; init; }

        /// <summary>Failed frames in a row (the loop's thread only).</summary>
        public int Failed { get; set; }

        public Dictionary<string, (TimeOnly At, DateOnly Done)> Resets { get; set; } = [];

        public FrameOutcome? Last
        {
            get => _last;
            set => _last = value;
        }

        /// <summary>Ends with the next delivered frame (take it before looking at <see cref="InWork"/>).</summary>
        public Task<FrameOutcome> NextAsync => Volatile.Read(ref _next).Task;

        public void Delivered(FrameOutcome outcome) =>
            Interlocked.Exchange(ref _next, new TaskCompletionSource<FrameOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(outcome);
    }

    /// <summary>An On action pipeline's evaluator (its counts go on from run to run) and its one run at a time.</summary>
    private sealed class Runner(string print, IPipelineEvaluator evaluator)
    {
        private volatile FrameOutcome? _last;

        public int Busy;

        public string Print { get; } = print;

        public IPipelineEvaluator Evaluator { get; } = evaluator;

        public SerialWork Serial { get; } = new();

        public Dictionary<string, (TimeOnly At, DateOnly Done)> Resets { get; init; } = [];

        public FrameOutcome? Last
        {
            get => _last;
            set => _last = value;
        }
    }
}
