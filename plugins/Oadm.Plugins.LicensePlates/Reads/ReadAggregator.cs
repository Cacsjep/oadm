using Oadm.Plugins.LicensePlates.Matching;

namespace Oadm.Plugins.LicensePlates.Reads;

/// <summary>A read decided: OADM's one decision per car and camera, from <paramref name="Event"/>.</summary>
internal sealed record DecidedRead(long ReadId, Guid Camera, string CarId, DateTimeOffset Time, AlpvEvent Event, MatchResult Match, PlateDirection Direction, string? Image);

/// <summary>A later event of a decided car: the log row gets the new direction or a better picture; nothing fires.</summary>
internal sealed record ReadChange(long ReadId, Guid Camera, DateTimeOffset Time, PlateDirection? Direction, string? Image);

/// <summary>
/// From ALPV's events to reads (docs/spec/license-plate-center.md "From events to a read"): ALPV sends New, Update and Lost per
/// car (one <c>carID</c>). New or Update with an exact match decides at once; otherwise the events are collected and the read
/// is decided at Lost or when the wait for the best read ends (from the first event), from the event with the highest
/// <c>lpConfidence</c>. A decided car's later events only change its log row. The entry of a car is dropped at Lost or
/// 60 seconds after its last event, so a camera that never sends Lost loses nothing. Time is a <see cref="TimeProvider"/>.
/// Thread safe; the callbacks run outside the lock, in the order of the decisions.
/// </summary>
internal sealed class ReadAggregator : IDisposable
{
    private readonly TimeProvider _time;
    private readonly Func<Guid, string, MatchResult> _match;
    private readonly Func<TimeSpan> _wait;
    private readonly Action<DecidedRead> _decided;
    private readonly Action<ReadChange> _changed;
    private readonly Func<long> _nextId;
    private readonly Lock _sync = new();
    private readonly Dictionary<(Guid Camera, string CarId), Car> _cars = [];
    private bool _disposed;

    public ReadAggregator(TimeProvider time, Func<Guid, string, MatchResult> match, Func<TimeSpan> wait, Action<DecidedRead> decided, Action<ReadChange> changed, Func<long> nextId)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _match = match ?? throw new ArgumentNullException(nameof(match));
        _wait = wait ?? throw new ArgumentNullException(nameof(wait));
        _decided = decided ?? throw new ArgumentNullException(nameof(decided));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _nextId = nextId ?? throw new ArgumentNullException(nameof(nextId));
    }

    /// <summary>The cars being followed now.</summary>
    public int Active
    {
        get
        {
            lock (_sync)
            {
                return _cars.Count;
            }
        }
    }

    /// <summary>An event of ALPV on <paramref name="camera"/>.</summary>
    public void Handle(Guid camera, AlpvEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var after = new List<Action>(1);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var key = (camera, e.CarId);
            var now = _time.GetUtcNow();
            if (!_cars.TryGetValue(key, out var car))
            {
                if (e.Kind == AlpvEventKind.Lost && e.Text.Length == 0)
                {
                    return; // a Lost of a car never seen (it came before OADM connected)
                }

                car = new Car(key, now);
                car.Timer = _time.CreateTimer(OnTimer, key, _wait(), Timeout.InfiniteTimeSpan);
                _cars[key] = car;
            }

            car.Last = now;
            if (e.Direction != PlateDirection.Unknown)
            {
                car.Direction = e.Direction;
            }

            if (e.Image is not null && (car.ImageEvent is null || e.Confidence > car.ImageEvent.Confidence))
            {
                car.ImageEvent = e;
            }

            if (car.Decided is { } decided)
            {
                var direction = car.Direction != decided.Direction ? car.Direction : (PlateDirection?)null;
                var image = car.ImageEvent is { } better && !ReferenceEquals(better, decided.ImageEvent) ? better : null;
                if (direction is not null || image is not null)
                {
                    car.Decided = decided with { Direction = car.Direction, ImageEvent = car.ImageEvent };
                    var change = new ReadChange(decided.ReadId, camera, now, direction, image?.Image);
                    after.Add(() => _changed(change));
                }
            }
            else
            {
                if (e.Text.Length > 0 && (car.Best is null || e.Confidence > car.Best.Confidence))
                {
                    car.Best = e;
                }

                if (e.Text.Length > 0 && e.Kind != AlpvEventKind.Lost && _match(camera, e.Text) is { Exact: true } exact)
                {
                    after.Add(Decide(car, e, exact, now));
                }
                else if (e.Kind == AlpvEventKind.Lost)
                {
                    if (car.Best is { } best)
                    {
                        after.Add(Decide(car, best, _match(camera, best.Text), now));
                    }
                }
            }

            if (e.Kind == AlpvEventKind.Lost)
            {
                Drop(car);
            }
            else if (car.Decided is not null)
            {
                car.Timer?.Change(LicensePlateLimits.CarTimeout, Timeout.InfiniteTimeSpan);
            }
        }

        foreach (var action in after)
        {
            action();
        }
    }

    /// <summary>The wait for the best read ended (decide), or a decided car was quiet for 60 s (drop).</summary>
    private void OnTimer(object? state)
    {
        var key = ((Guid Camera, string CarId))state!;
        var after = new List<Action>(1);
        lock (_sync)
        {
            if (_disposed || !_cars.TryGetValue(key, out var car))
            {
                return;
            }

            var now = _time.GetUtcNow();
            if (car.Decided is null)
            {
                if (car.Best is { } best)
                {
                    after.Add(Decide(car, best, _match(key.Camera, best.Text), now));
                }
                else if (now - car.Last >= LicensePlateLimits.CarTimeout)
                {
                    Drop(car); // only events without text: nothing to decide
                    return;
                }
            }

            var quiet = now - car.Last;
            if (car.Decided is not null && quiet >= LicensePlateLimits.CarTimeout)
            {
                Drop(car);
            }
            else
            {
                var left = LicensePlateLimits.CarTimeout - quiet;
                car.Timer?.Change(left > TimeSpan.Zero ? left : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }

        foreach (var action in after)
        {
            action();
        }
    }

    private Action Decide(Car car, AlpvEvent e, MatchResult match, DateTimeOffset now)
    {
        var direction = e.Direction != PlateDirection.Unknown ? e.Direction : car.Direction;
        var image = e.Image is not null ? e : car.ImageEvent;
        car.ImageEvent = image;
        var read = new DecidedRead(_nextId(), car.Key.Camera, car.Key.CarId, e.Captured ?? now, e, match, direction, image?.Image);
        car.Decided = new Decision(read.ReadId, direction, image);
        car.Direction = direction;
        return () => _decided(read);
    }

    private void Drop(Car car)
    {
        car.Timer?.Dispose();
        car.Timer = null;
        _cars.Remove(car.Key);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            foreach (var car in _cars.Values)
            {
                car.Timer?.Dispose();
            }

            _cars.Clear();
        }
    }

    private sealed record Decision(long ReadId, PlateDirection Direction, AlpvEvent? ImageEvent);

    private sealed class Car((Guid Camera, string CarId) key, DateTimeOffset first)
    {
        public (Guid Camera, string CarId) Key { get; } = key;

        public DateTimeOffset First { get; } = first;

        public DateTimeOffset Last { get; set; } = first;

        public AlpvEvent? Best { get; set; }

        public AlpvEvent? ImageEvent { get; set; }

        public PlateDirection Direction { get; set; }

        public Decision? Decided { get; set; }

        public ITimer? Timer { get; set; }
    }
}
