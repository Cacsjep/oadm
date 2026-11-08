namespace Oadm.Plugins.Shared.Vision;

/// <summary>
/// One page's preview as both vision add-ons keep it (docs/spec/ocr.md "Page", docs/spec/vision-center.md "Previews"): the id
/// the page made, the user and device scope that started it (only they keep, read, pause and stop it), its place among the
/// previews (older ones keep their streams first) and the end of its lease.
/// </summary>
internal abstract class PreviewEntry(string id, string owner, string user, long order)
{
    public string Id { get; } = id;

    /// <summary>The scope of the page that started it (<c>OwnerScope.Of</c>).</summary>
    public string Owner { get; } = owner;

    /// <summary>The user who started it.</summary>
    public string User { get; } = user;

    /// <summary>When it started among the previews.</summary>
    public long Order { get; } = order;

    /// <summary>The end of its lease; changed under the registry's lock.</summary>
    public DateTimeOffset Expires { get; set; }

    public bool IsOf(string owner, string user) =>
        Owner == owner && string.Equals(User, user, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The previews of one add-on: page-made ids, at most <see cref="Max"/> per server and <see cref="MaxPerUser"/> per user (a
/// further one of a user ends that user's oldest), each ending <see cref="Lease"/> after the page's last call (checked with
/// the <see cref="TimeProvider"/>), so a closed client never leaves one running. The runtime locks the registry itself
/// (<c>lock (registry)</c>) around the members marked "under the lock" and around its own state of the previews; the other
/// members take the lock themselves.
/// </summary>
internal sealed class PreviewRegistry<T>(TimeProvider time, int max, int maxPerUser, TimeSpan lease)
    where T : PreviewEntry
{
    private readonly Dictionary<string, T> _previews = new(StringComparer.Ordinal);
    private long _order;

    public int Max { get; } = max;

    public int MaxPerUser { get; } = maxPerUser;

    public TimeSpan Lease { get; } = lease;

    /// <summary>Every preview now (under the lock).</summary>
    public IEnumerable<T> Values => _previews.Values;

    /// <summary>How many previews run (under the lock).</summary>
    public int Count => _previews.Count;

    /// <summary>Under the lock.</summary>
    public bool ContainsKey(string id) => _previews.ContainsKey(id);

    /// <summary>Under the lock.</summary>
    public bool TryGetValue(string id, out T preview) => _previews.TryGetValue(id, out preview!);

    /// <summary>A copy of every preview, oldest first.</summary>
    public IReadOnlyList<T> Snapshot()
    {
        lock (this)
        {
            return [.. _previews.Values.OrderBy(p => p.Order)];
        }
    }

    /// <summary>The preview <paramref name="previewId"/> of <paramref name="user"/> of the scope <paramref name="owner"/>; null when it ended or is another user's.</summary>
    public T? Find(string? previewId, string owner, string user)
    {
        lock (this)
        {
            return !string.IsNullOrEmpty(previewId) && _previews.TryGetValue(previewId, out var preview) && preview.IsOf(owner, user) ? preview : null;
        }
    }

    /// <summary>
    /// What a start of <paramref name="previewId"/> by <paramref name="user"/> finds: that user's preview when
    /// <paramref name="keeps"/> says it may go on (else it is the one to end: another camera), or none and, when the user holds
    /// <see cref="MaxPerUser"/> already, the user's oldest one to end. Refuses with <paramref name="tooMany"/> when the server
    /// holds <see cref="Max"/>.
    /// </summary>
    public (T? Kept, T? Ending) Claim(string? previewId, string owner, string user, Func<T, bool> keeps, string tooMany)
    {
        ArgumentNullException.ThrowIfNull(keeps);
        lock (this)
        {
            if (!string.IsNullOrEmpty(previewId) && _previews.TryGetValue(previewId, out var kept) && kept.IsOf(owner, user))
            {
                return keeps(kept) ? (kept, null) : (null, kept);
            }

            var mine = _previews.Values.Where(p => p.IsOf(owner, user)).ToList();
            if (mine.Count >= MaxPerUser)
            {
                return (null, mine.MinBy(p => p.Order));
            }

            return _previews.Count >= Max ? throw new InvalidOperationException(tooMany) : (null, null);
        }
    }

    /// <summary>
    /// <paramref name="kept"/>, or a new preview made by <paramref name="make"/> under the page's own id (32 hex digits, so the
    /// page can end a preview whose start answer it never saw; an id in use or any other text gets a new one); its lease
    /// starts again (the keep-alive).
    /// </summary>
    public T Open(T? kept, string? previewId, Func<string, long, T> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        lock (this)
        {
            var preview = kept;
            if (preview is null)
            {
                var id = IsPreviewId(previewId) && !_previews.ContainsKey(previewId!) ? previewId! : Guid.NewGuid().ToString("N");
                preview = make(id, ++_order);
                _previews[preview.Id] = preview;
            }

            preview.Expires = time.GetUtcNow() + Lease;
            return preview;
        }
    }

    /// <summary>Takes the previews <paramref name="which"/> chooses out of the registry and returns them (their streams are the caller's to stop).</summary>
    public List<T> RemoveWhere(Func<T, bool> which)
    {
        ArgumentNullException.ThrowIfNull(which);
        lock (this)
        {
            var ending = _previews.Values.Where(which).ToList();
            foreach (var preview in ending)
            {
                _previews.Remove(preview.Id);
            }

            return ending;
        }
    }

    /// <summary>Whether the lease of <paramref name="preview"/> has ended (for <see cref="RemoveWhere"/>).</summary>
    public Func<T, bool> Expired()
    {
        var now = time.GetUtcNow();
        return p => p.Expires <= now;
    }

    /// <summary>A preview id a page made: 32 hex digits.</summary>
    public static bool IsPreviewId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigit);
}

/// <summary>
/// The worker of a paused preview (docs/spec/ocr.md "Pause"): runs its step again while a newer draft waits, one run at a time,
/// latest wins (a burst of edits never queues runs). <see cref="Kick"/> and the check of <c>due</c> run under the given lock.
/// </summary>
internal sealed class LatestWinsWorker(object gate, Action<Exception>? failed = null)
{
    private CancellationTokenSource Stop { get; } = new();
    private Task _worker = Task.CompletedTask;
    private bool _working;

    /// <summary>Starts the worker unless it runs or nothing is due; it runs <paramref name="step"/> while <paramref name="due"/> (checked under the lock) says so.</summary>
    public void Kick(Func<bool> due, Func<CancellationToken, Task> step)
    {
        ArgumentNullException.ThrowIfNull(due);
        ArgumentNullException.ThrowIfNull(step);
        lock (gate)
        {
            if (_working || Stop.IsCancellationRequested || !due())
            {
                return;
            }

            _working = true;
            var ct = Stop.Token;
            _worker = Task.Run(() => LoopAsync(due, step, ct), CancellationToken.None);
        }
    }

    private async Task LoopAsync(Func<bool> due, Func<CancellationToken, Task> step, CancellationToken ct)
    {
        while (true)
        {
            lock (gate)
            {
                if (ct.IsCancellationRequested || !due())
                {
                    _working = false;
                    return;
                }
            }

            try
            {
                await step(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (gate)
                {
                    _working = false;
                }

                return;
            }
#pragma warning disable CA1031 // A failed run is the step's to tell; the worker stops, the next Kick runs again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failed?.Invoke(ex);
                lock (gate)
                {
                    _working = false;
                }

                return;
            }
        }
    }

    /// <summary>Stops the worker and waits for its run.</summary>
    public async Task StopAsync()
    {
        await Stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The worker never fails its stop.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        Stop.Dispose();
    }
}

/// <summary>The frame a paused preview may hold: within the stream's frame limit and the picture limits (<see cref="PictureLimits"/>).</summary>
internal static class HeldPictures
{
    /// <summary><paramref name="jpeg"/> may be held; <paramref name="fits"/> checks its pixels (<see cref="PictureLimits.Fits"/>).</summary>
    public static bool Fits(ReadOnlyMemory<byte> jpeg, Func<ReadOnlyMemory<byte>, bool> fits)
    {
        ArgumentNullException.ThrowIfNull(fits);
        return jpeg.Length <= MjpegParser.MaxFrameBytes && fits(jpeg);
    }
}
