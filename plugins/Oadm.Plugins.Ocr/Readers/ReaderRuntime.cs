using System.Collections.Concurrent;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Events;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// The readers at work (docs/spec/ocr.md "Readers", "Events and the rule action"):
/// <list type="bullet">
/// <item>one loop per watching reader on <see cref="IFrameSource.WatchAsync(FrameRequest, int, Action{string?}, CancellationToken)"/>:
/// the newest frame wins (older ones are dropped, never queued), one frame in work per reader, up to
/// <see cref="OcrLimits.MaxFramesInWork"/> while the recognition has room, results in the frames' order, then per area the vote
/// and the event only on change (<see cref="AreaWatch"/>);</item>
/// <item>single readings (the action Read text) through <see cref="IFrameSource.SingleAsync"/> that publish every area's
/// event; a watching reader answers with its fresh reading instead (<see cref="ReadOnceAsync"/>);</item>
/// <item>the last reading of each reader (picture, results, cut-outs) and its last reads in memory only.</item>
/// </list>
/// A reader reads only while its camera is in its owner's devices (fail closed: a deleted group's readers read nothing).
/// All work runs without the user's call (background boundary of docs/spec/device-scope.md).
/// </summary>
internal sealed partial class ReaderRuntime : IAsyncDisposable
{
    private readonly ICorePluginContext _ctx;
    private readonly ILogger _logger;
    private readonly IFrameSource? _frames;
    private readonly ITextRecognizer? _recognizer;
    private readonly ReadingMaker? _maker;
    private readonly IPictureCutter? _cutter;
    private readonly Func<ReadOnlyMemory<byte>, bool> _pictureFits;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ReaderReading, IReadOnlyList<AreaDetails>> _cutouts = new();
    private readonly ConcurrentDictionary<string, (string Signature, DateTimeOffset At)> _published = new(StringComparer.Ordinal);
    private int _kept;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, WatchLoop> _loops = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReaderSlot> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReaderReading> _last = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _actionProblems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _readingProblems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _streamProblems = new(StringComparer.Ordinal);
    private readonly ITimer _recheck;
    private readonly ITimer _sweep;
    private readonly ITimer _statsTimer;
    private readonly ReaderStats _stats;
    private readonly TimeProvider _time;
    private readonly Func<IReadOnlyList<Reader>> _readers;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Task, byte> _singles = new();
    private volatile Dictionary<string, string> _problems = [];
    private volatile bool _stopped;
    private volatile Task _engineStart = Task.CompletedTask;
    private int _dropped;

    /// <summary>Frames of watching readers dropped because a single reading of the same reader ran (tests).</summary>
    public int DroppedWhileBusy => Volatile.Read(ref _dropped);

    /// <param name="ctx">The plugin's context.</param>
    /// <param name="parts">The recognition, matcher, pictures and clock.</param>
    /// <param name="logger">The plugin's log.</param>
    /// <param name="readers">
    /// The stored readers as they are now; read under the runtime's lock on every sync, so an older list never wins over a
    /// newer one (a deleted reader never streams again).
    /// </param>
    public ReaderRuntime(ICorePluginContext ctx, ReaderParts parts, ILogger logger, Func<IReadOnlyList<Reader>> readers)
    {
        _readers = readers ?? throw new ArgumentNullException(nameof(readers));
        ArgumentNullException.ThrowIfNull(parts);
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _frames = parts.Frames?.Invoke(ctx);
        _cutter = parts.Cutter;
        _pictureFits = parts.PictureFits;
        _recognizer = parts.Recognizer?.Invoke(ctx);
        _stats = new ReaderStats(parts.Time, parts.ProcessorTime, parts.ProcessorCount);
        MaxWatching = OcrLimits.MaxWatching(parts.ProcessorCount);
        _maker = _recognizer is not null && parts.Matcher is not null ? new ReadingMaker(_recognizer, parts.Matcher, parts.Time, _stats) : null;
        using (ExecutionContext.SuppressFlow())
        {
            _statsTimer = parts.Time.CreateTimer(_ => TickStats(), null, ReaderStats.SampleInterval, ReaderStats.SampleInterval);
            _recheck = parts.Time.CreateTimer(_ => _ = QuietlyAsync(SyncAsync, "the check of the readers"), null, RecheckInterval, RecheckInterval);
            _sweep = parts.Time.CreateTimer(_ => _ = QuietlyAsync(SweepPreviewsAsync, "the end of old previews"), null, PreviewSweepInterval, PreviewSweepInterval);
        }

        _time = parts.Time;
        _readIds = parts.Time.GetUtcNow().ToUnixTimeMilliseconds(); // a plugin started again never repeats an id the page holds
    }

