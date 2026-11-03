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
/// the newest frame wins (older ones are dropped, never queued), one reading at a time per reader, then per area the vote and
/// the event only on change (<see cref="AreaWatch"/>);</item>
/// <item>single readings (the action Read text) through <see cref="IFrameSource.SingleAsync"/> that publish every area's
/// event; a watching reader answers with its fresh reading instead (<see cref="ReadOnceAsync"/>);</item>
/// <item>the last reading of each reader (picture, results, cut-outs) in memory only.</item>
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
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ReaderReading, IReadOnlyList<AreaDetails>> _cutouts = new();
    private readonly ConcurrentDictionary<string, (string Signature, DateTimeOffset At)> _published = new(StringComparer.Ordinal);
    private int _kept;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, WatchLoop> _loops = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReaderSlot> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReaderReading> _last = new(StringComparer.Ordinal);
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
    }

    /// <summary>How often the readers' cameras are checked again (a camera that refused connections starts when it is Ok).</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>Raised (without a caller) when the problems of the readers changed (<see cref="ProblemOf"/>).</summary>
    public event EventHandler? ProblemsChanged;

    /// <summary>Watching readers at most on this server (its cores, <see cref="OcrLimits.MaxWatching"/>), previews' own streams included.</summary>
    public int MaxWatching { get; }

    /// <summary>True while the recognition uses a graphics card (<see cref="IEngineInfo.OnGpu"/>).</summary>
    public bool OnGpu => _recognizer is IEngineInfo { OnGpu: true };

    /// <summary>What the recognition runs on and "Use the graphics card"; null while the recognizer does not tell.</summary>
    public IEngineInfo? Engine => _recognizer as IEngineInfo;

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
    /// stream gives no pictures; null while it reads.
    /// </summary>
    public string? ProblemOf(string readerId) => _problems.GetValueOrDefault(readerId) ?? _streamProblems.GetValueOrDefault(readerId);

    /// <summary>The last reading of a reader; null without one.</summary>
    public ReaderReading? Last(string readerId) => _last.TryGetValue(readerId, out var reading) ? reading : null;

    /// <summary>Drops the last reading of a reader (its camera or areas changed).</summary>
    public void Forget(string readerId) => _last.TryRemove(readerId, out _);

    /// <summary>Drops everything of a deleted reader (after <see cref="SyncAsync"/> stopped its loop).</summary>
    public void Remove(string readerId)
    {
        _published.TryRemove(readerId, out _);
        _last.TryRemove(readerId, out _);
        _slots.TryRemove(readerId, out _);
        _streamProblems.TryRemove(readerId, out _);
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
    /// Reads one picture of <paramref name="reader"/> now and publishes every area's event, changed or not. A watching reader
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

    private async Task<ReaderReading> ReadOnceCoreAsync(Reader reader, CancellationToken callerCt)
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

        var asked = _time.GetUtcNow();
        var slot = SlotOf(reader.Id);
        while (true)
        {
            if (WatchingLoopOf(reader.Id) is not null && _last.TryGetValue(reader.Id, out var recent)
                && recent.Reading.Time >= asked - FreshReading && SamePicture(reader, recent.Reader))
            {
                // The watching loop read the camera just now: its (voted) reading answers, every area's event again.
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
            Fire(reader, reading.Reading.Areas[i], reading.Details[i]);
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

                if (stored.Mode != ReaderMode.Watching || stored.Areas.Count == 0 || _maker is null || _frames is null)
                {
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

            var before = _problems;
            _problems = problems;
            if (before.Count != problems.Count || problems.Any(p => before.GetValueOrDefault(p.Key) != p.Value))
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
        }, problem => SetStreamProblem(id, problem), SlotOf(id), ct);
    }

    /// <summary>A watching reader's stream gives no pictures (<paramref name="problem"/>) or does again (null): the list says so.</summary>
    private void SetStreamProblem(string readerId, string? problem)
    {
        var changed = problem is null
            ? _streamProblems.TryRemove(readerId, out _)
            : !string.Equals(_streamProblems.GetValueOrDefault(readerId), problem, StringComparison.Ordinal) && (_streamProblems[readerId] = problem) is not null;
        if (changed && !_stopped)
        {
            RaiseProblemsChanged();
        }
    }

    /// <summary>
    /// One stream of a camera: a pump takes the frames into a slot of one (the newest replaces an unread one), the loop
    /// reads the slot's frame, one at a time, with the reader <paramref name="current"/> gives at that moment (a preview takes
    /// the page's new areas without opening the stream again). With <paramref name="vote"/> (a saved watching reader) the
    /// areas vote and publish their events; without (a preview) every frame's own result is kept and nothing is published to
    /// the rules. A frame that arrives while the action reads the same reader (<paramref name="slot"/>) is dropped. Areas whose
    /// pixels have not changed since the last reading of <paramref name="source"/> give that reading again (docs/spec/ocr.md
    /// "Unchanged areas"). <paramref name="problem"/> hears why the stream gives no pictures.
    /// </summary>
    private async Task LoopAsync(Reader stream, Func<Reader> current, bool vote, string source, Action<Reader, ReaderReading> keep, Action<string?> problem, ReaderSlot? slot, CancellationToken ct)
    {
        var watches = vote ? stream.Areas.Select(a => new AreaWatch(a.Expected)).ToArray() : null;
        var latest = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        var pump = Task.Run(() => PumpAsync(stream, latest.Writer, problem, ct), CancellationToken.None);
        try
        {
            await foreach (var frame in latest.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (slot is not null && !slot.TryEnter())
                {
                    Interlocked.Increment(ref _dropped);
                    continue;
                }

                var reader = current();
                try
                {
                    var read = await _maker!.ReadAsync(reader, frame, source, ct).ConfigureAwait(false);
                    if (watches is null)
                    {
                        keep(reader, read);
                    }
                    else if (IsCurrent(reader))
                    {
                        // A reader deleted or changed while the frame was read publishes nothing (the sync stops its loop).
                        keep(reader, Vote(reader, read, watches));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
#pragma warning disable CA1031 // A failed reading is logged; the reader goes on with the next frame.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogReadingFailed(_logger, reader.Name, ex);
                }
                finally
                {
                    slot?.Exit();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
        finally
        {
            await pump.ConfigureAwait(false);
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

    /// <summary>Each area's vote over the frame's result; the areas whose voted value changed publish their event.</summary>
    private ReaderReading Vote(Reader reader, ReaderReading read, AreaWatch[] watches)
    {
        var areas = new AreaResult[watches.Length];
        var details = new AreaDetails[watches.Length];
        var publish = new bool[watches.Length];
        for (var i = 0; i < watches.Length; i++)
        {
            var frameResult = i < read.Reading.Areas.Count ? read.Reading.Areas[i] : new AreaResult { Area = i < reader.Areas.Count ? reader.Areas[i].Name : string.Empty };
            var frameDetails = i < read.Details.Count ? read.Details[i] : new AreaDetails { Area = frameResult.Area };
            (areas[i], publish[i]) = watches[i].Add(frameResult);
            details[i] = string.Equals(areas[i].Text, frameResult.Text, StringComparison.Ordinal)
                ? frameDetails
                : ReadingMaker.DetailsOf(areas[i], areas[i].MatchesFormat && frameDetails.AllMatches.Length > 0 ? [areas[i].Text] : [], null) with { TooSmall = frameDetails.TooSmall };
        }

        for (var i = 0; i < watches.Length; i++)
        {
            if (publish[i])
            {
                Fire(reader, areas[i], details[i]);
            }
        }

        // Kept afterwards: the page's event tells that the reading is complete, its rule events included.
        return read with { Reading = read.Reading with { Areas = areas }, Details = details };
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
            : [.. r.Details.Select(d => d.MatchBox is { } box && d.Cutout is null ? d with { Cutout = _cutter.Cut(r.Jpeg, box) } : d)]);
        return new ReadingReply { Reading = reading.Reading, Details = details };
    }

    private void Fire(Reader reader, AreaResult area, AreaDetails details) =>
        _ctx.EventPublisher?.Fire(OcrInfo.TextReadTypeId(reader.Id), reader.DeviceId, TextReadEvents.Data(area, details));

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
    /// frame rate and owner. A new name or new area names never restart it (the vote and the published values stay).
    /// </summary>
    private static bool SameLoop(Reader now, Reader before) =>
        SamePicture(now, before) && now.Fps == before.Fps
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

    /// <summary>One reading at a time per reader; <see cref="Exited"/> ends when the reading in work ends.</summary>
    private sealed class ReaderSlot
    {
        private int _busy;
        private TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Ends at the next <see cref="Exit"/> (take it before <see cref="TryEnter"/>).</summary>
        public Task Exited => Volatile.Read(ref _exited).Task;

        public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

        public void Exit()
        {
            Volatile.Write(ref _busy, 0);
            Interlocked.Exchange(ref _exited, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }
    }
}
