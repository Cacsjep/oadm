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
/// the same loop with the vote and the events off, and counts against the watching limits (also per site), after the saved
/// readers. At most <see cref="OcrLimits.MaxPreviews"/>, <see cref="OcrLimits.MaxPreviewsPerUser"/> per user; each belongs to
/// the user who started it and ends <see cref="OcrLimits.PreviewLease"/> after the page's last call, so a closed client never
/// leaves one running.
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
                    preview = kept;
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

            await ReconcileAsync(preview).ConfigureAwait(false);
            return new PreviewReply { PreviewId = preview.Id, Problem = preview.Problem ?? preview.StreamProblem };
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
    public (Reader Reader, ReaderReading? Last)? PreviewOf(string? previewId, string owner, string user)
    {
        lock (_previews)
        {
            if (string.IsNullOrEmpty(previewId) || !_previews.TryGetValue(previewId, out var preview) || !preview.IsOf(owner, user))
            {
                return null;
            }

            return (preview.Reader, _previewLast.TryGetValue(previewId, out var last) ? last : null);
        }
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
    private void KeepPreview(Preview preview, ReaderReading reading)
    {
        lock (_previews)
        {
            if (!_previews.ContainsKey(preview.Id))
            {
                return; // ended meanwhile
            }

            _previewLast[preview.Id] = reading;
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

        var left = ReaderRules.MaxWatching(OnGpu) - watching;
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
        if (reader.Areas.Count == 0)
        {
            // Text is read only inside areas: no area, no stream (the page asks to draw one).
            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            preview.SharedWith = null;
            preview.Problem = null;
            return;
        }

        var (problem, camera) = await CheckAsync(reader, CancellationToken.None).ConfigureAwait(false);
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

        if (watching + ownStreams >= ReaderRules.MaxWatching(OnGpu)
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
            () => LoopAsync(reader, () => preview.Reader, vote: false, source: "preview:" + preview.Id, (_, reading) => KeepPreview(preview, reading), p => preview.StreamProblem = p, slot: null, stop.Token),
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

    /// <summary>The draft reads like the saved reader's loop: the same camera, source, resolution, rate and areas.</summary>
    private static bool SameReading(Reader loop, Reader draft) =>
        SamePicture(loop, draft) && loop.Fps == draft.Fps && loop.Areas.Zip(draft.Areas).All(p => p.First.Name == p.Second.Name);

    /// <summary>The same stream: camera, source, resolution and rate.</summary>
    private static bool SameStream(Reader a, Reader b) =>
        a.DeviceId == b.DeviceId && a.VideoSource == b.VideoSource && a.Resolution == b.Resolution && a.Fps == b.Fps;

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
