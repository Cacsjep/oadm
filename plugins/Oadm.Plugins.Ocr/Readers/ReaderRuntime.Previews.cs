using System.Collections.Concurrent;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>
/// The page's previews (docs/spec/ocr.md "Page"): while a page shows a reader, the server reads it live, saved or not, at the
/// reader's rate (On action: 1 per second), and publishes no rule events. A preview of a saved watching reader that the page
/// did not change shares the reader's stream (its readings are the preview's); any other preview has its own stream through
/// the same loop with the vote and the events off (with Moving objects its values follow the 2 frames rule), and counts against the watching limits (also per site), after the saved
/// readers. At most <see cref="OcrLimits.MaxPreviews"/>, <see cref="OcrLimits.MaxPreviewsPerUser"/> per user; each belongs to
/// the user who started it and ends <see cref="OcrLimits.PreviewLease"/> after the page's last call, so a closed client never
/// leaves one running. Pause holds the frame of a preview's latest reading (<see cref="HeldFrame"/>, docs/spec/ocr.md "Pause"):
/// the stream stops or is no longer shared, the draft re-reads that frame, and a saved reader's loop never hears of it.
/// </summary>
internal sealed partial class ReaderRuntime
{
    /// <summary>How often ended leases are looked for.</summary>
    public static readonly TimeSpan PreviewSweepInterval = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, Preview> _previews = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReaderReading> _previewLast = new(StringComparer.Ordinal);
    private long _previewOrder;

    /// <summary>The previews now (tests): id, whether it has its own stream, the reader whose stream it shares.</summary>
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