    /// <summary>How often the readers' cameras are checked again (a camera that refused connections starts when it is Ok).</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>Raised (without a caller) when the problems of the readers changed (<see cref="ProblemOf"/>).</summary>
    public event EventHandler? ProblemsChanged;

    /// <summary>Watching readers at most on this server (its cores, <see cref="OcrLimits.MaxWatching"/>), previews' own streams included.</summary>
    public int MaxWatching { get; }

    /// <summary>True while the recognition uses a graphics card (<see cref="IEngineInfo.OnGpu"/>).</summary>
    public bool OnGpu => _recognizer is IEngineInfo { OnGpu: true };

    /// <summary>What the recognition runs on and "Use GPU"; null while the recognizer does not tell.</summary>
    public IEngineInfo? Engine => _recognizer as IEngineInfo;

    /// <summary>The graphics card's test of the plugin's start runs: no reader reads yet (docs/spec/ocr.md "GPU").</summary>
    public bool Starting => !_engineStart.IsCompleted;

    /// <summary>
    /// The plugin's start: hands "Use GPU" over without waiting. While a graphics card's test runs no reader starts and the action
    /// waits, so every reader starts where it stays: on the card, or on the CPU at once when the card fails or the test passes
    /// its limit. <paramref name="started"/> runs after the readers started.
    /// </summary>
    public void StartEngine(bool useGpu, Action started)
    {
        if (Engine?.SetUseGpu(useGpu) is not { IsCompletedSuccessfully: false } start)
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
            "starting the readers");
    }

    /// <summary>The ids of the readers with a loop now (tests).</summary>
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

    /// <summary>The frame rate each loop reads at now (tests: a rate the recognition no longer offers is lowered).</summary>
    internal int? LoopFps(string readerId)
    {
        lock (_loops)
        {
            return _loops.TryGetValue(readerId, out var loop) ? loop.Reader.Fps : null;
        }
    }

    /// <summary>
    /// Why the reader cannot read now as of the last check (<see cref="CheckAsync"/>, the watching limits), else why its
    /// stream gives no pictures, else why its readings fail, else why the action's last reading of an On action reader failed;
    /// null while it reads.
    /// </summary>
    public string? ProblemOf(string readerId) =>
        _problems.GetValueOrDefault(readerId) ?? _streamProblems.GetValueOrDefault(readerId)
        ?? _readingProblems.GetValueOrDefault(readerId) ?? _actionProblems.GetValueOrDefault(readerId);

    /// <summary>The last reading of a reader; null without one.</summary>
    public ReaderReading? Last(string readerId) => _last.TryGetValue(readerId, out var reading) ? reading : null;

    /// <summary>Drops the last reading of a reader (its camera or areas changed); its rows stay, each area's next value is a new one.</summary>
    public void Forget(string readerId)
    {
        _last.TryRemove(readerId, out _);
        if (_recent.TryGetValue(readerId, out var reads))
        {
            reads.ForgetValues();
        }

        SetProblem(_actionProblems, readerId, null);
    }

    /// <summary>Drops everything of a deleted reader (after <see cref="SyncAsync"/> stopped its loop).</summary>
    public void Remove(string readerId)
    {
        _published.TryRemove(readerId, out _);
        _last.TryRemove(readerId, out _);
        _recent.TryRemove(readerId, out _);
        _slots.TryRemove(readerId, out _);
        _streamProblems.TryRemove(readerId, out _);
        _readingProblems.TryRemove(readerId, out _);
        _actionProblems.TryRemove(readerId, out _);
    }

    /// <summary>
    /// Brings the loops in line with the stored readers as they are when the lock is taken: a loop for every watching reader
    /// with areas that may read, within the watching limits (a reader beyond them gets the limit as its problem; text is read
    /// only inside areas, so a reader without one opens no stream), at a frame rate the recognition offers now. A reader whose
    /// stream and areas changed starts again; a change of its name or of its areas' names goes on in the running loop. The
    /// others stop. Runs without the caller.
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

    /// <summary>A reading of a watching reader at most this old answers the action Read text (no second picture is taken).</summary>
    public static readonly TimeSpan FreshReading = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Reads one picture of <paramref name="reader"/> now and publishes the event of every area (Text read or No text read), changed or not. A watching reader
    /// answers with its reading of at most <see cref="FreshReading"/> ago, else with the reading in work once it ends (its
    /// stream reads all the time). Refuses with <see cref="OcrInfo.AlreadyReading"/> while another single reading of the reader
    /// runs (nothing is queued), with <see cref="ReaderRules.Off"/> when its camera is not in its owner's devices and with
    /// <see cref="ReaderRules.Gone"/> when the reader was deleted while it read. Runs without the caller.
    /// </summary>
    public Task<ReaderReading> ReadOnceAsync(Reader reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (_stopped)
        {
            return Task.FromException<ReaderReading>(new InvalidOperationException(ReaderRules.NotRunning));
        }

        // Tracked: the stop cancels it and waits for it before the recognizer goes.
        var task = Detached(() => ReadOnceCoreAsync(reader, ct));
        _singles.TryAdd(task, 0);
        _ = task.ContinueWith(t => _singles.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>
    /// <see cref="ReadAndFireAsync"/> with the action's result as the problem of an On action reader: it has no stream that could
    /// tell, so a failed reading is on until a reading works. "Already reading" and a deleted reader are no problem of a reader.
    /// </summary>
    private async Task<ReaderReading> ReadOnceCoreAsync(Reader reader, CancellationToken callerCt)
    {
        try
        {
            var reading = await ReadAndFireAsync(reader, callerCt).ConfigureAwait(false);
            SetProblem(_actionProblems, reader.Id, null);
            return reading;
        }
        catch (OperationCanceledException) when (!_stopped && !Starting && reader.Mode == ReaderMode.OnAction)
        {
            SetProblem(_actionProblems, reader.Id, DeviceMessages.TimedOut); // the rule's time for an action ran out
            throw;
        }
        catch (Exception ex) when (!_stopped && reader.Mode == ReaderMode.OnAction && ex is not OperationCanceledException && ex.Message is not (OcrInfo.AlreadyReading or ReaderRules.Gone))
        {
            SetProblem(_actionProblems, reader.Id, ex.Message);
            throw;
        }
    }

    private async Task<ReaderReading> ReadAndFireAsync(Reader reader, CancellationToken callerCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerCt, _stopping.Token);
        var ct = linked.Token;
        if (_maker is null || _frames is null)
        {
            throw new InvalidOperationException(ReaderRules.NotInstalled);
        }

        if (reader.Areas.Count == 0)
        {
            throw new InvalidOperationException(ReaderRules.NoAreas); // text is read only inside areas
        }

        if ((await CheckAsync(reader, ct).ConfigureAwait(false)).Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        await _engineStart.WaitAsync(ct).ConfigureAwait(false);
        var asked = _time.GetUtcNow();
        var slot = SlotOf(reader.Id);
        while (true)
        {
            if (WatchingLoopOf(reader.Id) is not null && _last.TryGetValue(reader.Id, out var recent)
                && recent.Reading.Time >= asked - FreshReading && SamePicture(reader, recent.Reader))
            {
                // The watching loop read the camera just now: its (voted) reading answers, every area's event again.
                AddReads(reader, recent);
                FireAll(reader, recent);
                return recent;
            }

            var exited = slot.Exited;
            if (slot.TryEnter())
            {
                break;
            }

            if (WatchingLoopOf(reader.Id) is null)
            {
                throw new InvalidOperationException(OcrInfo.AlreadyReading);
            }

            // The watching loop reads a frame now: its reading answers once it ends.
            await exited.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var jpeg = await _frames.SingleAsync(FrameRequest.Of(reader), ct).ConfigureAwait(false);
            var reading = await _maker.ReadAsync(reader, jpeg, reuseSource: null, ct).ConfigureAwait(false); // the action: every area read
            if (StoredOf(reader.Id) is not { } stored)
            {
                throw new InvalidOperationException(ReaderRules.Gone); // deleted while it read: no event, no reading kept
            }

            if (SamePicture(stored, reader))
            {
                AddReads(reader, reading);
                Keep(reader, reading);
            }

            FireAll(reader, reading);
            return reading;
        }
        finally
        {
            slot.Exit();
        }
    }

    /// <summary>Every area's event of <paramref name="reading"/>.</summary>
    private void FireAll(Reader reader, ReaderReading reading)
    {
        for (var i = 0; i < reading.Reading.Areas.Count && i < reading.Details.Count; i++)
        {
            Fire(reader, reading.Reading.Areas[i], reading.Details[i], hasText: reading.Reading.Areas[i] is { } area && (area.Text.Length > 0 || area.TextRead.Length > 0));
        }
    }

    /// <summary>Stops every loop and waits for them; no loop starts afterwards.</summary>
    public async Task StopAsync()
    {
        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _stopped = true;
            await _stopping.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(_singles.Keys).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The cancelled single readings end with their own errors; their callers see them.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            await _recheck.DisposeAsync().ConfigureAwait(false);
            await _sweep.DisposeAsync().ConfigureAwait(false);
            await _statsTimer.DisposeAsync().ConfigureAwait(false);
            await StopLoopsAsync(_ => true).ConfigureAwait(false);
            await EndPreviewsAsync(_ => true).ConfigureAwait(false);
            await _cutWork.ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        switch (_recognizer)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }

        // _sync and _stopping stay: a late timer or event callback may still take them, and they hold no native handle.
    }

    /// <summary>
    /// Why the reader cannot read now, or null with its camera: <see cref="ReaderRules.Off"/> when the camera is not in its
    /// owner's devices (a deleted group, a removed camera), the device's refusal when OADM contacts it not (certificate
    /// changed, credentials required, site offline).
    /// </summary>
    public async Task<(string? Problem, IDeviceInfo? Camera)> CheckAsync(Reader reader, CancellationToken ct)
    {
        var owner = OwnerScope.Of(reader.OwnerScopeKey);
        if (owner != ICallContext.AllDevicesScopeKey
            && (_ctx.DeviceScopes is not { } scopes || !scopes.Exists(owner) || !scopes.Contains(owner, reader.DeviceId)))
        {
            return (ReaderRules.Off, null);
        }

        if (await _ctx.System.Devices.FindAsync(reader.DeviceId, ct).ConfigureAwait(false) is not { } camera)
        {
            return (ReaderRules.Off, null);
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

            var readers = _readers();
            var limit = MaxWatching;
            var problems = new Dictionary<string, string>(StringComparer.Ordinal);
            var wanted = new Dictionary<string, (Reader Reader, IDeviceInfo? Camera)>(StringComparer.Ordinal);
            var perSite = new Dictionary<Guid, int>();
            foreach (var stored in readers)
            {
                var (problem, camera) = await CheckAsync(stored, CancellationToken.None).ConfigureAwait(false);
                if (problem is not null)
                {
                    problems[stored.Id] = problem; // a refusing camera is not contacted; the next check starts it again
                    continue;
                }

                if (stored.Mode != ReaderMode.Watching || stored.Areas.Count == 0)
                {
                    continue;
                }

                if (_maker is null || _frames is null)
                {
                    problems[stored.Id] = ReaderRules.NotInstalled; // it would read nothing: the chip and the rules say so
                    continue;
                }

                // Beyond a limit (another server with fewer cores, a camera moved to a site) the reader says why it reads nothing.
                if (wanted.Count >= limit)
                {
                    problems[stored.Id] = ReaderRules.TooManyWatching(limit);
                    continue;
                }

                if (camera?.SiteId is { } site)
                {
                    if (perSite.GetValueOrDefault(site) >= ReaderRules.MaxWatchingPerSite)
                    {
                        problems[stored.Id] = ReaderRules.TooManyWatchingAtSite(camera.SiteName);
                        continue;
                    }

                    perSite[site] = perSite.GetValueOrDefault(site) + 1;
                }

                // A frame rate no choice (a record of an older version) reads at the highest one below it.
                wanted[stored.Id] = (ReaderRules.WithOfferedFps(stored), camera);
            }

            // Nothing starts before the graphics card's test ended: a reader starts where it stays.
            if (Starting)
            {
                foreach (var waiting in wanted.Keys)
                {
                    problems[waiting] = ReaderRules.Starting;
                }

                wanted.Clear();
            }

            // Only an On action reader keeps the result of its last action.
            var actionGone = false;
            foreach (var id in _actionProblems.Keys)
            {
                if (!readers.Any(r => r.Id == id && r.Mode == ReaderMode.OnAction))
                {
                    actionGone |= _actionProblems.TryRemove(id, out _);
                }
            }

            var before = _problems;
            _problems = problems;
            if (actionGone || before.Count != problems.Count || problems.Any(p => before.GetValueOrDefault(p.Key) != p.Value))
            {
                RaiseProblemsChanged();
            }

            await StopLoopsAsync(loop => !wanted.TryGetValue(loop.Reader.Id, out var now) || !SameLoop(now.Reader, loop.Reader)).ConfigureAwait(false);
            lock (_loops)
            {
                foreach (var (reader, camera) in wanted.Values)
                {
                    if (_loops.TryGetValue(reader.Id, out var running))
                    {
                        running.Reader = reader; // a new name, new area names: the loop goes on with them
                        continue;
                    }

                    var stop = new CancellationTokenSource();
                    _stats.ReaderStarted(reader.Id);
                    var loop = new WatchLoop(reader, stop)
                    {
                        Site = camera?.SiteId,
                        SiteName = camera?.SiteName,
                    };
                    loop.Run = Task.Run(() => WatchAsync(loop, stop.Token), CancellationToken.None);
                    _loops[reader.Id] = loop;
                }
            }

            // The previews follow: one may now share a reader's stream, or need its own again; the saved readers come first.
            await ReconcilePreviewsAsync().ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    private void RaiseProblemsChanged()
    {
        try
        {
            ProblemsChanged?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // Telling the pages never stops the readers.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBackgroundFailed(_logger, "telling the pages of the readers' problems", ex);
        }
    }

    /// <summary>The watching readers' loops now per site (the previews' own streams count against what is left).</summary>
    private Dictionary<Guid, int> LoopsPerSite()
    {
        lock (_loops)
        {
            return _loops.Values.Where(l => l.Site is not null).GroupBy(l => l.Site!.Value).ToDictionary(g => g.Key, g => g.Count());
        }
    }

    private async Task StopLoopsAsync(Func<WatchLoop, bool> which)
    {
        List<WatchLoop> stopping;
        lock (_loops)
        {
            stopping = [.. _loops.Values.Where(which)];
            foreach (var loop in stopping)
            {
                _loops.Remove(loop.Reader.Id);
                _stats.ReaderStopped(loop.Reader.Id);
            }
        }

        foreach (var loop in stopping)
        {
            await loop.Stop.CancelAsync().ConfigureAwait(false);
        }

        var cleared = false;
        foreach (var loop in stopping)
        {
            await loop.Run.ConfigureAwait(false);
            loop.Stop.Dispose();
            cleared |= _streamProblems.TryRemove(loop.Reader.Id, out _);
            cleared |= _readingProblems.TryRemove(loop.Reader.Id, out _);
        }

        if (cleared)
        {
            RaiseProblemsChanged();
        }
    }

    /// <summary>The loop of a watching reader; null when it has none.</summary>
    private WatchLoop? WatchingLoopOf(string readerId)
    {
        lock (_loops)
        {
            return _loops.GetValueOrDefault(readerId);
        }
    }

    /// <summary>A saved watching reader: votes per area and publishes its events (<see cref="LoopAsync"/>).</summary>
    private Task WatchAsync(WatchLoop loop, CancellationToken ct)
    {
        var id = loop.Reader.Id;
        return LoopAsync(loop.Reader, () => loop.Reader, vote: true, source: id, (r, reading) =>
        {
            _stats.CountReading(r.Id);
            Keep(r, reading);
        }, problem => SetProblem(_streamProblems, id, problem), SlotOf(id), ct, problem => SetProblem(_readingProblems, id, problem));
    }

    /// <summary>A reader's stream or action fails (<paramref name="problem"/>) or works again (null): the list and the event rules say so.</summary>
    private void SetProblem(ConcurrentDictionary<string, string> problems, string readerId, string? problem)
    {
        var changed = problem is null
            ? problems.TryRemove(readerId, out _)
            : !string.Equals(problems.GetValueOrDefault(readerId), problem, StringComparison.Ordinal) && (problems[readerId] = problem) is not null;
        if (changed && !_stopped)
        {
            RaiseProblemsChanged();
        }
    }

    /// <summary>
    /// One stream of a camera: a pump takes the frames into a slot of one (the newest replaces an unread one), the loop reads the
    /// slot's frame with the reader <paramref name="current"/> gives at that moment (a preview takes the page's new areas without
    /// opening the stream again). One frame is in work at a time; a further one starts beside it only while the recognition has
    /// room (<see cref="ReadingMaker.HasRoom"/>, at most <see cref="OcrLimits.MaxFramesInWork"/>), so an idle server carries one
    /// reader at full speed and busy workers fall back to one frame per reader. The results are taken in the order of the frames,
    /// so an older frame's result never replaces a newer one and the vote sees the frames in order. Frames that find no room
    /// are dropped, newest wins, never queued, so the lag stays bounded. With <paramref name="vote"/> (a saved watching reader)
    /// the areas vote and publish their events; without (a preview) every frame's own result is kept and nothing is published
    /// to the rules, or with Moving objects the same 2 frames rule as the reader's, still without an event. A frame that arrives while the action reads the same reader (<paramref name="slot"/>) is dropped. Areas whose
    /// pixels have not changed since the last reading of <paramref name="source"/> give that reading again (docs/spec/ocr.md
    /// "Unchanged areas"). <paramref name="problem"/> hears why the stream gives no pictures, <paramref name="failing"/> when
    /// <see cref="OcrLimits.FailedReadingsForProblem"/> readings in a row failed (<see cref="ReaderRules.ReadingFailed"/>) and when one works again.
    /// </summary>
    private async Task LoopAsync(Reader stream, Func<Reader> current, bool vote, string source, Action<Reader, ReaderReading> keep, Action<string?> problem, ReaderSlot? slot, CancellationToken ct, Action<string?>? failing = null)
    {
        var failed = 0;
        var watches = vote ? WatchesOf(stream) : null;
        (Reader Reader, AreaWatch[] Watches)? moving = null; // a preview's draft can change Moving objects and its areas under a running stream
        var latest = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        var pump = Task.Run(() => PumpAsync(stream, latest.Writer, problem, ct), CancellationToken.None);
        var inWork = new Queue<Task<(Reader Reader, ReaderReading? Read)>>();

        void Deliver((Reader Reader, ReaderReading? Read) done)
        {
            try
            {
                if (done.Read is null)
                {
                    if (!ct.IsCancellationRequested && ++failed == OcrLimits.FailedReadingsForProblem)
                    {
                        failing?.Invoke(ReaderRules.ReadingFailed);
                    }

                    return;
                }

                if (failed > 0)
                {
                    failed = 0;
                    failing?.Invoke(null);
                }

                if (watches is null)
                {
                    if (!ReaderRules.IsMoving(done.Reader))
                    {
                        moving = null;
                        keep(done.Reader, done.Read);
                        return;
                    }

                    if (moving is not { } now || !SamePicture(now.Reader, done.Reader))
                    {
                        moving = (done.Reader, WatchesOf(done.Reader));
                    }

                    keep(done.Reader, Decide(done.Reader, done.Read, moving.Value.Watches, publish: false));
                }
                else if (IsCurrent(done.Reader))
                {
                    // A reader deleted or changed while the frame was read publishes nothing (the sync stops its loop).
                    keep(done.Reader, Decide(done.Reader, done.Read, watches, publish: true));
                }
            }
#pragma warning disable CA1031 // A failed reading is logged; the reader goes on with the next frame.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogReadingFailed(_logger, done.Reader.Name, ex);
            }
            finally
            {
                slot?.ExitFrame();
            }
        }

        try
        {
            var arrived = latest.Reader.WaitToReadAsync(ct).AsTask();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                while (inWork.TryPeek(out var head) && head.IsCompleted)
                {
                    Deliver(await inWork.Dequeue().ConfigureAwait(false));
                }

                if (arrived.IsCompleted)
                {
                    if (!await arrived.ConfigureAwait(false))
                    {
                        break;
                    }

                    if (inWork.Count == 0 || (inWork.Count < OcrLimits.MaxFramesInWork && _maker!.HasRoom))
                    {
                        if (latest.Reader.TryRead(out var frame))
                        {
                            if (slot is not null && !slot.TryEnterFrame())
                            {
                                Interlocked.Increment(ref _dropped);
                            }
                            else
                            {
                                inWork.Enqueue(ReadFrameAsync(current(), frame, source, ct));
                            }
                        }

                        arrived = latest.Reader.WaitToReadAsync(ct).AsTask();
                        continue;
                    }

                    // No room: the frame waits in the slot of one, a newer one replaces it. Asked again at the next frame or
                    // when the oldest frame ends.
                    await Task.WhenAny(inWork.Peek(), Task.Delay(TimeSpan.FromSeconds(1.0 / Math.Max(1, stream.Fps)), _time, ct)).ConfigureAwait(false);
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
            // Every frame in work ends before the loop does: the recognizer is never freed under one.
            while (inWork.TryDequeue(out var left))
            {
                var done = await left.ConfigureAwait(false);
                Deliver(ct.IsCancellationRequested ? (done.Reader, null) : done);
            }

            await pump.ConfigureAwait(false);
        }
    }

    /// <summary>One frame's reading; a failure is logged and gives no reading.</summary>
    private async Task<(Reader Reader, ReaderReading? Read)> ReadFrameAsync(Reader reader, ReadOnlyMemory<byte> frame, string source, CancellationToken ct)
    {
        try
        {
            return (reader, await _maker!.ReadAsync(reader, frame, source, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (reader, null);
        }
#pragma warning disable CA1031 // A failed reading is logged; the reader goes on with the next frame.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReadingFailed(_logger, reader.Name, ex);
            return (reader, null);
        }
    }

    private async Task PumpAsync(Reader reader, ChannelWriter<ReadOnlyMemory<byte>> writer, Action<string?> problem, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _frames!.WatchAsync(FrameRequest.Of(reader), reader.Fps, problem, ct).ConfigureAwait(false))
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
            LogStreamFailed(_logger, reader.Name, ex);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary>One watch per area of <paramref name="reader"/>: the vote, or the 2 frames rule with Moving objects.</summary>
    private static AreaWatch[] WatchesOf(Reader reader)
    {
        var moving = ReaderRules.IsMoving(reader);
        return [.. reader.Areas.Select(a => new AreaWatch(a.Expected, moving))];
    }

    /// <summary>
    /// Each area's watch over the frame's result. With <paramref name="publish"/> (a saved reader) the areas whose value changed
    /// add their row and publish their event, both from the frame the value was read in; a preview only shows the values.
    /// </summary>
    private ReaderReading Decide(Reader reader, ReaderReading read, AreaWatch[] watches, bool publish)
    {
        var areas = new AreaResult[watches.Length];
        var details = new AreaDetails[watches.Length];
        var decisions = new AreaDecision[watches.Length];
        var sources = new ValueFrame[watches.Length];
        var elsewhere = false;
        for (var i = 0; i < watches.Length; i++)
        {
            var frameResult = i < read.Reading.Areas.Count ? read.Reading.Areas[i] : new AreaResult { Area = i < reader.Areas.Count ? reader.Areas[i].Name : string.Empty };
            var frameDetails = i < read.Details.Count ? read.Details[i] : new AreaDetails { Area = frameResult.Area };
            decisions[i] = watches[i].Add(frameResult, new ValueFrame(read.Jpeg, frameDetails.MatchBox, frameDetails.MatchTurn));
            areas[i] = decisions[i].Voted;
            if (string.Equals(areas[i].Text, frameResult.Text, StringComparison.Ordinal))
            {
                details[i] = frameDetails;
            }
            else
            {
                // A value this frame does not hold has no place in it: no box, its picture comes from its own frame.
                details[i] = ReadingMaker.DetailsOf(areas[i], areas[i].MatchesFormat && frameDetails.AllMatches.Length > 0 ? [areas[i].Text] : [], null) with { TooSmall = frameDetails.TooSmall, SmallText = frameDetails.SmallText };
                sources[i] = decisions[i].Seen;
                elsewhere = true;
            }
        }

        for (var i = 0; publish && i < watches.Length; i++)
        {
            if (decisions[i].Publish)
            {
                // The same moment as its event.
                AddRead(ReadsOf(reader.Id), reader, i, areas[i], decisions[i].Seen, read.Reading.Time);
                Fire(reader, areas[i], details[i], hasText: areas[i].Text.Length > 0); // a value gone, whatever raw text this frame holds
            }
        }

        // Kept afterwards: the page's event tells that the reading is complete, its rule events included.
        return read with { Reading = read.Reading with { Areas = areas }, Details = details, Sources = elsewhere ? sources : null };
    }

    /// <summary>The stored reader of <paramref name="readerId"/>; null after a delete.</summary>
    private Reader? StoredOf(string readerId)
    {
        foreach (var reader in _readers())
        {
            if (reader.Id == readerId)
            {
                return reader;
            }
        }

        return null;
    }

    /// <summary>The reader is still stored with the same camera, source, resolution and areas (a reading of it still fits).</summary>
    private bool IsCurrent(Reader reader) => StoredOf(reader.Id) is { } stored && SamePicture(stored, reader);

    /// <summary>
    /// Keeps the reading as the reader's last and tells the pages that see the reader; the previews that share the reader's
    /// stream get it as theirs.
    /// </summary>
    private void Keep(Reader reader, ReaderReading reading)
    {
        reading = reading with { Reader = reader };
        _last[reader.Id] = reading;
        foreach (var preview in SharingPreviews(reader.Id))
        {
            KeepPreview(preview, reading);
        }

        try
        {
            if (_ctx.Events is not { } events || !ShouldPublish(reader.Id, reading.Reading))
            {
                return;
            }

            var json = OcrJson.Serialize(new ReadingEvent
            {
                Reading = reading.Reading,
                Details = [.. reading.Details.Select(d => d with { Cutout = null })],
            });
            events.Publish(OcrMethods.ReadingTopic, json, PluginEventAudience.AllDevices);
            var owner = OwnerScope.Of(reader.OwnerScopeKey);
            if (owner != ICallContext.AllDevicesScopeKey)
            {
                events.Publish(OcrMethods.ReadingTopic, json, PluginEventAudience.DeviceScope(owner));
            }
        }
        finally
        {
            Interlocked.Increment(ref _kept);
        }
    }

    /// <summary>The readings kept so far, published or not (tests wait on it).</summary>
    public int KeptReadings => Volatile.Read(ref _kept);

    /// <summary>How often a reader's unchanged reading reaches the pages at most.</summary>
    public static readonly TimeSpan UnchangedReadingInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The page hears of a reading when its values changed, else at most every <see cref="UnchangedReadingInterval"/> per
    /// reader (15 frames per second of 50 readers never flood the open pages).
    /// </summary>
    private bool ShouldPublish(string readerId, Reading reading)
    {
        var signature = string.Join('\u001f', reading.Areas.Select(a => a.Area + "\u001e" + a.Text)) + "\u001d" + reading.Note;
        var now = _time.GetUtcNow();
        if (_published.TryGetValue(readerId, out var last) && last.Signature == signature && now - last.At < UnchangedReadingInterval)
        {
            return false;
        }

        _published[readerId] = (signature, now);
        return true;
    }

    /// <summary>
    /// The reply of a reading for the page, with the values' cut-outs when <paramref name="withCutouts"/> (the caller may see
    /// live video): cut once per reading, when a page first asks (never per frame), from the reading's picture. Never the
    /// picture itself: the page shows the camera's live video.
    /// </summary>
    public ReadingReply ReplyOf(ReaderReading reading, bool withCutouts = true)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (!withCutouts)
        {
            return new ReadingReply { Reading = reading.Reading, Details = [.. reading.Details.Select(d => d with { Cutout = null })] };
        }

        var details = _cutouts.GetValue(reading, r => _cutter is null
            ? r.Details
            : [.. r.Details.Select(d => d.MatchBox is { } box && d.Cutout is null ? d with { Cutout = _cutter.Cut(r.Jpeg, box, d.MatchTurn) } : d)]);
        return new ReadingReply { Reading = reading.Reading, Details = details };
    }

    /// <summary>
    /// The event of one area. No text is the event No text read, never an empty Text read: that would run the rules on real
    /// reads and start their dead time, so the real read right after it would be held back. <paramref name="hasText"/>: a
    /// watching area has a value; the action also counts text that did not match the format, with the raw text in Text read.
    /// </summary>
    private void Fire(Reader reader, AreaResult area, AreaDetails details, bool hasText)
    {
        if (hasText)
        {
            _ctx.EventPublisher?.Fire(OcrInfo.TextReadTypeId(reader.Id), reader.DeviceId, TextReadEvents.Data(area, details));
        }
        else
        {
            _ctx.EventPublisher?.Fire(OcrInfo.NoTextTypeId(reader.Id), reader.DeviceId, TextReadEvents.NoTextData(area));
        }
    }

    private ReaderSlot SlotOf(string readerId) => _slots.GetOrAdd(readerId, _ => new ReaderSlot());

    /// <summary>
    /// A reading of <paramref name="a"/> fits <paramref name="b"/>: the same camera, video source, resolution and areas (place
    /// and expected text; names aside).
    /// </summary>
    internal static bool SamePicture(Reader a, Reader? b) =>
        b is not null
        && a.DeviceId == b.DeviceId && a.VideoSource == b.VideoSource && a.Resolution == b.Resolution
        && a.Areas.Count == b.Areas.Count
        && a.Areas.Zip(b.Areas).All(p => p.First.Box == p.Second.Box && p.First.Expected == p.Second.Expected);

    /// <summary>
    /// A loop of <paramref name="before"/> can go on for <paramref name="now"/>: the same picture (<see cref="SamePicture"/>),
    /// frame rate, owner and Moving objects. A new name or new area names never restart it (the vote and the published values stay).
    /// </summary>
    private static bool SameLoop(Reader now, Reader before) =>
        SamePicture(now, before) && now.Fps == before.Fps && ReaderRules.IsMoving(now) == ReaderRules.IsMoving(before)
        && string.Equals(now.OwnerScopeKey, before.OwnerScopeKey, StringComparison.OrdinalIgnoreCase);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Text recognition: {What} failed")]
    private static partial void LogBackgroundFailed(ILogger logger, string what, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Text recognition: a reading of the reader {Reader} failed")]
    private static partial void LogReadingFailed(ILogger logger, string reader, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Text recognition: the pictures of the reader {Reader} ended")]
    private static partial void LogStreamFailed(ILogger logger, string reader, Exception ex);

    /// <summary>A camera stream of a saved watching reader or a preview. The reader may change while it runs (names only).</summary>
    private sealed class WatchLoop(Reader reader, CancellationTokenSource stop)
    {
        private volatile Reader _reader = reader;

        public Reader Reader
        {
            get => _reader;
            set => _reader = value;
        }

        public CancellationTokenSource Stop { get; } = stop;

        public Task Run { get; set; } = Task.CompletedTask;

        /// <summary>The camera's site; null on the server's own network.</summary>
        public Guid? Site { get; init; }

        public string? SiteName { get; init; }
    }

    /// <summary>
    /// The readings of one reader: the watching loop's frames (several at once when there is room) or one single reading, never
    /// both; <see cref="Exited"/> ends when a reading in work ends.
    /// </summary>
    private sealed class ReaderSlot
    {
        private readonly object _lock = new();
        private int _frames;
        private bool _single;
        private TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Ends at the next exit (take it before <see cref="TryEnter"/>).</summary>
        public Task Exited => Volatile.Read(ref _exited).Task;

        /// <summary>A single reading: only while nothing is in work.</summary>
        public bool TryEnter()
        {
            lock (_lock)
            {
                if (_single || _frames > 0)
                {
                    return false;
                }

                _single = true;
                return true;
            }
        }

        /// <summary>A frame of the watching loop: beside other frames, never beside a single reading.</summary>
        public bool TryEnterFrame()
        {
            lock (_lock)
            {
                if (_single)
                {
                    return false;
                }

                _frames++;
                return true;
            }
        }

        public void Exit()
        {
            lock (_lock)
            {
                _single = false;
            }

            Tell();
        }

        public void ExitFrame()
        {
            lock (_lock)
            {
                _frames--;
            }

            Tell();
        }

        private void Tell() =>
            Interlocked.Exchange(ref _exited, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }
}
