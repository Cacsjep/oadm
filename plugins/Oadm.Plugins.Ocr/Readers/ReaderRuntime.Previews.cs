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
/// the same loop with the vote and the events off, and counts against the watching limits (also per site). At most
/// <see cref="OcrLimits.MaxPreviews"/>; each ends <see cref="OcrLimits.PreviewLease"/> after the page's last call, so a
/// closed client never leaves one running.
/// </summary>
internal sealed partial class ReaderRuntime
{
    /// <summary>How often ended leases are looked for.</summary>
    public static readonly TimeSpan PreviewSweepInterval = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, Preview> _previews = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReaderReading> _previewLast = new(StringComparer.Ordinal);
    private volatile Dictionary<Guid, int> _watchingPerSite = [];

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
    /// Starts a preview of <paramref name="reader"/> (checked by <see cref="ReaderRules.PreviewOf"/>) for the scope
    /// <paramref name="owner"/>, or keeps the preview <paramref name="previewId"/> of that scope alive with the new draft.
    /// Refuses with <see cref="ReaderRules.TooManyPreviews"/> beyond <see cref="OcrLimits.MaxPreviews"/>. Runs without the caller.
    /// </summary>
    public Task<PreviewReply> StartPreviewAsync(string? previewId, Reader reader, string owner) => Detached(async () =>
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
            lock (_previews)
            {
                if (!string.IsNullOrEmpty(previewId) && _previews.TryGetValue(previewId, out var kept) && kept.Owner == owner)
                {
                    preview = kept;
                }
                else if (_previews.Count >= OcrLimits.MaxPreviews)
                {
                    throw new InvalidOperationException(ReaderRules.TooManyPreviews);
                }
                else
                {
                    preview = new Preview(Guid.NewGuid().ToString("N"), owner);
                    _previews[preview.Id] = preview;
                }

                preview.Reader = reader with { OwnerScopeKey = owner == Oadm.Sdk.Plugins.ICallContext.AllDevicesScopeKey ? null : owner };
                preview.Expires = _time.GetUtcNow() + OcrLimits.PreviewLease;
            }

            await ReconcileAsync(preview).ConfigureAwait(false);
            return new PreviewReply { PreviewId = preview.Id, Problem = preview.Problem };
        }
        finally
        {
            _sync.Release();
        }
    });

    /// <summary>Ends the preview <paramref name="previewId"/> of the scope <paramref name="owner"/>; nothing when there is none.</summary>
    public Task StopPreviewAsync(string? previewId, string owner) => Detached(async () =>
    {
        await _sync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await EndPreviewsAsync(p => p.Id == previewId && p.Owner == owner).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    });

    /// <summary>The reader and the last reading of the preview <paramref name="previewId"/> of the scope <paramref name="owner"/>; null when it ended.</summary>
    public (Reader Reader, ReaderReading? Last)? PreviewOf(string? previewId, string owner)
    {
        lock (_previews)
        {
            if (string.IsNullOrEmpty(previewId) || !_previews.TryGetValue(previewId, out var preview) || preview.Owner != owner)
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

    /// <summary>Keeps a preview's reading and tells the pages of its scope (without the cut-outs).</summary>
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
            return;
        }

        var json = OcrJson.Serialize(new PreviewEvent
        {
            PreviewId = preview.Id,
            Reading = reading.Reading,
            Details = [.. reading.Details.Select(d => d with { Cutout = null })],
        });
        events.Publish(OcrMethods.PreviewTopic, json, PluginEventAudience.AllDevices);
        if (preview.Owner != Oadm.Sdk.Plugins.ICallContext.AllDevicesScopeKey)
        {
            events.Publish(OcrMethods.PreviewTopic, json, PluginEventAudience.DeviceScope(preview.Owner));
        }
    }

    /// <summary>Every preview again (the saved readers' loops changed). Under <see cref="_sync"/>.</summary>
    private async Task ReconcilePreviewsAsync()
    {
        List<Preview> previews;
        lock (_previews)
        {
            previews = [.. _previews.Values];
        }

        foreach (var preview in previews)
        {
            await ReconcileAsync(preview).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Brings one preview in line with its draft: a problem of the camera stops it; an unchanged saved watching reader shares
    /// its stream; otherwise its own stream, kept while camera, source, resolution and rate stay (the areas follow the draft),
    /// within the watching limits. Under <see cref="_sync"/>.
    /// </summary>
    private async Task ReconcileAsync(Preview preview)
    {
        var reader = preview.Reader;
        var (problem, camera) = await CheckAsync(reader, CancellationToken.None).ConfigureAwait(false);
        if (problem is not null)
        {
            await StopOwnStreamAsync(preview).ConfigureAwait(false);
            preview.SharedWith = null;
            preview.Problem = problem;
            return;
        }

        WatchLoop? saved = null;
        lock (_loops)
        {
            if (reader.Id.Length > 0)
            {
                _loops.TryGetValue(reader.Id, out saved);
            }
        }

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
            || (site is { } at && _watchingPerSite.GetValueOrDefault(at) + atSite >= ReaderRules.MaxWatchingPerSite))
        {
            preview.Problem = ReaderRules.PreviewBusy;
            return;
        }

        var stop = new CancellationTokenSource();
        preview.Site = site;
        preview.Problem = null;
        preview.Loop = new WatchLoop(reader, stop, Task.Run(
            () => LoopAsync(reader, () => preview.Reader, vote: false, (_, reading) => KeepPreview(preview, reading), slot: null, stop.Token),
            CancellationToken.None));
    }

    private static async Task StopOwnStreamAsync(Preview preview)
    {
        if (preview.Loop is not { } loop)
        {
            return;
        }

        preview.Loop = null;
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

    /// <summary>The draft reads like the saved reader's loop: the same camera, source, resolution, rate and areas.</summary>
    private static bool SameReading(Reader loop, Reader draft) =>
        OcrJson.Serialize(loop with { Name = string.Empty, OwnerScopeKey = null }) == OcrJson.Serialize(draft with { Name = string.Empty, OwnerScopeKey = null });

    /// <summary>The same stream: camera, source, resolution and rate.</summary>
    private static bool SameStream(Reader a, Reader b) =>
        a.DeviceId == b.DeviceId && a.VideoSource == b.VideoSource && a.Resolution == b.Resolution && a.Fps == b.Fps;

    /// <summary>One page's preview. Its fields change under <see cref="_sync"/>; <see cref="Reader"/> is read by its loop.</summary>
    private sealed class Preview(string id, string owner)
    {
        private volatile Reader _reader = new();

        public string Id { get; } = id;

        /// <summary>The scope of the page that started it (<see cref="OwnerScope.Of"/>).</summary>
        public string Owner { get; } = owner;

        public Reader Reader
        {
            get => _reader;
            set => _reader = value;
        }

        public DateTimeOffset Expires { get; set; }

        public string? SharedWith { get; set; }

        public WatchLoop? Loop { get; set; }

        public Guid? Site { get; set; }

        public string? Problem { get; set; }
    }
}
