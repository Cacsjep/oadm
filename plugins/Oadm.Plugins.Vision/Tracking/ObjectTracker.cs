using Oadm.Plugins.Vision.Geometry;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tracking;

/// <summary>The settings of one Track node.</summary>
/// <param name="ConfirmFrames">Frames in a row an object must be seen before it gets its id.</param>
/// <param name="KeepLost">How long a lost object keeps its id, by frame time.</param>
public sealed record TrackerSettings(int ConfirmFrames, TimeSpan KeepLost)
{
    public static readonly TrackerSettings Default = new(3, TimeSpan.FromSeconds(1));
}

/// <summary>
/// The tracker of a Track node (docs/spec/vision-center.md "Nodes"), ByteTrack-like: a constant-velocity Kalman filter per
/// object, then two rounds of IoU matching per class: the detections with a high score against every track, then the
/// ones with a low score against the tracks still unmatched. An object gets its id (1, 2, ... per tracker) once it was
/// seen <see cref="TrackerSettings.ConfirmFrames"/> frames in a row; a lost one keeps it for
/// <see cref="TrackerSettings.KeepLost"/>. At most <see cref="VisionLimits.MaxObjects"/> tracks. One frame at a time, in
/// order; the time comes with the frame.
/// </summary>
public sealed class ObjectTracker
{
    /// <summary>A detection at or above this score is a high one (round 1, may start a track).</summary>
    public const double HighScore = 0.5;

    /// <summary>The least IoU of a match in round 1 (ByteTrack's 0.8 distance).</summary>
    public const double FirstRoundIoU = 0.2;

    /// <summary>The least IoU of a match in round 2 (ByteTrack's 0.5 distance).</summary>
    public const double SecondRoundIoU = 0.5;

    /// <summary>Bottom centres kept per object.</summary>
    public const int TrailLength = 12;

    private readonly TrackerSettings _settings;
    private readonly List<Track> _tracks = [];
    private readonly List<Candidate> _pairs = [];
    private int _nextId = 1;
    private DateTimeOffset? _last;

    public ObjectTracker(TrackerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings with { ConfirmFrames = Math.Max(1, settings.ConfirmFrames) };
    }

    /// <summary>The tracks held now (tentative, confirmed and lost).</summary>
    public int TrackCount => _tracks.Count;

    /// <summary>
    /// Takes one frame's objects (highest score first) and gives the confirmed ones seen in it, in their input order, each
    /// with its id and trail. A frame older than the last one is ignored (nothing comes back).
    /// </summary>
    public IReadOnlyList<VisionObject> Update(IReadOnlyList<VisionObject> objects, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (_last is { } last && at < last)
        {
            return [];
        }

        var seconds = _last is { } l ? (at - l).TotalSeconds : 0;
        _last = at;

        foreach (var t in _tracks)
        {
            t.Filter.Predict(seconds);
            t.Predicted = t.Filter.Box;
            t.Matched = -1;
        }

        var count = Math.Min(objects.Count, VisionLimits.MaxObjects);
        var matchOf = new Track?[count];

        // Round 1: high detections against every track.
        Match(objects, count, matchOf, high: true, FirstRoundIoU, _ => true);

        // Round 2: low detections against the confirmed tracks seen last frame and still unmatched.
        Match(objects, count, matchOf, high: false, SecondRoundIoU, t => t.Confirmed && !t.Lost);

        // Matched tracks take their detection.
        for (var d = 0; d < count; d++)
        {
            if (matchOf[d] is not { } t)
            {
                continue;
            }

            var box = objects[d].Detection.Box;
            t.Filter.Update(box);
            t.Hits++;
            t.LastSeen = at;
            t.Lost = false;
            t.AddTrail(box.BottomCentre);
            if (!t.Confirmed && t.Hits >= _settings.ConfirmFrames)
            {
                t.Confirmed = true;
                t.Id = _nextId++;
            }
        }

        // Unmatched tracks: tentative ones end, confirmed ones are lost until they are kept too long.
        for (var i = _tracks.Count - 1; i >= 0; i--)
        {
            var t = _tracks[i];
            if (t.Matched >= 0)
            {
                continue;
            }

            if (!t.Confirmed || at - t.LastSeen > _settings.KeepLost)
            {
                _tracks.RemoveAt(i);
            }
            else
            {
                t.Lost = true;
            }
        }

        // Unmatched high detections start tracks while there is room (lost tracks make room first, the oldest first).
        for (var d = 0; d < count; d++)
        {
            var o = objects[d];
            if (matchOf[d] is not null || o.Detection.Score < HighScore)
            {
                continue;
            }

            if (_tracks.Count >= VisionLimits.MaxObjects && !DropOldestLost())
            {
                continue;
            }

            var t = new Track(new BoxFilter(o.Detection.Box), o.Detection.ClassId) { Hits = 1, LastSeen = at, Matched = int.MaxValue };
            t.AddTrail(o.Detection.Box.BottomCentre);
            if (_settings.ConfirmFrames <= 1)
            {
                t.Confirmed = true;
                t.Id = _nextId++;
            }

            _tracks.Add(t);
            matchOf[d] = t;
        }

        var result = new List<VisionObject>(count);
        for (var d = 0; d < count; d++)
        {
            if (matchOf[d] is not { Confirmed: true } t)
            {
                continue;
            }

            result.Add(objects[d] with { TrackId = t.Id, Trail = t.TrailSnapshot() });
        }

        return result;
    }

