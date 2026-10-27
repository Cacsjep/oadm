using System.Globalization;

using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LoadTest.Checking;

/// <summary>
/// The checks of the server: one at a time (a second Start is refused with the running check's user and progress), the
/// kept runs (<see cref="LoadTestInfo.MaxRuns"/>, the next one deletes the oldest) and the page's events. Device
/// groups (docs/spec/device-scope.md): the chosen ids must all be in the caller's devices (else NOT_FOUND, nothing read);
/// a run is owned by the device scope that ran it: users of that group list and open only its runs, users with all
/// devices every run. The streams and exposure writes are background work over the context (the call has no stream
/// probes), started only after the ids were checked against the caller.
/// </summary>
internal sealed class CheckService : IAsyncDisposable
{
    private readonly ICorePluginContext _ctx;
    private readonly LoadTestOptions _options;
    private readonly CheckPlanner _planner;
    private readonly RunStore _store;
    private readonly FirewallRuleKeeper _firewall;
    private readonly Lock _gate = new();
    private readonly List<StoredRun> _kept = [];
    private CheckRunner? _runner;
    private Task _current = Task.CompletedTask;
    private bool _starting;

    public CheckService(ICorePluginContext ctx, LoadTestOptions options)
    {
        _ctx = ctx;
        _options = options;
        var probes = ctx.StreamProbes ?? throw new InvalidOperationException("This OADM server cannot measure video streams.");
        _planner = new CheckPlanner(probes, options);
        _store = new RunStore(ctx.Records, ctx.DataDirectory);
        _firewall = new FirewallRuleKeeper(
            FirewallRule.ForServiceRange("Load test", FirewallProtocol.Udp, LoadTestInfo.UdpFirstPort, LoadTestInfo.UdpLastPort),
            ctx.Firewall,
            ctx.Logger);
    }

    /// <summary>The firewall rule over the RTP ports, open only while a check runs (tests).</summary>
    internal FirewallRuleKeeper Firewall => _firewall;

    /// <summary>The running check (tests).</summary>
    internal CheckRunner? Runner
    {
        get
        {
            lock (_gate)
            {
                return _runner;
            }
        }
    }

    /// <summary>Completes when the running check ended and was moved to the kept runs (tests).</summary>
    internal Task Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Loads the kept runs. A run that was running when the server stopped is kept as Failed ("The server stopped during the
    /// check"); its exposure values were in memory only.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        // A rule left by a crash during a check is removed.
        await _firewall.SyncAsync(false, ct).ConfigureAwait(false);
        var runs = await _store.LoadAsync(ct).ConfigureAwait(false);
        var kept = new List<StoredRun>();
        foreach (var run in runs)
        {
            if (run.Summary.State == RunState.Running)
            {
                var failed = run with
                {
                    Summary = run.Summary with
                    {
                        State = RunState.Failed,
                        Verdict = Verdicts.ServerStopped,
                        Passed = false,
                        EndedUtc = run.Summary.EndedUtc ?? run.Summary.StartedUtc,
                        Remaining = null,
                        Headroom = run.Summary.Headroom,
                    },
                };
                await _store.SaveSummaryAsync(failed, ct).ConfigureAwait(false);
                kept.Add(failed);
            }
            else
            {
                kept.Add(run);
            }
        }

        foreach (var old in kept.Skip(LoadTestInfo.MaxRuns).ToList())
        {
            await _store.DeleteAsync(old.Summary.RunId, ct).ConfigureAwait(false);
            kept.Remove(old);
        }

        lock (_gate)
        {
            _kept.Clear();
            _kept.AddRange(kept);
        }
    }

    public async Task<CheckPlan> PlanAsync(ICallContext caller, PlanRequest request, CancellationToken ct)
    {
        var devices = await ResolveAsync(caller, request.AllDevices ? null : request.DeviceIds, request.BatchSize, request.PhaseMinutes, ct).ConfigureAwait(false);
        var planned = await Detached.Run(() => _planner.PlanAsync(devices, request.BatchSize, request.PhaseMinutes, ct)).ConfigureAwait(false);
        return planned.Plan;
    }

