using Oadm.Plugins.Shared;
using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Events;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision.Pipelines;

/// <summary>
/// The page's previews (docs/spec/vision-center.md "Previews"): while a page shows a pipeline, the server runs its draft,
/// saved or not, as far as its check allows, with every node's output for the page, and publishes no rule events and changes
/// no counter of the saved pipeline (its counts start at 0). A preview of a saved watching pipeline the page did not change
/// shares that pipeline's stream and results; any other has its own stream (On action: 1 frame per second), counted against
/// the watching limits (also per site) after the saved pipelines. At most <see cref="VisionLimits.MaxPreviews"/>,
/// <see cref="VisionLimits.MaxPreviewsPerUser"/> per user (a further one ends that user's oldest); each belongs to the user
/// who started it and ends <see cref="VisionLimits.PreviewLease"/> after the page's last call. Its state reaches only that
/// user, at most every <see cref="VisionLimits.PreviewEvery"/>. Pause holds the latest frame in memory and runs the draft on it
/// again after every change (latest wins, one worker); nothing ever reaches the disk.
/// </summary>
internal sealed partial class PipelineRuntime
{
    public static readonly TimeSpan PreviewSweepInterval = TimeSpan.FromSeconds(5);

    /// <summary>The previews, their leases and limits (shared with Text recognition); also the lock of the previews' state.</summary>
    private readonly PreviewRegistry<Preview> _previews;
    private int _keptPreviews;
    private int _sentPreviews;

    /// <summary>The previews now (tests): id, whether it has its own stream, the pipeline whose stream it shares.</summary>
    public IReadOnlyList<(string Id, bool OwnStream, string? SharedWith)> Previews
    {
        get
        {
            lock (_previews)
            {
                return [.. _previews.Values.Select(p => (p.Id, p.Loop is not null, p.SharedWith))];
            }
        }
    }

    /// <summary>Preview results kept so far (tests wait on it).</summary>
    public int KeptPreviewResults => Volatile.Read(ref _keptPreviews);

    /// <summary>Preview events sent to the pages so far.</summary>
    public int SentPreviewEvents => Volatile.Read(ref _sentPreviews);

