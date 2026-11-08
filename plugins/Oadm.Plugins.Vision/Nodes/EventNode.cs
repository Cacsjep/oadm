using System.Globalization;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Nodes;

/// <summary>On and off with a time to become on and a time to become off again (Objects present, Condition holds).</summary>
internal sealed class EdgeState
{
    private DateTimeOffset? _trueSince;
    private DateTimeOffset? _falseSince;

    public bool On { get; private set; }

    /// <summary>Takes this frame's condition; +1 when it went on, -1 when it went off, else 0.</summary>
    public int Step(bool condition, DateTimeOffset at, TimeSpan onAfter, TimeSpan offAfter)
    {
        if (condition)
        {
            _trueSince ??= at;
            _falseSince = null;
        }
        else
        {
            _falseSince ??= at;
            _trueSince = null;
        }

        if (!On && condition && at - _trueSince!.Value >= onAfter)
        {
            On = true;
            return 1;
        }

        if (On && !condition && at - _falseSince!.Value >= offAfter)
        {
            On = false;
            return -1;
        }

        return 0;
    }

    /// <summary>How long until the state changes while the condition stays as it is; null: it will not.</summary>
    public TimeSpan? Left(bool condition, DateTimeOffset at, TimeSpan onAfter, TimeSpan offAfter) =>
        condition && !On && _trueSince is { } t ? onAfter - (at - t)
        : !condition && On && _falseSince is { } f ? offAfter - (at - f)
        : null;
}

/// <summary>
/// Event (docs/spec/vision-center.md "Events"): what it publishes follows its input. Objects: the stateful Objects present
/// (Minimum objects, At least, Off after), with tracked objects also Object entered and Object left; Crossings: Line
/// crossed per crossing; Number: Count changed (the value, or In or Out of crossings) at most once per
/// <see cref="VisionLimits.CountChangedEvery"/> with the latest values; Condition: the stateful Condition holds. In On
/// action mode Objects gives Objects found or No objects found once per run. A pulse into Send now gives a Report with the
/// input's current values. Only while no: the stateful kinds count it as "not there", nothing stateless is sent.
/// </summary>
internal sealed class EventNode : VisionNode
{
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    private readonly PipelineMode _mode;
    private readonly int _minObjects;
    private readonly TimeSpan _atLeast;
    private readonly TimeSpan _offAfter;
    private readonly TimeSpan _keepLost;
    private readonly bool _numberHasInOut;

    private readonly EdgeState _state = new();
    private IReadOnlyDictionary<string, string> _stateValues = NoValues;

    // Object entered and left.
    private readonly Dictionary<int, Seen> _seen = [];
    private readonly HashSet<int> _now = [];

    // Count changed.
    private long _lastCount;
    private long _lastIn;
    private long _lastOut;
    private DateTimeOffset? _lastCountAt;

    private DateTimeOffset? _lastFired;

