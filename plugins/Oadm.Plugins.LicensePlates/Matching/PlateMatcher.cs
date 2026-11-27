namespace Oadm.Plugins.LicensePlates.Matching;

/// <summary>The plates the matcher compares with (the plate store).</summary>
internal interface IPlateSource
{
    /// <summary>The plate with exactly this text; null when the list has none.</summary>
    Plate? Find(string text);

    /// <summary>The texts of the plates that have one of <paramref name="tags"/> (for building an index).</summary>
    IReadOnlyCollection<string> TextsWithAnyTag(IReadOnlySet<Guid> tags);
}

/// <summary>The decision of one read text at one camera.</summary>
/// <param name="Result">Allowed, Not allowed or Unknown plate.</param>
/// <param name="Reason">Why.</param>
/// <param name="Plate">The plate of the list (exact or near), null for an unknown plate.</param>
/// <param name="ReasonTime">Allowed from or Allowed to of a plate that is not allowed yet or any more.</param>
/// <param name="Exact">The read text is a plate of the list: the read is decided at once.</param>
internal readonly record struct MatchResult(ReadResult Result, ReadReason Reason, Plate? Plate, DateTime? ReasonTime, bool Exact)
{
    public static MatchResult Unknown { get; } = new(ReadResult.Unknown, ReadReason.NoPlate, null, null, false);
}

/// <summary>
/// Decides a read (docs/spec/license-plate-center.md "Matching"): only plates allowed at the camera at that moment count.
/// An exact match is one dictionary lookup of the plate store (Allowed, or Not allowed with the reason); a near match looks
/// up the deletion index of the camera's allowed tag set (<see cref="DeletionIndex"/>), built once per tag set and rebuilt
/// when a plate with one of its tags changes, and checks the few candidates; never a scan of the list per read. Equally
/// close to two allowed plates: Not allowed ("Matches more than one plate"); near matches to plates not allowed here do not
/// count. Thread safe; a rebuild never blocks a read (the previous index answers, each candidate is checked against the
/// current plate list).
/// </summary>
internal sealed class PlateMatcher
{
    private readonly IPlateSource _source;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, TagSet> _sets = new(StringComparer.Ordinal);
    private int _deviation = 1;
    private int _rebuildScheduled;

    public PlateMatcher(IPlateSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>The allowed character deviation: 0 (Exact), 1 or 2. A change rebuilds every index.</summary>
    public int Deviation
    {
        get => Volatile.Read(ref _deviation);
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
            if (Interlocked.Exchange(ref _deviation, clamped) == clamped)
            {
                return;
            }

            lock (_sync)
            {
                foreach (var set in _sets.Values)
                {
                    set.Index = null;
                }
            }
        }
    }

    /// <summary>Run rebuilds right away on the caller's thread (tests); else they run in the background.</summary>
    public bool RebuildInline { get; init; }

    /// <summary>Whether a plate is allowed at a camera with <paramref name="allowedTags"/> at <paramref name="now"/> (server wall-clock).</summary>
    public static bool IsAllowed(Plate plate, IReadOnlySet<Guid> allowedTags, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(plate);
        ArgumentNullException.ThrowIfNull(allowedTags);
        return HasAllowedTag(plate, allowedTags) && (plate.AllowedFrom is not { } from || now >= from) && (plate.AllowedTo is not { } to || now < to);
    }