    /// <summary>
    /// Starts a preview of <paramref name="draft"/> for <paramref name="user"/> of the scope <paramref name="owner"/>, or keeps
    /// that user's preview <paramref name="previewId"/> alive with the new draft. Refuses with
    /// <see cref="PipelineRules.TooManyPreviews"/> beyond <see cref="VisionLimits.MaxPreviews"/>. Runs without the caller.
    /// </summary>
    public Task<PreviewReply> StartPreviewAsync(string? previewId, Pipeline draft, string owner, string user) => Detached(async () =>
    {
        ArgumentNullException.ThrowIfNull(draft);
        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            // Another camera: nothing of the old one stays under this id; a user's third preview ends their oldest.
            var (kept, ending) = _previews.Claim(previewId, owner, user, p => p.Draft.DeviceId == draft.DeviceId, PipelineRules.TooManyPreviews);
            if (ending is not null)
            {
                await EndPreviewsAsync(p => p == ending).ConfigureAwait(false);
            }

            var stored = PipelineRules.Stored(draft) with { OwnerScopeKey = owner == ICallContext.AllDevicesScopeKey ? null : owner };
            var preview = _previews.Open(kept, previewId, (id, order) => new Preview(id, owner, user, order));

            UpdateDraft(preview, stored);
            FollowEvents(_pipelines());
            if (preview.Held is { } held && !held.Fits(stored))
            {
                await ReleaseHeldAsync(preview).ConfigureAwait(false); // another source or size: that frame is not its picture
            }

            await ReconcileAsync(preview).ConfigureAwait(false);
            return ReplyOf(preview);
        }
        finally
        {
            _sync.Release();
        }
    });

    /// <summary>Ends the preview of <paramref name="user"/>; nothing when there is none.</summary>
    public Task StopPreviewAsync(string? previewId, string owner, string user) => Detached(async () =>
    {
        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await EndPreviewsAsync(p => p.Id == previewId && p.IsOf(owner, user)).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    });

    /// <summary>The draft of the preview <paramref name="previewId"/> of <paramref name="user"/>; null when it ended or is another user's.</summary>
    public Pipeline? PreviewDraftOf(string? previewId, string owner, string user)
    {
        lock (_previews)
        {
            return !string.IsNullOrEmpty(previewId) && _previews.TryGetValue(previewId, out var preview) && preview.IsOf(owner, user) ? preview.Draft : null;
        }
    }

    /// <summary>
    /// Holds the latest frame of the preview: in memory only, the bytes of its result (no copy), within the picture limits
    /// (<see cref="IInference.Decode"/>). Its own stream stops, a shared one is left alone; the draft runs on the frame again
    /// after every change. Runs without the caller.
    /// </summary>
    public Task<PauseReply> PausePreviewAsync(string? previewId, string owner, string user) => Detached(async () =>
    {
        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Preview? preview;
            FrameOutcome? last;
            lock (_previews)
            {
                if (string.IsNullOrEmpty(previewId) || !_previews.TryGetValue(previewId, out preview) || !preview.IsOf(owner, user))
                {
                    return new PauseReply();
                }
            }

            lock (preview)
            {
                last = preview.Held is { } kept ? kept.Outcome : preview.Last;
            }

            if (last is null)
            {
                return new PauseReply { Problem = PipelineRules.NothingToPause };
            }

            if (preview.Held is null)
            {
                if (!HeldPictures.Fits(last.Jpeg, Fits))
                {
                    return new PauseReply { Problem = PipelineRules.PictureTooLarge };
                }

                lock (_previews)
                {
                    // From here on no live result replaces the one held (KeepPreview).
                    var ranFor = preview.SharedWith is null && ReferenceEquals(last.Evaluator, preview.Evaluator) ? preview.DraftPrint : null;
                    preview.Held = new HeldFrame(last, preview.Draft, _previews, ex => LogBackgroundFailed(_logger, "the run of a paused frame", ex)) { RanFor = ranFor };
                    preview.SharedWith = null;
                }
            }

            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            preview.Problem = null;
            RunHeld(preview);
            return new PauseReply { Paused = true, Picture = last.Jpeg.ToArray(), State = EventOf(preview) };
        }
        finally
        {
            _sync.Release();
        }
    });

    /// <summary>The held frame goes and the preview runs live again. Runs without the caller.</summary>
    public Task<PreviewReply> ResumePreviewAsync(string? previewId, string owner, string user) => Detached(async () =>
    {
        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Preview? preview;
            lock (_previews)
            {
                if (string.IsNullOrEmpty(previewId) || !_previews.TryGetValue(previewId, out preview) || !preview.IsOf(owner, user))
                {
                    return new PreviewReply { PreviewId = previewId ?? string.Empty };
                }
            }

            await ReleaseHeldAsync(preview).ConfigureAwait(false);
            await ReconcileAsync(preview).ConfigureAwait(false);
            return ReplyOf(preview);
        }
        finally
        {
            _sync.Release();
        }
    });

    /// <summary>The latest state of a preview as its page gets it (tests; the live timer sends it).</summary>
    public PreviewEvent? PreviewStateOf(string previewId)
    {
        Preview? preview;
        lock (_previews)
        {
            if (!_previews.TryGetValue(previewId, out preview))
            {
                return null;
            }
        }

        return EventOf(preview);
    }

    private bool Fits(ReadOnlyMemory<byte> jpeg) => _inference.Fits(jpeg);

    /// <summary>A new draft: a changed graph gets a new evaluator (its counts start at 0) and its lists start empty.</summary>
    private void UpdateDraft(Preview preview, Pipeline draft)
    {
        var print = Print(draft);
        if (print == preview.DraftPrint)
        {
            preview.Draft = draft;
            return;
        }

        var check = CheckOf(draft);
        var runnable = check.GraphErrors.Count == 0 && draft.DeviceId != Guid.Empty;
        var evaluator = runnable ? Build(draft, preview: true) : null;
        lock (preview)
        {
            preview.Draft = draft;
            preview.DraftPrint = print;
            preview.Check = check;
            preview.Runnable = runnable;
            preview.Evaluator = evaluator;
            if (preview.Held is null)
            {
                preview.Last = null;
            }

            preview.Crossings.Clear();
            preview.Fires.Clear();
            preview.Dirty = true;
        }
    }

    private static PreviewReply ReplyOf(Preview preview) => new()
    {
        PreviewId = preview.Id,
        Problem = preview.Problem ?? preview.StreamProblem,
        Paused = preview.Held is not null,
    };

    /// <summary>
    /// The draft changed while a frame is held: one worker runs the newest draft on that frame. Latest wins: a burst of edits
    /// never queues runs, a result of an older draft is never shown. Under <see cref="_sync"/>.
    /// </summary>
    private void RunHeld(Preview preview)
    {
        lock (_previews)
        {
            if (preview.Held is { } held)
            {
                held.Worker.Kick(() => preview.Held == held && held.RanFor != preview.DraftPrint, ct => RunHeldOnceAsync(preview, held, ct));
            }
        }
    }

    /// <summary>One run of the held frame's worker: the newest draft on the frame.</summary>
    private async Task RunHeldOnceAsync(Preview preview, HeldFrame held, CancellationToken ct)
    {
        Pipeline draft;
        string print;
        lock (_previews)
        {
            draft = preview.Draft;
            print = preview.DraftPrint;
        }

        var evaluator = CheckOf(draft).GraphErrors.Count == 0 ? Build(draft, preview: true) : null;
        var outcome = evaluator is null
            ? null
            : await Process(new SerialWork(), evaluator, held.Outcome.Jpeg, ct, held.Outcome.At).ConfigureAwait(false);
        lock (_previews)
        {
            held.RanFor = print;
        }

        if (outcome is { Cancelled: true })
        {
            ct.ThrowIfCancellationRequested();
            return;
        }

        if (print == preview.DraftPrint)
        {
            if (outcome is null)
            {
                lock (preview)
                {
                    preview.Last = null;
                    preview.Dirty = true;
                }
            }
            else
            {
                KeepPreview(preview, outcome, held);
            }
        }
    }

    private async Task ReleaseHeldAsync(Preview preview)
    {
        HeldFrame? held;
        lock (_previews)
        {
            held = preview.Held;
            preview.Held = null;
        }

        if (held is null)
        {
            return;
        }

        await held.Worker.StopAsync().ConfigureAwait(false);
        lock (preview)
        {
            preview.Last = null;
            preview.Dirty = true;
        }
    }

    private List<Preview> SharingPreviews(string pipelineId)
    {
        lock (_previews)
        {
            return _previews.Count == 0 ? [] : [.. _previews.Values.Where(p => p.SharedWith == pipelineId)];
        }
    }

    /// <summary>
    /// Keeps a preview's result: its node outputs, its crossings and what its Event nodes would have sent (a preview never
    /// publishes). <paramref name="held"/>: the held frame it is of; null: a live result (dropped while a frame is held, or
    /// of an older draft's evaluator).
    /// </summary>
    private void KeepPreview(Preview preview, FrameOutcome outcome, HeldFrame? held)
    {
        if (outcome.Cancelled || outcome.Result is not { } result)
        {
            return;
        }

        lock (_previews)
        {
            if (!_previews.ContainsKey(preview.Id) || preview.Held != held)
            {
                return;
            }

            if (held is null && preview.SharedWith is null && !ReferenceEquals(outcome.Evaluator, preview.Evaluator))
            {
                return;
            }
        }

        lock (preview)
        {
            preview.Last = outcome;
            if (held is not null)
            {
                held.Outcome = outcome;
            }

            foreach (var (nodeId, output) in result.Nodes)
            {
                if (output.Crossings.Count > 0)
                {
                    Prepend(preview.Crossings, nodeId, output.Crossings.Reverse());
                }
            }

            foreach (var emission in result.Emissions)
            {
                Prepend(preview.Fires, emission.NodeId, [new RecentFire(outcome.At, FireText(emission))]);
            }

            preview.Dirty = true;
        }

        Interlocked.Increment(ref _keptPreviews);
    }

    private static void Prepend<T>(Dictionary<string, List<T>> lists, string nodeId, IEnumerable<T> newestFirst)
    {
        if (!lists.TryGetValue(nodeId, out var list))
        {
            lists[nodeId] = list = [];
        }

        list.InsertRange(0, newestFirst);
        if (list.Count > VisionLimits.RecentPerNode)
        {
            list.RemoveRange(VisionLimits.RecentPerNode, list.Count - VisionLimits.RecentPerNode);
        }
    }

    /// <summary>"On · Count 3 · Classes person car", "Fired · Direction In · Class person".</summary>
    internal static string FireText(Emission emission)
    {
        ArgumentNullException.ThrowIfNull(emission);
        var state = emission.State switch
        {
            EmissionState.On => "On",
            EmissionState.Off => "Off",
            _ => "Fired",
        };
        var values = emission.Values.Where(v => v.Value.Length > 0).Select(v => VisionEventTypes.LabelOf(v.Key) + " " + v.Value);
        return string.Join(" · ", values.Prepend(state));
    }

    /// <summary>
    /// <paramref name="output"/> without its objects' trails: only a Track node draws them, the nodes after it hand the same
    /// objects on (twelve points each were most of a busy preview's state).
    /// </summary>
    internal static NodeOutput WithoutTrails(NodeOutput output)
    {
        static bool Trailed(IReadOnlyList<VisionObject> list)
        {
            foreach (var o in list)
            {
                if (o.Trail.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        if (!Trailed(output.Objects) && !Trailed(output.Dropped))
        {
            return output;
        }

        return output with
        {
            Objects = [.. output.Objects.Select(o => o.Trail.Count > 0 ? o with { Trail = [] } : o)],
            Dropped = [.. output.Dropped.Select(o => o.Trail.Count > 0 ? o with { Trail = [] } : o)],
        };
    }

    /// <summary>The page's state of a preview: every node's output of its latest result, the recent lists, its check.</summary>
    private PreviewEvent EventOf(Preview preview)
    {
        lock (preview)
        {
            var draft = preview.Draft;
            var last = preview.Last;
            var nodes = new List<NodeOutput>();
            if (last?.Result is { } result)
            {
                foreach (var node in draft.Nodes)
                {
                    if (result.Nodes.TryGetValue(node.Id, out var output))
                    {
                        nodes.Add(node.Kind == NodeKinds.Track ? output : WithoutTrails(output));
                    }
                }
            }

            return new PreviewEvent
            {
                PreviewId = preview.Id,
                At = last?.At ?? _time.GetUtcNow(),
                Resolution = last?.Size ?? draft.Resolution,
                Nodes = nodes,
                RecentCrossings = preview.Crossings.ToDictionary(c => c.Key, c => (IReadOnlyList<Crossing>)[.. c.Value], StringComparer.Ordinal),
                RecentFires = preview.Fires.ToDictionary(f => f.Key, f => (IReadOnlyList<RecentFire>)[.. f.Value], StringComparer.Ordinal),
                Check = preview.Check,
                Paused = preview.Held is not null,
            };
        }
    }

    /// <summary>The preview timer: each preview with a new state sends it to its user (at most every <see cref="VisionLimits.PreviewEvery"/>).</summary>
    private void PublishPreviews()
    {
        List<Preview> previews;
        lock (_previews)
        {
            previews = [.. _previews.Values];
        }

        foreach (var preview in previews)
        {
            lock (preview)
            {
                if (!preview.Dirty)
                {
                    continue;
                }

                preview.Dirty = false;
            }

            var json = VisionJson.Serialize(EventOf(preview));
            _ctx.Events?.Publish(VisionMethods.PreviewTopic, json, PluginEventAudience.User(preview.User));
            Interlocked.Increment(ref _sentPreviews);
        }
    }

    /// <summary>
    /// Every preview again (the saved pipelines' loops changed). The saved pipelines come first: a preview's own stream that no
    /// longer fits beside them stops with <see cref="PipelineRules.PreviewBusy"/>, the oldest previews keep theirs. Under <see cref="_sync"/>.
    /// </summary>
    private async Task ReconcilePreviewsAsync()
    {
        List<Preview> previews;
        lock (_previews)
        {
            previews = [.. _previews.Values.OrderBy(p => p.Order)];
        }

        int watching;
        Dictionary<Guid, int> perSite;
        lock (_loops)
        {
            watching = _loops.Count;
            perSite = LoopsPerSiteLocked();
        }

        var left = MaxWatching - watching;
        foreach (var preview in previews.Where(p => p.Loop is not null))
        {
            var site = preview.Loop!.Site;
            if (left > 0 && (site is not { } at || perSite.GetValueOrDefault(at) < VisionLimits.MaxWatchingPerSite))
            {
                left--;
                if (site is { } s)
                {
                    perSite[s] = perSite.GetValueOrDefault(s) + 1;
                }

                continue;
            }

            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            preview.Problem = PipelineRules.PreviewBusy;
        }

        foreach (var preview in previews)
        {
            UpdateDraft(preview, preview.Draft); // a model may have changed under it (the print holds the models, made anew)
            await ReconcileAsync(preview).ConfigureAwait(false);
        }
    }

    private Dictionary<Guid, int> LoopsPerSiteLocked() =>
        _loops.Values.Where(l => l.Site is not null).GroupBy(l => l.Site!.Value).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>
    /// Brings one preview in line with its draft: a held frame needs no camera; a draft that cannot run streams nothing; a
    /// problem of the camera stops it; an unchanged saved watching pipeline shares its stream; otherwise its own stream, kept
    /// while camera, source, resolution and rate stay, within the watching limits. Under <see cref="_sync"/>.
    /// </summary>
    private async Task ReconcileAsync(Preview preview)
    {
        var draft = preview.Draft;
        if (preview.Held is not null)
        {
            // Only a camera that left the owner's devices or a deleted pipeline ends the pause (fail closed).
            var off = (await CameraAsync(draft, CancellationToken.None).ConfigureAwait(false)).Problem == PipelineRules.Off;
            if (!off && (draft.Id.Length == 0 || StoredOf(draft.Id) is not null))
            {
                await StopOwnStreamAsync(preview).ConfigureAwait(false);
                preview.SharedWith = null;
                preview.Problem = null;
                RunHeld(preview);
                return;
            }

            await ReleaseHeldAsync(preview).ConfigureAwait(false);
        }

        if (!preview.Runnable)
        {
            // Nothing to run: the page gets the check (the camera first, the graph's errors).
            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            preview.SharedWith = null;
            preview.Problem = null;
            return;
        }

        var (problem, camera) = await CameraAsync(draft, CancellationToken.None).ConfigureAwait(false);
        problem ??= Starting ? PipelineRules.Starting : null;
        problem ??= preview.Evaluator is null ? CannotRun : null;
        if (problem is not null)
        {
            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            preview.SharedWith = null;
            preview.Problem = problem;
            return;
        }

        if (draft.Id.Length > 0 && draft.Mode == PipelineMode.Watching && WatchOf(draft.Id) is { } saved && saved.Print == preview.DraftPrint)
        {
            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            lock (_previews)
            {
                preview.SharedWith = draft.Id;
            }

            preview.Problem = null;
            return;
        }

        lock (_previews)
        {
            preview.SharedWith = null;
        }

        if (preview.Loop is { } own && SameStream(own.Stream, draft))
        {
            preview.Problem = null;
            return;
        }

        await StopOwnStreamAsync(preview).ConfigureAwait(false);
        int watching;
        int atSiteLoops;
        var site = camera?.SiteId;
        lock (_loops)
        {
            watching = _loops.Count;
            atSiteLoops = site is { } s ? _loops.Values.Count(l => l.Site == s) : 0;
        }

        int ownStreams;
        int atSite;
        lock (_previews)
        {
            ownStreams = _previews.Values.Count(p => p.Loop is not null);
            atSite = site is { } s ? _previews.Values.Count(p => p.Loop is not null && p.Loop.Site == s) : 0;
        }

        if (watching + ownStreams >= MaxWatching || (site is not null && atSiteLoops + atSite >= VisionLimits.MaxWatchingPerSite))
        {
            preview.Problem = PipelineRules.PreviewBusy;
            return;
        }

        var stop = new CancellationTokenSource();
        var loop = new PreviewLoop(draft, stop) { Site = site };
        preview.Problem = null;
        preview.StreamProblem = null;
        var token = stop.Token;
        loop.Run = Task.Run(
            () => StreamAsync(
                draft,
                PipelineRules.StreamFps(draft),
                (jpeg, ct) => preview.Evaluator is { } evaluator
                    ? Process(preview.Serial, evaluator, jpeg, ct)
                    : Task.FromResult(new FrameOutcome(null, jpeg, draft.Resolution, _time.GetUtcNow(), null, null) { Cancelled = true }),
                outcome => KeepPreview(preview, outcome, held: null),
                p => preview.StreamProblem = p,
                token),
            CancellationToken.None);
        preview.Loop = loop;
    }

    private static async Task StopOwnStreamAsync(Preview preview)
    {
        if (preview.Loop is not { } loop)
        {
            return;
        }

        preview.Loop = null;
        preview.StreamProblem = null;
        await loop.Stop.CancelAsync().ConfigureAwait(false);
        await loop.Run.ConfigureAwait(false);
        loop.Stop.Dispose();
    }

    private async Task EndPreviewsAsync(Func<Preview, bool> which)
    {
        List<Preview> ending;
        lock (_previews)
        {
            ending = _previews.RemoveWhere(which);
        }

        foreach (var preview in ending)
        {
            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            await ReleaseHeldAsync(preview).ConfigureAwait(false);
            await preview.Serial.IdleAsync().ConfigureAwait(false);
            lock (preview)
            {
                preview.Last = null; // no frame of an ended preview stays in memory
            }
        }
    }

    private Task SweepPreviewsAsync() => Detached(async () =>
    {
        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            await EndPreviewsAsync(_previews.Expired()).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    });

    /// <summary>The same stream: camera, source, resolution and rate.</summary>
    private static bool SameStream(Pipeline a, Pipeline b) =>
        a.DeviceId == b.DeviceId && a.VideoSource == b.VideoSource && a.Resolution == b.Resolution && PipelineRules.StreamFps(a) == PipelineRules.StreamFps(b);

    /// <summary>The frame a paused preview keeps: server memory only, one per preview, with the result of its latest run.</summary>
    private sealed class HeldFrame(FrameOutcome outcome, Pipeline draft, object gate, Action<Exception> failed)
    {
        public FrameOutcome Outcome { get; set; } = outcome;

        /// <summary>Runs the newest draft on the frame, latest wins (shared with Text recognition).</summary>
        public LatestWinsWorker Worker { get; } = new(gate, failed);

        /// <summary>The print of the draft its latest result is of; null: none fits (it runs once more).</summary>
        public string? RanFor { get; set; }

        /// <summary>The frame is a picture of <paramref name="other"/>: its camera, video source and size, the same pipeline.</summary>
        public bool Fits(Pipeline other) =>
            other.DeviceId == draft.DeviceId && other.VideoSource == draft.VideoSource && other.Resolution == draft.Resolution
            && (draft.Id.Length == 0 || other.Id == draft.Id);
    }

    private sealed class PreviewLoop(Pipeline stream, CancellationTokenSource stop)
    {
        public Pipeline Stream { get; } = stream;

        public CancellationTokenSource Stop { get; } = stop;

        public Task Run { get; set; } = Task.CompletedTask;

        public Guid? Site { get; init; }
    }

    /// <summary>One page's preview. Its stream and state change under <see cref="_sync"/>; its results under its own lock.</summary>
    private sealed class Preview(string id, string owner, string user, long order) : PreviewEntry(id, owner, user, order)
    {
        private volatile Pipeline _draft = new();
        private volatile IPipelineEvaluator? _evaluator;
        private volatile string? _streamProblem;

        public Pipeline Draft
        {
            get => _draft;
            set => _draft = value;
        }

        public string DraftPrint { get; set; } = string.Empty;

        public GraphCheck Check { get; set; } = new();

        /// <summary>The draft has a camera and no error of the whole graph.</summary>
        public bool Runnable { get; set; }

        public IPipelineEvaluator? Evaluator
        {
            get => _evaluator;
            set => _evaluator = value;
        }

        public SerialWork Serial { get; } = new();

        public string? SharedWith { get; set; }

        public PreviewLoop? Loop { get; set; }

        public HeldFrame? Held { get; set; }

        public string? Problem { get; set; }

        public string? StreamProblem
        {
            get => _streamProblem;
            set => _streamProblem = value;
        }

        public FrameOutcome? Last { get; set; }

        public Dictionary<string, List<Crossing>> Crossings { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<RecentFire>> Fires { get; } = new(StringComparer.Ordinal);

        public bool Dirty { get; set; }
    }
}