    /// <summary>
    /// Starts a preview of <paramref name="reader"/> (checked by <see cref="ReaderRules.PreviewOf"/>) for <paramref name="user"/>
    /// of the scope <paramref name="owner"/>, or keeps that user's preview <paramref name="previewId"/> alive with the new draft.
    /// Refuses with <see cref="ReaderRules.TooManyPreviews"/> beyond <see cref="OcrLimits.MaxPreviews"/>; a user's preview
    /// beyond <see cref="OcrLimits.MaxPreviewsPerUser"/> ends that user's oldest one. Runs without the caller.
    /// </summary>
    public Task<PreviewReply> StartPreviewAsync(string? previewId, Reader reader, string owner, string user) => Detached(async () =>
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (_maker is null || _frames is null)
        {
            throw new InvalidOperationException(ReaderRules.NotInstalled);
        }

        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            Preview? preview = null;
            Preview? oldest = null;
            lock (_previews)
            {
                if (!string.IsNullOrEmpty(previewId) && _previews.TryGetValue(previewId, out var kept) && kept.IsOf(owner, user))
                {
                    if (kept.Reader.DeviceId == reader.DeviceId)
                    {
                        preview = kept;
                    }
                    else
                    {
                        // Another camera: nothing of the old one (its reading, frame and rows) stays under this id.
                        oldest = kept;
                    }
                }
                else
                {
                    var mine = _previews.Values.Where(p => p.IsOf(owner, user)).ToList();
                    if (mine.Count >= OcrLimits.MaxPreviewsPerUser)
                    {
                        // One user never holds more: their oldest preview (a page closed without a word) makes room.
                        oldest = mine.MinBy(p => p.Order);
                    }
                    else if (_previews.Count >= OcrLimits.MaxPreviews)
                    {
                        throw new InvalidOperationException(ReaderRules.TooManyPreviews);
                    }
                }
            }

            if (oldest is not null)
            {
                await EndPreviewsAsync(p => p == oldest).ConfigureAwait(false);
            }

            lock (_previews)
            {
                if (preview is null)
                {
                    // The page's own id (32 hex digits), so it can end a preview whose start answer it never saw; an id in use
                    // or any other text gets a new one.
                    var id = IsPreviewId(previewId) && !_previews.ContainsKey(previewId!) ? previewId! : Guid.NewGuid().ToString("N");
                    preview = new Preview(id, owner, user, Interlocked.Increment(ref _previewOrder));
                    _previews[preview.Id] = preview;
                }

                preview.Reader = reader with { OwnerScopeKey = owner == ICallContext.AllDevicesScopeKey ? null : owner };
                preview.Expires = _time.GetUtcNow() + OcrLimits.PreviewLease;
            }

            if (preview.Held is { } held && !held.Fits(preview.Reader))
            {
                await ReleaseHeldAsync(preview).ConfigureAwait(false); // another camera, source, size or reader: that frame is not its picture
            }

            await ReconcileAsync(preview).ConfigureAwait(false);
            return ReplyOf(preview);
        }
        finally
        {
            _sync.Release();
        }
    });

    /// <summary>Ends the preview <paramref name="previewId"/> of <paramref name="user"/> of the scope <paramref name="owner"/>; nothing when there is none.</summary>
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

    /// <summary>
    /// The reader and the last reading of the preview <paramref name="previewId"/> of <paramref name="user"/> of the scope
    /// <paramref name="owner"/>; null when it ended or is another user's.
    /// </summary>
    public (Reader Reader, ReaderReading? Last, bool Paused, IReadOnlyList<RecentRead> Recent)? PreviewOf(string? previewId, string owner, string user) =>
        PreviewOf(previewId, owner, user, withCutouts: false, known: null);

    /// <summary>
    /// <see cref="PreviewOf(string?, string, string)"/> with the rows the page shows (<see cref="ReadsOf(Preview)"/>): none
    /// while paused, the cut-outs when <paramref name="withCutouts"/> and not in <paramref name="known"/>.
    /// </summary>
    public (Reader Reader, ReaderReading? Last, bool Paused, IReadOnlyList<RecentRead> Recent)? PreviewOf(string? previewId, string owner, string user, bool withCutouts, IReadOnlyList<long>? known)
    {
        Preview? preview;
        ReaderReading? last;
        lock (_previews)
        {
            if (string.IsNullOrEmpty(previewId) || !_previews.TryGetValue(previewId, out preview) || !preview.IsOf(owner, user))
            {
                return null;
            }

            last = _previewLast.TryGetValue(previewId, out var kept) ? kept : null;
        }

        var paused = preview.Held is not null;
        return (preview.Reader, last, paused, paused ? [] : RowsOf(ReadsOf(preview), withCutouts, known));
    }

    /// <summary>
    /// Holds the frame of the latest reading of the preview <paramref name="previewId"/> of <paramref name="user"/>: in memory
    /// only, the bytes that reading already keeps (no copy), within the limits of a reading. Its own stream stops, a shared one
    /// is left alone: a saved watching reader goes on and fires its events. A frame of a shared stream carries the vote's
    /// values, and a draft changed since then fits no longer: such a frame is read at once with the draft. Runs without the caller.
    /// </summary>
    public Task<PauseReply> PausePreviewAsync(string? previewId, string owner, string user) => Detached(async () =>
    {
        if (_maker is null)
        {
            throw new InvalidOperationException(ReaderRules.NotInstalled);
        }

        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Preview? preview;
            ReaderReading? last;
            lock (_previews)
            {
                if (string.IsNullOrEmpty(previewId) || !_previews.TryGetValue(previewId, out preview) || !preview.IsOf(owner, user))
                {
                    return new PauseReply();
                }

                if (!_previewLast.TryGetValue(previewId, out last))
                {
                    return new PauseReply { Problem = ReaderRules.NothingToPause };
                }

                if (preview.Held is null)
                {
                    if (last.Jpeg.Length > Frames.MjpegParser.MaxFrameBytes || !_pictureFits(last.Jpeg))
                    {
                        return new PauseReply { Problem = ReaderRules.PictureTooLarge };
                    }

                    // Under this lock: from here on no live reading replaces the one held (KeepPreview).
                    var voted = preview.SharedWith is not null || ReaderRules.IsMoving(preview.Reader);
                    preview.Held = new HeldFrame(last.Jpeg, last.Reading.Time, last.Reader ?? preview.Reader) { ReadFor = voted ? null : last.Reader };
                    preview.SharedWith = null;
                }
            }

            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            preview.Problem = null;
            RereadHeld(preview);
            var reply = ReplyOf(last, withCutouts: true);
            var state = ReplyOf(preview);
            return new PauseReply { Paused = true, Picture = last.Jpeg.ToArray(), Reading = reply.Reading, Details = reply.Details, Rereading = state.Rereading, HeldRead = state.HeldRead };
        }
        finally
        {
            _sync.Release();
        }
    });

    /// <summary>
    /// The held frame of the preview <paramref name="previewId"/> of <paramref name="user"/> goes, with the reading it gave, and
    /// the preview reads live again. An ended preview or another user's answers like one that does not hold a frame. Runs
    /// without the caller.
    /// </summary>
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

    /// <summary>What the page hears of its preview now.</summary>
    private PreviewReply ReplyOf(Preview preview)
    {
        lock (_previews)
        {
            return new PreviewReply
            {
                PreviewId = preview.Id,
                Problem = preview.Problem ?? preview.StreamProblem,
                Paused = preview.Held is not null,
                Rereading = preview.Held is { } held && !SameAreas(held.ReadFor, preview.Reader),
                HeldRead = preview.Held?.Reads ?? 0,
            };
        }
    }

    /// <summary>
    /// The draft changed while a frame is held: one worker reads that frame with the newest draft through the same recognition
    /// workers as every reading. Latest wins: a burst of edits never queues readings, a reading of an older draft is never
    /// shown. Every area is read (no reuse: the held frame's pixels never change, so "unchanged" would say nothing). Under
    /// <see cref="_sync"/>.
    /// </summary>
    private void RereadHeld(Preview preview)
    {
        lock (_previews)
        {
            if (preview.Held is not { } held || held.Working || SameAreas(held.ReadFor, preview.Reader))
            {
                return;
            }

            held.Working = true;
            var ct = held.Stop.Token;
            held.Worker = Task.Run(() => RereadLoopAsync(preview, held, ct), CancellationToken.None);
        }
    }

    private async Task RereadLoopAsync(Preview preview, HeldFrame held, CancellationToken ct)
    {
        while (true)
        {
            Reader reader;
            lock (_previews)
            {
                if (preview.Held != held || SameAreas(held.ReadFor, preview.Reader))
                {
                    held.Working = false;
                    return;
                }

                reader = preview.Reader;
            }

            try
            {
                var read = await _maker!.ReadAsync(reader, held.Jpeg, reuseSource: null, ct).ConfigureAwait(false);
                read = read with { Reading = read.Reading with { Time = held.Time } }; // the time of the frame, not of the edit
                lock (_previews)
                {
                    held.ReadFor = reader;
                }

                if (SameAreas(reader, preview.Reader))
                {
                    lock (_previews)
                    {
                        held.Reads++;
                    }

                    KeepPreview(preview, read, held);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (_previews)
                {
                    held.Working = false;
                }

                return;
            }
#pragma warning disable CA1031 // A failed reading is logged; the page keeps its results and the next edit tries again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogReadingFailed(_logger, reader.Name, ex);
                lock (_previews)
                {
                    held.ReadFor = reader;
                }
            }
        }
    }

    /// <summary>The held frame goes, with the reading that keeps its bytes. Under <see cref="_sync"/>.</summary>
    private async Task ReleaseHeldAsync(Preview preview)
    {
        HeldFrame? held;
        lock (_previews)
        {
            held = preview.Held;
            preview.Held = null;
            if (held is not null)
            {
                _previewLast.TryRemove(preview.Id, out _);
            }
        }

        if (held is null)
        {
            return;
        }

        await held.Stop.CancelAsync().ConfigureAwait(false);
        await held.Worker.ConfigureAwait(false);
        held.Stop.Dispose();
    }

    /// <summary>The previews that share the stream of the saved reader <paramref name="readerId"/>.</summary>
    private List<Preview> SharingPreviews(string readerId)
    {
        lock (_previews)
        {
            return _previews.Count == 0 ? [] : [.. _previews.Values.Where(p => p.SharedWith == readerId)];
        }
    }

    /// <summary>Keeps a preview's reading and tells the user's pages (without the cut-outs).</summary>
    /// <param name="preview">The preview.</param>
    /// <param name="reading">Its reading.</param>
    /// <param name="held">The held frame this reading is of; null: a live reading, dropped while a frame is held.</param>
    /// <param name="ownStream">A frame of its own stream: its new values are the preview's rows (a shared stream's are the reader's).</param>
    private void KeepPreview(Preview preview, ReaderReading reading, HeldFrame? held = null, bool ownStream = false)
    {
        lock (_previews)
        {
            if (!_previews.ContainsKey(preview.Id) || preview.Held != held)
            {
                return; // ended or paused meanwhile
            }

            _previewLast[preview.Id] = reading;
        }

        if (ownStream)
        {
            AddReads(preview.Recent, reading.Reader ?? preview.Reader, reading);
        }

        if (_ctx.Events is not { } events)
        {
            Interlocked.Increment(ref _keptPreviews);
            return;
        }

        // Only to the user who started it: nobody else learns its id or what it reads.
        var json = OcrJson.Serialize(new PreviewEvent
        {
            PreviewId = preview.Id,
            HeldRead = held?.Reads ?? 0,
            Reading = reading.Reading,
            Details = [.. reading.Details.Select(d => d with { Cutout = null })],
        });
        events.Publish(OcrMethods.PreviewTopic, json, PluginEventAudience.User(preview.User));
        Interlocked.Increment(ref _keptPreviews); // after the event: a test that waits for the count sees the event
    }

    /// <summary>The readings of previews kept so far (tests wait on it).</summary>
    public int KeptPreviewReadings => Volatile.Read(ref _keptPreviews);

    private int _keptPreviews;

    /// <summary>
    /// Every preview again (the saved readers' loops changed). The saved readers come first: a preview's own stream that no
    /// longer fits beside them (the watching limits, also per site) stops with <see cref="ReaderRules.PreviewBusy"/>, the oldest
    /// previews keep theirs. Under <see cref="_sync"/>.
    /// </summary>
    private async Task ReconcilePreviewsAsync()
    {
        List<Preview> previews;
        lock (_previews)
        {
            previews = [.. _previews.Values.OrderBy(p => p.Order)];
        }

        int watching;
        lock (_loops)
        {
            watching = _loops.Count;
        }

        var left = MaxWatching - watching;
        var perSite = LoopsPerSite();
        foreach (var preview in previews.Where(p => p.Loop is not null))
        {
            var site = preview.Site;
            if (left > 0 && (site is not { } at || perSite.GetValueOrDefault(at) < ReaderRules.MaxWatchingPerSite))
            {
                left--;
                if (site is { } s)
                {
                    perSite[s] = perSite.GetValueOrDefault(s) + 1;
                }

                continue;
            }

            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            preview.Problem = ReaderRules.PreviewBusy;
        }

        foreach (var preview in previews)
        {
            await ReconcileAsync(preview).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Brings one preview in line with its draft: without areas nothing streams; a problem of the camera stops it; an unchanged saved watching reader shares
    /// its stream; otherwise its own stream, kept while camera, source, resolution and rate stay (the areas follow the draft),
    /// within the watching limits. Under <see cref="_sync"/>.
    /// </summary>
    private async Task ReconcileAsync(Preview preview)
    {
        var reader = preview.Reader;
        if (preview.Held is not null)
        {
            // The frame needs no camera: only a camera that left the owner's devices or a deleted reader ends the pause (fail closed).
            var off = (await CheckAsync(reader, CancellationToken.None).ConfigureAwait(false)).Problem == ReaderRules.Off;
            if (!off && (reader.Id.Length == 0 || StoredOf(reader.Id) is not null))
            {
                await StopOwnStreamAsync(preview).ConfigureAwait(false);
                preview.SharedWith = null;
                preview.Problem = null;
                RereadHeld(preview);
                return;
            }

            await ReleaseHeldAsync(preview).ConfigureAwait(false);
        }

        if (reader.Areas.Count == 0)
        {
            // Text is read only inside areas: no area, no stream (the page asks to draw one).
            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            preview.SharedWith = null;
            preview.Problem = null;
            return;
        }

        var (problem, camera) = await CheckAsync(reader, CancellationToken.None).ConfigureAwait(false);
        problem ??= Starting ? ReaderRules.Starting : null; // the sync after the graphics card's test starts it
        if (problem is not null)
        {
            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            preview.SharedWith = null;
            preview.Problem = problem;
            return;
        }

        var saved = reader.Id.Length > 0 ? WatchingLoopOf(reader.Id) : null;
        if (saved is not null && SameReading(saved.Reader, reader))
        {
            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            preview.SharedWith = reader.Id;
            preview.Problem = null;
            return;
        }

        preview.SharedWith = null;
        if (preview.Loop is { } own && SameStream(own.Reader, reader))
        {
            preview.Problem = null;
            return;
        }

        await StopOwnStreamAsync(preview).ConfigureAwait(false);
        int watching;
        lock (_loops)
        {
            watching = _loops.Count;
        }

        int ownStreams;
        int atSite;
        var site = camera?.SiteId;
        lock (_previews)
        {
            ownStreams = _previews.Values.Count(p => p.Loop is not null);
            atSite = site is { } s ? _previews.Values.Count(p => p.Loop is not null && p.Site == s) : 0;
        }

        if (watching + ownStreams >= MaxWatching
            || (site is { } at && LoopsPerSite().GetValueOrDefault(at) + atSite >= ReaderRules.MaxWatchingPerSite))
        {
            preview.Problem = ReaderRules.PreviewBusy;
            return;
        }

        var stop = new CancellationTokenSource();
        preview.Site = site;
        preview.SiteName = camera?.SiteName;
        preview.Problem = null;
        preview.StreamProblem = null;
        var loop = new WatchLoop(reader, stop) { Site = site, SiteName = camera?.SiteName };
        loop.Run = Task.Run(
            () => LoopAsync(reader, () => preview.Reader, vote: false, source: "preview:" + preview.Id, (_, reading) => KeepPreview(preview, reading, ownStream: true), p => preview.StreamProblem = p, slot: null, stop.Token),
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

    /// <summary>Ends the previews <paramref name="which"/> chooses: gone from the list, their streams stopped. Under <see cref="_sync"/>.</summary>
    private async Task EndPreviewsAsync(Func<Preview, bool> which)
    {
        List<Preview> ending;
        lock (_previews)
        {
            ending = [.. _previews.Values.Where(which)];
            foreach (var preview in ending)
            {
                _previews.Remove(preview.Id);
                _previewLast.TryRemove(preview.Id, out _);
            }
        }

        foreach (var preview in ending)
        {
            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            await ReleaseHeldAsync(preview).ConfigureAwait(false);
        }
    }

    /// <summary>The timer: previews whose lease ended go.</summary>
    private Task SweepPreviewsAsync() => Detached(async () =>
    {
        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            var now = _time.GetUtcNow();
            await EndPreviewsAsync(p => p.Expires <= now).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    });

    /// <summary>A preview id a page made: 32 hex digits.</summary>
    private static bool IsPreviewId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigit);

    /// <summary>The draft reads like the saved reader's loop: the same camera, source, resolution, rate, Moving objects and areas.</summary>
    private static bool SameReading(Reader loop, Reader draft) =>
        SamePicture(loop, draft) && loop.Fps == draft.Fps && ReaderRules.IsMoving(loop) == ReaderRules.IsMoving(draft) && loop.Areas.Zip(draft.Areas).All(p => p.First.Name == p.Second.Name);

    /// <summary>The areas read alike: the same names, places and expected texts, in the same order; null fits nothing.</summary>
    private static bool SameAreas(Reader? a, Reader b) => a is not null && a.Areas.SequenceEqual(b.Areas);

    /// <summary>The same stream: camera, source, resolution and rate.</summary>
    private static bool SameStream(Reader a, Reader b) =>
        a.DeviceId == b.DeviceId && a.VideoSource == b.VideoSource && a.Resolution == b.Resolution && a.Fps == b.Fps;

    /// <summary>
    /// The frame a paused preview keeps (docs/spec/ocr.md "Pause"): server memory only, one per preview, the bytes of the reading
    /// it came from. <see cref="ReadFor"/> and the worker's state change under the lock of the previews.
    /// </summary>
    private sealed class HeldFrame(ReadOnlyMemory<byte> jpeg, DateTimeOffset time, Reader reader)
    {
        public ReadOnlyMemory<byte> Jpeg { get; } = jpeg;

        /// <summary>When the frame was read: its results keep this time through every re-read.</summary>
        public DateTimeOffset Time { get; } = time;

        public CancellationTokenSource Stop { get; } = new();

        public Task Worker { get; set; } = Task.CompletedTask;

        public bool Working { get; set; }

        /// <summary>The readings of this frame that reached the page: each event carries its number.</summary>
        public int Reads { get; set; }

        /// <summary>The draft of the frame's latest reading; null: none fits (it is read once more).</summary>
        public Reader? ReadFor { get; set; }

        /// <summary>The frame is a picture of <paramref name="draft"/>: its camera, video source and size, the same reader (a new one may be saved meanwhile).</summary>
        public bool Fits(Reader draft) =>
            draft.DeviceId == reader.DeviceId && draft.VideoSource == reader.VideoSource && draft.Resolution == reader.Resolution
            && (reader.Id.Length == 0 || draft.Id == reader.Id);
    }

    /// <summary>One page's preview. Its fields change under <see cref="_sync"/>; <see cref="Reader"/> is read by its loop.</summary>
    private sealed class Preview(string id, string owner, string user, long order)
    {
        private volatile Reader _reader = new();
        private volatile string? _streamProblem;

        public string Id { get; } = id;

        /// <summary>The scope of the page that started it (<see cref="OwnerScope.Of"/>).</summary>
        public string Owner { get; } = owner;

        /// <summary>The user who started it: only they keep, read and stop it.</summary>
        public string User { get; } = user;

        /// <summary>When it started among the previews (older ones keep their streams first).</summary>
        public long Order { get; } = order;

        public Reader Reader
        {
            get => _reader;
            set => _reader = value;
        }

        public DateTimeOffset Expires { get; set; }

        public string? SharedWith { get; set; }

        public WatchLoop? Loop { get; set; }

        /// <summary>The frame it holds while paused; set and cleared under the lock of the previews.</summary>
        public HeldFrame? Held { get; set; }

        /// <summary>The new values of its own stream; goes with the preview.</summary>
        public RecentReadList Recent { get; } = new();

        public Guid? Site { get; set; }

        public string? SiteName { get; set; }

        public string? Problem { get; set; }

        /// <summary>Why its own stream gives no pictures now (the camera's refusal); null while pictures come.</summary>
        public string? StreamProblem
        {
            get => _streamProblem;
            set => _streamProblem = value;
        }

        public bool IsOf(string owner, string user) =>
            Owner == owner && string.Equals(User, user, StringComparison.OrdinalIgnoreCase);
    }
}