    public EventNode(PipelineNode node, PortKind? input, PipelineMode mode, TimeSpan keepLost, bool numberHasInOut)
        : base(node, NodeKinds.EventKind, input)
    {
        _mode = mode;
        _minObjects = (int)Math.Max(1, Math.Round(NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.MinObjectsKey)) ?? 1));
        _atLeast = TimeSpan.FromSeconds(Math.Max(0, NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.AtLeastKey)) ?? 1));
        _offAfter = TimeSpan.FromSeconds(Math.Max(0, NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.OffAfterKey)) ?? 2));
        _keepLost = keepLost;
        _numberHasInOut = numberHasInOut;
    }

    /// <summary>The stateful kind it has; null: none.</summary>
    private VisionEventKind? StatefulKind => Input switch
    {
        PortKind.Objects or PortKind.TrackedObjects when _mode == PipelineMode.Watching => VisionEventKind.ObjectsPresent,
        PortKind.Condition => VisionEventKind.ConditionHolds,
        _ => null,
    };

    /// <summary>Its kinds that are on now, with their last values.</summary>
    public IEnumerable<Emission> OnStates()
    {
        if (_state.On && StatefulKind is { } kind)
        {
            yield return new Emission(Id, kind, EmissionState.On, _stateValues);
        }
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(frame);
        var at = frame.At;
        var emissions = frame.Emissions;
        var before = emissions.Count;
        var sending = While(frame);
        switch (Input)
        {
            case PortKind.Objects or PortKind.TrackedObjects when _mode == PipelineMode.OnAction:
                if (sending)
                {
                    Found(input.Objects, emissions);
                }

                break;

            case PortKind.Objects or PortKind.TrackedObjects:
                Edge(VisionEventKind.ObjectsPresent, sending && input.Objects.Count >= _minObjects, CountAndClasses(input.Objects), at, emissions);
                if (Input == PortKind.TrackedObjects)
                {
                    EnteredLeft(input.Objects, at, sending ? emissions : null);
                }

                break;

            case PortKind.Crossings when sending:
                foreach (var c in input.Crossings)
                {
                    emissions.Add(new Emission(Id, VisionEventKind.LineCrossed, EmissionState.Fired, new Dictionary<string, string>
                    {
                        [VisionInfo.DirectionField] = c.Direction.ToString(),
                        [VisionInfo.ClassField] = c.ClassName,
                        [VisionInfo.ObjectField] = Text(c.TrackId),
                        [VisionInfo.ScoreField] = LiveText.Score(c.Score),
                    }));
                }

                break;

            case PortKind.Number when sending:
                Number(input, at, emissions);
                break;

            case PortKind.Condition:
                Edge(VisionEventKind.ConditionHolds, sending && input.On == true, new Dictionary<string, string>
                {
                    [VisionInfo.CountField] = Text(input.Value ?? 0),
                }, at, emissions);
                break;
        }

        if (sending && Pulsed(frame, NodeKinds.SendPort))
        {
            emissions.Add(new Emission(Id, VisionEventKind.Report, EmissionState.Fired, ReportValues(input)));
        }

        for (var i = before; i < emissions.Count; i++)
        {
            if (emissions[i].State != EmissionState.Off)
            {
                _lastFired = at;
                break;
            }
        }

        var stateful = StatefulKind is not null;
        var live = _state.On ? "On"
            : _lastFired is { } fired ? LiveText.FiredAgo(fired, at)
            : stateful ? "Off"
            : string.Empty;
        return ValueTask.FromResult(new NodeOutput { NodeId = Id, On = stateful ? _state.On : null, Live = live });
    }

    private void Edge(VisionEventKind kind, bool condition, IReadOnlyDictionary<string, string> values, DateTimeOffset at, List<Emission> emissions)
    {
        var edge = _state.Step(condition, at, _atLeast, _offAfter);
        if (edge != 0 || _state.On)
        {
            // While on, the values follow without an edge (CurrentStates reports them).
            _stateValues = values;
        }

        if (edge != 0)
        {
            emissions.Add(new Emission(Id, kind, edge > 0 ? EmissionState.On : EmissionState.Off, values));
        }
    }

    private void Found(IReadOnlyList<VisionObject> objects, List<Emission> emissions)
    {
        if (objects.Count >= _minObjects)
        {
            emissions.Add(new Emission(Id, VisionEventKind.ObjectsFound, EmissionState.Fired, CountAndClasses(objects)));
        }
        else
        {
            emissions.Add(new Emission(Id, VisionEventKind.NoObjectsFound, EmissionState.Fired, new Dictionary<string, string>
            {
                [VisionInfo.CountField] = Text(objects.Count),
            }));
        }
    }

    /// <summary>The input's current values for a Report.</summary>
    private Dictionary<string, string> ReportValues(NodeOutput input) => Input switch
    {
        PortKind.Objects or PortKind.TrackedObjects => CountAndClasses(input.Objects),
        PortKind.Crossings => new Dictionary<string, string>
        {
            [VisionInfo.InField] = Text(input.In),
            [VisionInfo.OutField] = Text(input.Out),
        },
        PortKind.Number => NumberValues(input, input.Value ?? 0),
        _ => new Dictionary<string, string> { [VisionInfo.CountField] = Text(input.Value ?? 0) },
    };

    private void EnteredLeft(IReadOnlyList<VisionObject> objects, DateTimeOffset at, List<Emission>? emissions)
    {
        _now.Clear();
        foreach (var o in objects)
        {
            if (o.TrackId is not { } id)
            {
                continue;
            }

            _now.Add(id);
            if (_seen.TryGetValue(id, out var seen))
            {
                _seen[id] = seen with { LastSeen = at, ClassName = o.Detection.ClassName, Inside = o.InsideSeconds };
                continue;
            }

            _seen[id] = new Seen(at, at, o.Detection.ClassName, o.InsideSeconds);
            emissions?.Add(new Emission(Id, VisionEventKind.ObjectEntered, EmissionState.Fired, new Dictionary<string, string>
            {
                [VisionInfo.ClassField] = o.Detection.ClassName,
                [VisionInfo.ObjectField] = Text(id),
                [VisionInfo.TimeInsideField] = LiveText.Seconds(o.InsideSeconds ?? 0),
            }));
        }

        List<int>? left = null;
        foreach (var (id, seen) in _seen)
        {
            if (!_now.Contains(id) && at - seen.LastSeen > _keepLost)
            {
                (left ??= []).Add(id);
            }
        }

        if (left is null)
        {
            return;
        }

        left.Sort();
        foreach (var id in left)
        {
            var seen = _seen[id];
            _seen.Remove(id);
            var inside = seen.Inside ?? (seen.LastSeen - seen.FirstSeen).TotalSeconds;
            emissions?.Add(new Emission(Id, VisionEventKind.ObjectLeft, EmissionState.Fired, new Dictionary<string, string>
            {
                [VisionInfo.ClassField] = seen.ClassName,
                [VisionInfo.ObjectField] = Text(id),
                [VisionInfo.TimeInsideField] = LiveText.Seconds(inside),
            }));
        }
    }

    private void Number(NodeOutput input, DateTimeOffset at, List<Emission> emissions)
    {
        var value = input.Value ?? 0;
        var changed = value != _lastCount || (_numberHasInOut && (input.In != _lastIn || input.Out != _lastOut));
        if (changed && (_lastCountAt is not { } last || at - last >= VisionLimits.CountChangedEvery))
        {
            _lastCount = value;
            _lastIn = input.In;
            _lastOut = input.Out;
            _lastCountAt = at;
            emissions.Add(new Emission(Id, VisionEventKind.CountChanged, EmissionState.Fired, NumberValues(input, value)));
        }
    }

    private Dictionary<string, string> NumberValues(NodeOutput input, long value)
    {
        var values = new Dictionary<string, string> { [VisionInfo.CountField] = Text(value) };
        if (_numberHasInOut)
        {
            values[VisionInfo.InField] = Text(input.In);
            values[VisionInfo.OutField] = Text(input.Out);
        }

        return values;
    }

    private static Dictionary<string, string> CountAndClasses(IReadOnlyList<VisionObject> objects)
    {
        var classes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var o in objects)
        {
            classes.Add(o.Detection.ClassName);
        }

        return new Dictionary<string, string>
        {
            [VisionInfo.CountField] = Text(objects.Count),
            [VisionInfo.ClassesField] = string.Join(' ', classes),
        };
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private readonly record struct Seen(DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string ClassName, double? Inside);
}