    public async Task<RunSummary> StartAsync(ICallContext caller, StartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var devices = await ResolveAsync(caller, request.AllDevices ? null : request.DeviceIds, request.BatchSize, request.PhaseMinutes, ct).ConfigureAwait(false);
        lock (_gate)
        {
            RefuseWhileRunning();
            _starting = true;
        }

        try
        {
            var planned = await Detached.Run(() => _planner.PlanAsync(devices, request.BatchSize, request.PhaseMinutes, ct)).ConfigureAwait(false);
            if (planned.Plan.Refusal is { } refusal)
            {
                throw new ArgumentException(refusal);
            }

            if (planned.Streams.Count == 0)
            {
                throw new ArgumentException("None of the chosen cameras can be checked.");
            }

            var phaseSeconds = _options.PhaseSeconds ?? planned.PhaseMinutes * 60;
            var run = new CheckRun(Guid.NewGuid(), caller.UserName, caller.DeviceScopeKey, _options.Time.GetUtcNow(), planned, phaseSeconds);
            var exposure = new ExposureKeeper(_ctx.System.Vapix, _ctx.Logger, _options);
            var runner = new CheckRunner(run, _ctx.StreamProbes!, exposure, _store, _options, _ctx.Logger, PublishLive);

            // The RTP over UDP of the probes comes in on these ports, only while the check runs.
            await _firewall.SyncAsync(true, CancellationToken.None).ConfigureAwait(false);

            // The run is saved as running first: after a crash it is kept as Failed.
            await _store.SaveSummaryAsync(new StoredRun(run.Summary(), run.Owner), CancellationToken.None).ConfigureAwait(false);
            List<StoredRun> removed;
            lock (_gate)
            {
                _runner = runner;
                removed = _kept.Skip(LoadTestInfo.MaxRuns - 1).ToList();
                foreach (var old in removed)
                {
                    _kept.Remove(old);
                }

                runner.Start();
                _current = Detached.Run(() => FinishAsync(runner));
            }

            foreach (var old in removed)
            {
                await _store.DeleteAsync(old.Summary.RunId, CancellationToken.None).ConfigureAwait(false);
                PublishRuns(old.Owner);
            }

            PublishRuns(run.Owner);
            lock (run.Gate)
            {
                return run.Summary();
            }
        }
        finally
        {
            lock (_gate)
            {
                _starting = false;
            }
        }
    }

    /// <summary>Cancels the running check the caller sees: exposure restored, measured part kept. Null when none runs.</summary>
    public async Task<RunSummary?> CancelAsync(ICallContext caller)
    {
        CheckRunner? runner;
        Task current;
        lock (_gate)
        {
            runner = _runner;
            current = _current;
        }

        if (runner is null || !OwnerScope.Sees(caller, runner.Run.Owner))
        {
            return null;
        }

        runner.Stop(RunState.Cancelled);
        await current.ConfigureAwait(false);
        lock (runner.Run.Gate)
        {
            return runner.Run.Summary();
        }
    }

    public RunList ListRuns(ICallContext caller) => ListFor(caller.HasAllDevices ? ICallContext.AllDevicesScopeKey : caller.DeviceScopeKey, OwnerScope.HasNoDevices(caller));

    public async Task<RunDetail> GetRunAsync(ICallContext caller, RunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Skip < 0 || request.Take < 0)
        {
            throw new ArgumentException("Skip and Take must not be negative.");
        }

