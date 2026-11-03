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
/// <item>one loop per watching reader on <see cref="IFrameSource.WatchAsync"/>: the newest frame wins (older ones are
/// dropped, never queued), one reading at a time per reader, then per area the vote and the event only on change
/// (<see cref="AreaWatch"/>);</item>
/// <item>single readings (the action Read text, the page's Read now) through <see cref="IFrameSource.SingleAsync"/> that
/// publish every area's event; a reading already running for the reader refuses with <see cref="OcrInfo.AlreadyReading"/>;</item>
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
    private readonly Func<Area, IFrameVote> _vote;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, WatchLoop> _loops = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReaderSlot> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReaderReading> _last = new(StringComparer.Ordinal);
    private readonly ITimer _recheck;
    private volatile Dictionary<string, string> _problems = [];
    private volatile IReadOnlyList<Reader> _readers = [];
    private bool _stopped;
    private int _dropped;

    /// <summary>Frames of watching readers dropped because a single reading of the same reader ran (tests).</summary>
    public int DroppedWhileBusy => Volatile.Read(ref _dropped);

    public ReaderRuntime(ICorePluginContext ctx, ReaderParts parts, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(parts);
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _frames = parts.Frames?.Invoke(ctx);
        _recognizer = parts.Recognizer?.Invoke(ctx);
        _maker = _recognizer is not null && parts.Matcher is not null ? new ReadingMaker(_recognizer, parts.Matcher, parts.Cutter, parts.Time) : null;
        _vote = parts.Vote ?? (_ => new LatestFrameVote());
        using (ExecutionContext.SuppressFlow())
        {
            _recheck = parts.Time.CreateTimer(_ => _ = SyncAsync(_readers), null, RecheckInterval, RecheckInterval);
        }
    }

    /// <summary>How often the readers' cameras are checked again (a camera that refused connections starts when it is Ok).</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>Raised (without a caller) when the problems of the readers changed (<see cref="ProblemOf"/>).</summary>
    public event EventHandler? ProblemsChanged;

    /// <summary>True while the recognition runs on a graphics card (<see cref="IRecognizerCapabilities"/>).</summary>
    public bool OnGpu => _recognizer is IRecognizerCapabilities { OnGpu: true };

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

    /// <summary>Why the reader cannot read now (<see cref="CheckAsync"/>) as of the last check; null while it can.</summary>
    public string? ProblemOf(string readerId) => _problems.GetValueOrDefault(readerId);

    /// <summary>The last reading of a reader; null without one.</summary>
    public ReaderReading? Last(string readerId) => _last.TryGetValue(readerId, out var reading) ? reading : null;

    /// <summary>Drops the last reading of a reader (its camera or areas changed).</summary>
    public void Forget(string readerId) => _last.TryRemove(readerId, out _);

    /// <summary>Drops everything of a deleted reader (after <see cref="SyncAsync"/> stopped its loop).</summary>
    public void Remove(string readerId)
    {
        _last.TryRemove(readerId, out _);
        _slots.TryRemove(readerId, out _);
    }

    /// <summary>
    /// Brings the loops in line with <paramref name="readers"/>: a loop for every watching reader that may read (at most
    /// <see cref="ReaderRules.MaxWatching"/>), changed readers start again, the others stop. Runs without the caller.
    /// </summary>
    public Task SyncAsync(IReadOnlyList<Reader> readers) => Detached(() => SyncCoreAsync(readers));

    /// <summary>
    /// Reads one picture of <paramref name="reader"/> now and publishes every area's event, changed or not. Refuses with
    /// <see cref="OcrInfo.AlreadyReading"/> while a reading of the reader runs (nothing is queued) and with
    /// <see cref="ReaderRules.Off"/> when its camera is not in its owner's devices. Runs without the caller.
    /// </summary>
    public Task<ReaderReading> ReadOnceAsync(Reader reader, CancellationToken ct) => Detached(async () =>
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (_maker is null || _frames is null)
        {
            throw new InvalidOperationException(ReaderRules.NotInstalled);
        }

        if ((await CheckAsync(reader, ct).ConfigureAwait(false)).Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        var slot = SlotOf(reader.Id);
        if (!slot.TryEnter())
        {
            throw new InvalidOperationException(OcrInfo.AlreadyReading);
        }

        try
        {
            var jpeg = await _frames.SingleAsync(FrameRequest.Of(reader), ct).ConfigureAwait(false);
            var reading = await _maker.ReadAsync(reader, jpeg, ct).ConfigureAwait(false);
            Keep(reader, reading);
            for (var i = 0; i < reading.Reading.Areas.Count; i++)
            {
                Fire(reader, reading.Reading.Areas[i], reading.Details[i]);
            }

            return reading;
        }
        finally
        {
            slot.Exit();
        }
    });

    /// <summary>Stops every loop and waits for them; no loop starts afterwards.</summary>
    public async Task StopAsync()
    {
        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _stopped = true;
            await _recheck.DisposeAsync().ConfigureAwait(false);
            await StopLoopsAsync(_ => true).ConfigureAwait(false);
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

        _sync.Dispose();
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

    private async Task SyncCoreAsync(IReadOnlyList<Reader> readers)
    {
        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            _readers = readers;
            var problems = new Dictionary<string, string>(StringComparer.Ordinal);
            var wanted = new Dictionary<string, Reader>(StringComparer.Ordinal);
            var perSite = new Dictionary<Guid, int>();
            var limit = ReaderRules.MaxWatching(OnGpu);
            foreach (var reader in readers)
            {
                var (problem, camera) = await CheckAsync(reader, CancellationToken.None).ConfigureAwait(false);
                if (problem is not null)
                {
                    problems[reader.Id] = problem; // a refusing camera is not contacted; the next check starts it again
                }
                else if (reader.Mode == ReaderMode.Watching && _maker is not null && _frames is not null && wanted.Count < limit
                    && (camera?.SiteId is not { } site || (perSite[site] = perSite.GetValueOrDefault(site) + 1) <= ReaderRules.MaxWatchingPerSite))
                {
                    wanted[reader.Id] = reader;
                }
            }

            var before = _problems;
            _problems = problems;
            if (before.Count != problems.Count || problems.Any(p => before.GetValueOrDefault(p.Key) != p.Value))
            {
                ProblemsChanged?.Invoke(this, EventArgs.Empty);
            }
            await StopLoopsAsync(loop => !wanted.TryGetValue(loop.Reader.Id, out var now) || !Same(now, loop.Reader)).ConfigureAwait(false);
            lock (_loops)
            {
                foreach (var reader in wanted.Values.Where(r => !_loops.ContainsKey(r.Id)))
                {
                    var stop = new CancellationTokenSource();
                    _loops[reader.Id] = new WatchLoop(reader, stop, Task.Run(() => WatchAsync(reader, stop.Token), CancellationToken.None));
                }
            }
        }
        finally
        {
            _sync.Release();
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
            }
        }

        foreach (var loop in stopping)
        {
            await loop.Stop.CancelAsync().ConfigureAwait(false);
        }

        foreach (var loop in stopping)
        {
            await loop.Run.ConfigureAwait(false);
            loop.Stop.Dispose();
        }
    }

    /// <summary>
    /// One watching reader: a pump takes the camera's frames into a slot of one (the newest replaces an unread one), the
    /// loop reads the slot's frame, one at a time. A frame that arrives while the action reads the same reader is dropped.
    /// </summary>
    private async Task WatchAsync(Reader reader, CancellationToken ct)
    {
        var watches = ReaderRules.EffectiveAreas(reader).Select(a => new AreaWatch(_vote(a))).ToArray();
        var latest = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        var pump = Task.Run(() => PumpAsync(reader, latest.Writer, ct), CancellationToken.None);
        var slot = SlotOf(reader.Id);
        try
        {
            await foreach (var frame in latest.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!slot.TryEnter())
                {
                    Interlocked.Increment(ref _dropped);
                    continue;
                }

                try
                {
                    await ReadFrameAsync(reader, frame, watches, ct).ConfigureAwait(false);
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
                    slot.Exit();
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

    private async Task PumpAsync(Reader reader, ChannelWriter<ReadOnlyMemory<byte>> writer, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _frames!.WatchAsync(FrameRequest.Of(reader), reader.Fps, ct).ConfigureAwait(false))
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

    private async Task ReadFrameAsync(Reader reader, ReadOnlyMemory<byte> frame, AreaWatch[] watches, CancellationToken ct)
    {
        var read = await _maker!.ReadAsync(reader, frame, ct).ConfigureAwait(false);
        var areas = new AreaResult[watches.Length];
        var details = new AreaDetails[watches.Length];
        var publish = new bool[watches.Length];
        for (var i = 0; i < watches.Length; i++)
        {
            var frameResult = i < read.Reading.Areas.Count ? read.Reading.Areas[i] : new AreaResult { Area = ReaderRules.EffectiveAreas(reader)[i].Name };
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

        // Last: the page's event tells that the reading is complete, its rule events included.
        Keep(reader, read with { Reading = read.Reading with { Areas = areas }, Details = details });
    }

    /// <summary>Keeps the reading as the reader's last and tells the pages that see the reader.</summary>
    private void Keep(Reader reader, ReaderReading reading)
    {
        _last[reader.Id] = reading;
        if (_ctx.Events is not { } events)
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

    private void Fire(Reader reader, AreaResult area, AreaDetails details) =>
        _ctx.EventPublisher?.Fire(OcrInfo.TextReadTypeId(reader.Id), reader.DeviceId, TextReadEvents.Data(area, details));

    private ReaderSlot SlotOf(string readerId) => _slots.GetOrAdd(readerId, _ => new ReaderSlot());

    /// <summary>Whether a loop of <paramref name="before"/> can go on for <paramref name="now"/> (the same reader in every detail).</summary>
    private static bool Same(Reader now, Reader before) => OcrJson.Serialize(now) == OcrJson.Serialize(before);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Text recognition: a reading of the reader {Reader} failed")]
    private static partial void LogReadingFailed(ILogger logger, string reader, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Text recognition: the pictures of the reader {Reader} ended")]
    private static partial void LogStreamFailed(ILogger logger, string reader, Exception ex);

    private sealed record WatchLoop(Reader Reader, CancellationTokenSource Stop, Task Run);

    /// <summary>One reading at a time per reader.</summary>
    private sealed class ReaderSlot
    {
        private int _busy;

        public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

        public void Exit() => Volatile.Write(ref _busy, 0);
    }
}