    private static bool HasAllowedTag(Plate plate, IReadOnlySet<Guid> allowedTags)
    {
        foreach (var tag in plate.Tags)
        {
            if (allowedTags.Contains(tag))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The decision for <paramref name="read"/> (normalized) at a camera with <paramref name="allowedTags"/> at <paramref name="now"/>.</summary>
    public MatchResult Match(string read, IReadOnlySet<Guid> allowedTags, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(allowedTags);
        if (read.Length == 0)
        {
            return MatchResult.Unknown;
        }

        if (_source.Find(read) is { } exact)
        {
            if (IsAllowed(exact, allowedTags, now))
            {
                return new MatchResult(ReadResult.Allowed, ReadReason.Exact, exact, null, true);
            }

            if (!HasAllowedTag(exact, allowedTags))
            {
                return new MatchResult(ReadResult.NotAllowed, ReadReason.NoTagHere, exact, null, true);
            }

            return exact.AllowedFrom is { } from && now < from
                ? new MatchResult(ReadResult.NotAllowed, ReadReason.NotYet, exact, from, true)
                : new MatchResult(ReadResult.NotAllowed, ReadReason.Ended, exact, exact.AllowedTo, true);
        }

        var deviation = Deviation;
        if (deviation == 0 || allowedTags.Count == 0)
        {
            return MatchResult.Unknown;
        }

        var index = IndexOf(allowedTags, deviation);
        if (index is null)
        {
            return MatchResult.Unknown;
        }

        Plate? best = null;
        var bestDistance = int.MaxValue;
        var tied = false;
        foreach (var text in index.Candidates(read))
        {
            if (_source.Find(text) is not { } plate || !IsAllowed(plate, allowedTags, now))
            {
                continue;
            }

            var max = DeletionIndex.MaxFor(plate.Text.Length, deviation);
            var distance = EditDistance.Bounded(read, plate.Text, max);
            if (distance > max || distance == 0)
            {
                continue;
            }

            if (distance < bestDistance)
            {
                best = plate;
                bestDistance = distance;
                tied = false;
            }
            else if (distance == bestDistance && !string.Equals(plate.Text, best?.Text, StringComparison.Ordinal))
            {
                tied = true;
            }
        }

        if (best is null)
        {
            return MatchResult.Unknown;
        }

        return tied
            ? new MatchResult(ReadResult.NotAllowed, ReadReason.SeveralPlates, null, null, false)
            : new MatchResult(ReadResult.Allowed, ReadReason.Near, best, null, false);
    }

    /// <summary>Plates with one of <paramref name="tags"/> changed (added, deleted, retagged): the indexes of the sets with those tags are rebuilt.</summary>
    public void Changed(IEnumerable<Guid> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var changed = tags as IReadOnlySet<Guid> ?? tags.ToHashSet();
        if (changed.Count == 0)
        {
            return;
        }

        var any = false;
        lock (_sync)
        {
            foreach (var set in _sets.Values)
            {
                if (set.Tags.Overlaps(changed))
                {
                    set.Dirty = true;
                    any = true;
                }
            }
        }

        if (any)
        {
            ScheduleRebuild();
        }
    }

    /// <summary>The tag sets cameras no longer use are forgotten (their memory is freed).</summary>
    public void KeepOnly(IEnumerable<IReadOnlySet<Guid>> used)
    {
        ArgumentNullException.ThrowIfNull(used);
        var keys = used.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
        lock (_sync)
        {
            foreach (var key in _sets.Keys.Where(k => !keys.Contains(k)).ToList())
            {
                _sets.Remove(key);
            }
        }
    }

    /// <summary>The index sets known now (tests).</summary>
    internal int SetCount
    {
        get
        {
            lock (_sync)
            {
                return _sets.Count;
            }
        }
    }

    /// <summary>Rebuilds every changed index now.</summary>
    public void RebuildChanged()
    {
        List<TagSet> dirty;
        lock (_sync)
        {
            dirty = [.. _sets.Values.Where(s => s.Dirty)];
            foreach (var set in dirty)
            {
                set.Dirty = false;
            }
        }

        var deviation = Deviation;
        foreach (var set in dirty)
        {
            if (deviation > 0)
            {
                var index = DeletionIndex.Build(_source.TextsWithAnyTag(set.Tags), deviation);
                lock (_sync)
                {
                    set.Index = index;
                }
            }
        }
    }

    private DeletionIndex? IndexOf(IReadOnlySet<Guid> tags, int deviation)
    {
        var key = KeyOf(tags);
        TagSet set;
        lock (_sync)
        {
            if (!_sets.TryGetValue(key, out set!))
            {
                _sets[key] = set = new TagSet([.. tags]);
            }

            if (set.Index is { } ready && ready.Deviation == deviation)
            {
                return ready;
            }
        }

        // The first read of a tag set (or after the deviation changed) builds its index here.
        var built = DeletionIndex.Build(_source.TextsWithAnyTag(set.Tags), deviation);
        lock (_sync)
        {
            if (set.Index is null || set.Index.Deviation != deviation)
            {
                set.Index = built;
            }

            return set.Index;
        }
    }

    private void ScheduleRebuild()
    {
        if (RebuildInline)
        {
            RebuildChanged();
            return;
        }

        if (Interlocked.Exchange(ref _rebuildScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            Volatile.Write(ref _rebuildScheduled, 0);
            try
            {
                RebuildChanged();
            }
#pragma warning disable CA1031 // A failed rebuild keeps the previous index; the next change tries again.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        });
    }

    internal static string KeyOf(IReadOnlySet<Guid> tags) => string.Join(",", tags.Order().Select(t => t.ToString("N")));

    private sealed class TagSet(HashSet<Guid> tags)
    {
        public HashSet<Guid> Tags { get; } = tags;

        public DeletionIndex? Index { get; set; }

        public bool Dirty { get; set; }
    }
}