        var (summary, graphs, rows) = await ReadAsync(caller, request.RunId, ct).ConfigureAwait(false);
        var take = request.Take is 0 or > LoadTestInfo.MaxStreamsPerPage ? LoadTestInfo.MaxStreamsPerPage : request.Take;
        var page = rows.Skip(request.Skip).Take(take).ToList();
        return new RunDetail(summary, request.Skip == 0 ? graphs : new RunGraphs([], [], [], [], [], []), page, rows.Count);
    }

    public async Task<StreamDetail> GetStreamAsync(ICallContext caller, StreamRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (_, _, rows) = await ReadAsync(caller, request.RunId, ct).ConfigureAwait(false);
        var row = rows.FirstOrDefault(r => r.StreamId == request.StreamId) ?? throw new KeyNotFoundException(NoStream);
        var runner = Runner;
        if (runner is not null && runner.Run.Id == request.RunId)
        {
            var run = runner.Run;
            lock (run.Gate)
            {
                var stream = run.ById[row.StreamId];
                if (stream.Planned.Batch == run.Batch && stream.Mbps.Count > 0)
                {
                    return new StreamDetail(row, [.. run.BatchPhases], [.. stream.Mbps.Select(v => (double)v)], [.. stream.Loss.Select(v => (double)v)], [.. stream.Jitter.Select(v => (double)v)]);
                }
            }
        }

        var series = await _store.ReadBatchAsync(request.RunId, row.Batch, ct).ConfigureAwait(false);
        if (series is null || !series.Streams.TryGetValue(row.StreamId, out var values))
        {
            return new StreamDetail(row, [], [], [], []);
        }

        return new StreamDetail(row, series.Phases, [.. values.Mbps.Select(v => (double)v)], [.. values.Loss.Select(v => (double)v)], [.. values.Jitter.Select(v => (double)v)]);
    }

    public async Task DeleteRunAsync(ICallContext caller, RunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        StoredRun? kept;
        lock (_gate)
        {
            if (_runner is { } runner && runner.Run.Id == request.RunId && OwnerScope.Sees(caller, runner.Run.Owner))
            {
                throw new InvalidOperationException("Cancel the check first.");
            }

            kept = _kept.Find(r => r.Summary.RunId == request.RunId && OwnerScope.Sees(caller, r.Owner));
            if (kept is null)
            {
                throw new KeyNotFoundException(NoRun);
            }

            _kept.Remove(kept);
        }

        await _store.DeleteAsync(kept.Summary.RunId, ct).ConfigureAwait(false);
        PublishRuns(kept.Owner);
    }

    /// <summary>The plugin stops: the running check ends as Failed, its exposure goes back and its measured part is kept.</summary>
    public async ValueTask DisposeAsync()
    {
        CheckRunner? runner;
        Task current;
        lock (_gate)
        {
            runner = _runner;
            current = _current;
        }

        if (runner is not null)
        {
            runner.Stop(RunState.Failed, Verdicts.PluginStopped);
            await current.ConfigureAwait(false);
        }
    }

    /// <summary>The text of a run the caller cannot open (deleted, or another group's: the same answer).</summary>
    private const string NoRun = "The check was deleted.";

    private const string NoStream = "The camera is not in this check.";

    private void RefuseWhileRunning()
    {
        if (_starting)
        {
            throw new InvalidOperationException("A check is starting. Start again when it ends.");
        }

        if (_runner is { } runner)
        {
            RunSummary summary;
            lock (runner.Run.Gate)
            {
                summary = runner.Run.Summary();
            }

            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"A check by {summary.StartedBy} is running ({summary.Progress * 100:0} % done). Start again when it ends."));
        }
    }

    /// <summary>
    /// The chosen devices: a group user's ids must all be in the group (else the whole request is refused, NOT_FOUND); a user
    /// with all devices gets the devices that still exist, in the chosen order. Null ids (the page's "All devices"): every
    /// video device of the caller's devices.
    /// </summary>
    private static async Task<IReadOnlyList<IDeviceInfo>> ResolveAsync(ICallContext caller, IReadOnlyList<Guid>? ids, int batchSize, int phaseMinutes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (ids is null)
        {
            CheckPlanner.Validate(1, batchSize, phaseMinutes);
            var video = (await caller.Devices.ListAsync(ct).ConfigureAwait(false)).Where(d => d.HasVideo).ToList();
            return video.Count == 0 ? throw new ArgumentException("None of the chosen cameras can be checked.") : video;
        }

        CheckPlanner.Validate(ids.Count, batchSize, phaseMinutes);
        if (!caller.HasAllDevices)
        {
            return await CallDevices.RequireAsync(caller, ids, ct).ConfigureAwait(false);
        }

        var byId = (await caller.Devices.ListAsync(ct).ConfigureAwait(false)).ToDictionary(d => d.Id);
        var found = new List<IDeviceInfo>(ids.Count);
        foreach (var id in ids.Distinct())
        {
            if (byId.TryGetValue(id, out var device))
            {
                found.Add(device);
            }
        }

        return found.Count == 0 ? throw new DevicesNotAvailableException(ids.Count) : found;
    }

    private async Task FinishAsync(CheckRunner runner)
    {
        await runner.Completion.ConfigureAwait(false);
        await _firewall.SyncAsync(false, CancellationToken.None).ConfigureAwait(false);
        var run = runner.Run;
        StoredRun stored;
        lock (run.Gate)
        {
            stored = new StoredRun(run.Summary(), run.Owner);
        }

        lock (_gate)
        {
            _kept.Insert(0, stored);
            _runner = null;
        }

        PublishRuns(run.Owner);
        await runner.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The summary, graphs and rows of a run the caller sees; a group user gets only the rows of the group's devices.</summary>
    private async Task<(RunSummary Summary, RunGraphs Graphs, IReadOnlyList<StreamRow> Rows)> ReadAsync(ICallContext caller, Guid runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        RunSummary summary;
        RunGraphs graphs;
        IReadOnlyList<StreamRow> rows;
        CheckRunner? runner;
        StoredRun? kept;
        lock (_gate)
        {
            runner = _runner is { } r && r.Run.Id == runId && OwnerScope.Sees(caller, r.Run.Owner) ? r : null;
            kept = runner is null ? _kept.Find(k => k.Summary.RunId == runId && OwnerScope.Sees(caller, k.Owner)) : null;
        }

        if (runner is not null)
        {
            var run = runner.Run;
            lock (run.Gate)
            {
                summary = run.Summary();
                graphs = run.Graphs();
                rows = [.. run.Streams.Select(s => s.Row())];
            }
        }
        else if (kept is not null)
        {
            summary = kept.Summary;
            var detail = await _store.ReadDetailAsync(runId, ct).ConfigureAwait(false);
            graphs = detail?.Graphs ?? new RunGraphs([], [], [], [], [], []);
            rows = detail?.Streams ?? [];
        }
        else
        {
            throw new KeyNotFoundException(NoRun);
        }

        if (!caller.HasAllDevices)
        {
            var visible = (await caller.Devices.ListAsync(ct).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();
            rows = [.. rows.Where(r => visible.Contains(r.DeviceId))];
        }

        return (summary, graphs, Sorted(rows));
    }

    /// <summary>Worst first: interrupted, then failed, then by loss; then by name.</summary>
    private static List<StreamRow> Sorted(IEnumerable<StreamRow> rows) =>
        [.. rows
            .OrderByDescending(r => r.Phases.Any(p => p.Result == PhaseResult.Interrupted) ? 2 : r.Phases.Any(p => p.Result == PhaseResult.Failed) ? 1 : 0)
            .ThenByDescending(r => Math.Max(r.PacketLossPercent ?? 0, r.FrameLossPercent))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Camera)];

    private RunList ListFor(string scopeKey, bool noDevices = false)
    {
        bool Sees(string owner) => !noDevices && (scopeKey == ICallContext.AllDevicesScopeKey || string.Equals(OwnerScope.Of(owner), scopeKey, StringComparison.OrdinalIgnoreCase));
        var runs = new List<RunSummary>();
        Guid? running = null;
        CheckRunner? runner;
        List<StoredRun> kept;
        lock (_gate)
        {
            runner = _runner;
            kept = [.. _kept];
        }

        if (runner is not null && Sees(runner.Run.Owner))
        {
            lock (runner.Run.Gate)
            {
                runs.Add(runner.Run.Summary());
            }

            running = runner.Run.Id;
        }

        runs.AddRange(kept.Where(k => Sees(k.Owner)).Select(k => k.Summary));
        return new RunList([.. runs.OrderByDescending(r => r.StartedUtc)], running);
    }

    /// <summary>The run list to users with all devices and to the users of the run's group.</summary>
    private void PublishRuns(string owner)
    {
        if (_ctx.Events is not { } events)
        {
            return;
        }

        foreach (var scope in ScopesOf(owner))
        {
            events.Publish(LoadTestMethods.RunsTopic, LoadTestJson.Serialize(ListFor(scope)), PluginEventAudience.DeviceScope(scope));
        }
    }

    /// <summary>One live event per second (rows in chunks), to users with all devices and to the users of the run's group.</summary>
    private void PublishLive(CheckRun run)
    {
        if (_ctx.Events is not { } events)
        {
            return;
        }

        RunSummary summary;
        (int FromSecond, RunGraphs Points, IReadOnlyList<StreamRow> Rows) taken;
        lock (run.Gate)
        {
            summary = run.Summary();
            taken = run.TakeNew();
        }

        var payloads = new List<string>();
        var chunks = taken.Rows.Chunk(_options.MaxRowsPerEvent).ToList();
        payloads.Add(LoadTestJson.Serialize(new LiveEvent(summary, taken.FromSecond, taken.Points, chunks.Count > 0 ? chunks[0] : [])));
        var end = taken.FromSecond + taken.Points.Phases.Count;
        var empty = new RunGraphs([], [], [], [], [], []);
        payloads.AddRange(chunks.Skip(1).Select(chunk => LoadTestJson.Serialize(new LiveEvent(summary, end, empty, chunk))));
        foreach (var scope in ScopesOf(run.Owner))
        {
            foreach (var payload in payloads)
            {
                events.Publish(LoadTestMethods.LiveTopic, payload, PluginEventAudience.DeviceScope(scope));
            }
        }
    }

    private static IEnumerable<string> ScopesOf(string owner)
    {
        yield return ICallContext.AllDevicesScopeKey;
        var key = OwnerScope.Of(owner);
        if (key != ICallContext.AllDevicesScopeKey)
        {
            yield return key;
        }
    }
}
