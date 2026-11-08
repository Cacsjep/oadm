using System.Globalization;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Nodes;

/// <summary>
/// Event (docs/spec/vision-center.md "Events"): what it publishes follows its input. Objects: the stateful Objects present
/// (Minimum objects, At least, Off after), with tracked objects also Object entered and Object left; Crossings: Line
/// crossed per crossing; Number: Count changed (the value, or In or Out of crossings) at most once per
/// <see cref="VisionLimits.CountChangedEvery"/> with the latest values, and the stateful Above limit. In On action mode Objects gives Objects found or No objects found once per run.
/// </summary>
internal sealed class EventNode : VisionNode
{
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    private readonly PipelineMode _mode;
    private readonly int _minObjects;
    private readonly TimeSpan _atLeast;
    private readonly TimeSpan _offAfter;
    private readonly double? _limit;
    private readonly TimeSpan _keepLost;
    private readonly bool _numberHasInOut;

    // Objects present.
    private bool _presentOn;
    private DateTimeOffset? _trueSince;
    private DateTimeOffset? _falseSince;
    private IReadOnlyDictionary<string, string> _presentValues = NoValues;

    // Object entered and left.
    private readonly Dictionary<int, Seen> _seen = [];
    private readonly HashSet<int> _now = [];

    // Count changed and Above limit.
    private long _lastCount;
    private long _lastIn;
    private long _lastOut;
    private DateTimeOffset? _lastCountAt;
    private bool _aboveOn;
    private IReadOnlyDictionary<string, string> _aboveValues = NoValues;

    private DateTimeOffset? _lastFired;

    public EventNode(PipelineNode node, PortKind? input, PipelineMode mode, TimeSpan keepLost, bool numberHasInOut)
        : base(node, NodeKinds.EventKind, input)
    {
        _mode = mode;
        _minObjects = (int)Math.Max(1, Math.Round(NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.MinObjectsKey)) ?? 1));
        _atLeast = TimeSpan.FromSeconds(Math.Max(0, NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.AtLeastKey)) ?? 1));
        _offAfter = TimeSpan.FromSeconds(Math.Max(0, NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.OffAfterKey)) ?? 2));
        _limit = NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.LimitKey));
        _keepLost = keepLost;
        _numberHasInOut = numberHasInOut;
    }

    /// <summary>Whether it has a state that can be on (Objects present, Above limit).</summary>
    private bool Stateful => (_mode == PipelineMode.Watching && Input is PortKind.Objects or PortKind.TrackedObjects)
        || (Input == PortKind.Number && _limit is not null);

    /// <summary>Its kinds that are on now, with their last values.</summary>
    public IEnumerable<Emission> OnStates()
    {
        if (_presentOn)
        {
            yield return new Emission(Id, VisionEventKind.ObjectsPresent, EmissionState.On, _presentValues);
        }

        if (_aboveOn)
        {
            yield return new Emission(Id, VisionEventKind.AboveLimit, EmissionState.On, _aboveValues);
        }
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        var at = frame.At;
        var emissions = frame.Emissions;
        var before = emissions.Count;
        switch (Input)
        {
            case PortKind.Objects or PortKind.TrackedObjects when _mode == PipelineMode.OnAction:
                Found(input.Objects, emissions);
                break;

            case PortKind.Objects or PortKind.TrackedObjects:
                Present(input.Objects, at, emissions);
                if (Input == PortKind.TrackedObjects)
                {
                    EnteredLeft(input.Objects, at, emissions);
                }

                break;

            case PortKind.Crossings:
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

            case PortKind.Number:
                Number(input, at, emissions);
                break;
        }

        for (var i = before; i < emissions.Count; i++)
        {
            if (emissions[i].State != EmissionState.Off)
            {
                _lastFired = at;
                break;
            }
        }

        var on = _presentOn || _aboveOn;
        var live = on ? "On"
            : _lastFired is { } fired ? LiveText.FiredAgo(fired, at)
            : Stateful ? "Off"
            : string.Empty;
        return ValueTask.FromResult(new NodeOutput { NodeId = Id, On = Stateful ? on : null, Live = live });
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

    private void Present(IReadOnlyList<VisionObject> objects, DateTimeOffset at, List<Emission> emissions)
    {
        var condition = objects.Count >= _minObjects;
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

        if (!_presentOn && condition && at - _trueSince!.Value >= _atLeast)
        {
            _presentOn = true;
            _presentValues = CountAndClasses(objects);
            emissions.Add(new Emission(Id, VisionEventKind.ObjectsPresent, EmissionState.On, _presentValues));
        }
        else if (_presentOn && !condition && at - _falseSince!.Value >= _offAfter)
        {
            _presentOn = false;
            _presentValues = CountAndClasses(objects);
            emissions.Add(new Emission(Id, VisionEventKind.ObjectsPresent, EmissionState.Off, _presentValues));
        }
        else if (_presentOn)
        {
            // While on, the values follow without an edge (CurrentStates reports them).
            _presentValues = CountAndClasses(objects);
        }
    }

    private void EnteredLeft(IReadOnlyList<VisionObject> objects, DateTimeOffset at, List<Emission> emissions)
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
            emissions.Add(new Emission(Id, VisionEventKind.ObjectEntered, EmissionState.Fired, new Dictionary<string, string>
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
            emissions.Add(new Emission(Id, VisionEventKind.ObjectLeft, EmissionState.Fired, new Dictionary<string, string>
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

        if (_limit is not { } limit)
        {
            return;
        }

        var above = value >= limit;
        if (above != _aboveOn)
        {
            _aboveOn = above;
            _aboveValues = NumberValues(input, value);
            emissions.Add(new Emission(Id, VisionEventKind.AboveLimit, above ? EmissionState.On : EmissionState.Off, _aboveValues));
        }
        else if (above)
        {
            _aboveValues = NumberValues(input, value);
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