    /// <summary>Forgets every track; ids start again at 1.</summary>
    public void Clear()
    {
        _tracks.Clear();
        _nextId = 1;
        _last = null;
    }

    private void Match(IReadOnlyList<VisionObject> objects, int count, Track?[] matchOf, bool high, double minIoU, Func<Track, bool> eligible)
    {
        _pairs.Clear();
        for (var ti = 0; ti < _tracks.Count; ti++)
        {
            var t = _tracks[ti];
            if (t.Matched >= 0 || !eligible(t))
            {
                continue;
            }

            var p = t.Predicted;
            for (var d = 0; d < count; d++)
            {
                if (matchOf[d] is not null)
                {
                    continue;
                }

                var det = objects[d].Detection;
                if ((det.Score >= HighScore) != high || det.ClassId != t.ClassId)
                {
                    continue;
                }

                var b = det.Box;
                var iou = VisionGeometry.IoU(p.X, p.Y, p.Right, p.Bottom, b.X, b.Y, b.Right, b.Bottom);
                if (iou >= minIoU)
                {
                    _pairs.Add(new Candidate(iou, ti, d));
                }
            }
        }

        // Greedy by the best IoU first; ties by track, then detection order, so a run is deterministic.
        _pairs.Sort(static (a, b) =>
        {
            var c = b.IoU.CompareTo(a.IoU);
            if (c != 0)
            {
                return c;
            }

            c = a.Track.CompareTo(b.Track);
            return c != 0 ? c : a.Detection.CompareTo(b.Detection);
        });

        foreach (var pair in _pairs)
        {
            var t = _tracks[pair.Track];
            if (t.Matched >= 0 || matchOf[pair.Detection] is not null)
            {
                continue;
            }

            t.Matched = pair.Detection;
            matchOf[pair.Detection] = t;
        }
    }

    private bool DropOldestLost()
    {
        var oldest = -1;
        for (var i = 0; i < _tracks.Count; i++)
        {
            var t = _tracks[i];
            if (t.Lost && t.Matched < 0 && (oldest < 0 || t.LastSeen < _tracks[oldest].LastSeen))
            {
                oldest = i;
            }
        }

        if (oldest < 0)
        {
            return false;
        }

        _tracks.RemoveAt(oldest);
        return true;
    }

    private readonly record struct Candidate(double IoU, int Track, int Detection);

    private sealed class Track(BoxFilter filter, int classId)
    {
        private readonly RelativePoint[] _trail = new RelativePoint[TrailLength];
        private int _trailCount;
        private int _trailStart;

        public BoxFilter Filter { get; } = filter;

        public int ClassId { get; } = classId;

        public RelativeBox Predicted { get; set; } = filter.Box;

        /// <summary>The detection it took this frame; -1: none yet.</summary>
        public int Matched { get; set; } = -1;

        public int Hits { get; set; }

        public bool Confirmed { get; set; }

        public bool Lost { get; set; }

        public int Id { get; set; }

        public DateTimeOffset LastSeen { get; set; }

        public void AddTrail(RelativePoint p)
        {
            if (_trailCount < TrailLength)
            {
                _trail[(_trailStart + _trailCount++) % TrailLength] = p;
            }
            else
            {
                _trail[_trailStart] = p;
                _trailStart = (_trailStart + 1) % TrailLength;
            }
        }

        public RelativePoint[] TrailSnapshot()
        {
            var copy = new RelativePoint[_trailCount];
            for (var i = 0; i < _trailCount; i++)
            {
                copy[i] = _trail[(_trailStart + i) % TrailLength];
            }

            return copy;
        }
    }
}
