using System.Globalization;

using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Nodes;

/// <summary>Compare: yes while the number meets its condition (Between includes both bounds).</summary>
internal sealed class CompareNode : VisionNode
{
    private readonly string _operator;
    private readonly double _value;
    private readonly double _value2;

    public CompareNode(PipelineNode node, PortKind? input) : base(node, NodeKinds.CompareKind, input)
    {
        _operator = NodeSettings.Text(VisionGraph.Setting(node, NodeKinds.OperatorKey)) ?? NodeKinds.OpGreater;
        _value = NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.ValueKey)) ?? 10;
        _value2 = NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.Value2Key)) ?? 20;
    }

    public bool Holds(double v) => _operator switch
    {
        NodeKinds.OpAtLeast => v >= _value,
        NodeKinds.OpEqual => v == _value,
        NodeKinds.OpAtMost => v <= _value,
        NodeKinds.OpLess => v < _value,
        NodeKinds.OpBetween => v >= _value && v <= _value2,
        _ => v > _value,
    };

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        var value = input.Value ?? 0;
        var holds = Holds(value);
        return ValueTask.FromResult(new NodeOutput
        {
            NodeId = Id,
            Kind = PortKind.Condition,
            Value = value,
            In = input.In,
            Out = input.Out,
            On = holds,
            Live = $"{Text(value)} · {LiveText.YesNo(holds)}",
        });
    }

    /// <summary>"14 > 10", "10 ≤ 14 ≤ 20".</summary>
    private string Text(long value)
    {
        var v = LiveText.Number(value);
        var a = N(_value);
        return _operator switch
        {
            NodeKinds.OpAtLeast => $"{v} ≥ {a}",
            NodeKinds.OpEqual => $"{v} = {a}",
            NodeKinds.OpAtMost => $"{v} ≤ {a}",
            NodeKinds.OpLess => $"{v} < {a}",
            NodeKinds.OpBetween => $"{a} ≤ {v} ≤ {N(_value2)}",
            _ => $"{v} > {a}",
        };
    }

    private static string N(double d) => d.ToString("#,0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// Timer: a pulse on its schedule (every few minutes on the minutes of the hour Minutes divides, every hour at :00, every
/// day At, every week on Day At) in the server's local time, from the frame times only. A pulse is taken at the first
/// frame at or after its time, at most one per frame; times missed meanwhile are not repeated. The first pulse is the
/// first due time after the first frame.
/// </summary>
internal sealed class TimerNode : VisionNode
{
    private readonly string _every;
    private readonly int _minutes;
    private readonly TimeOnly _at;
    private readonly DayOfWeek _day;
    private DateTimeOffset? _next;

    public TimerNode(PipelineNode node) : base(node, NodeKinds.TimerKind, null)
    {
        _every = NodeSettings.Text(VisionGraph.Setting(node, NodeKinds.EveryKey)) ?? NodeKinds.EveryDay;
        _minutes = (int)Math.Clamp(Math.Round(NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.MinutesKey)) ?? 15), 1, 59);
        _at = NodeSettings.TimeOfDay(VisionGraph.Setting(node, NodeKinds.AtKey)) ?? TimeOnly.MinValue;
        _day = NodeSettings.Day(NodeSettings.Text(VisionGraph.Setting(node, NodeKinds.DayKey))) ?? DayOfWeek.Monday;
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var at = frame.At;
        var pulse = false;
        if (_next is not { } next)
        {
            _next = NextAfter(at, frame.Zone);
        }
        else if (at >= next)
        {
            pulse = true;
            _next = NextAfter(at, frame.Zone);
        }

        var local = TimeZoneInfo.ConvertTime(_next.Value, frame.Zone);
        var live = "Next " + local.ToString(_every == NodeKinds.EveryWeek ? "ddd HH:mm" : "HH:mm", CultureInfo.InvariantCulture);
        return ValueTask.FromResult(new NodeOutput { NodeId = Id, Kind = PortKind.Pulse, On = pulse, Live = live });
    }

    /// <summary>The first due time after <paramref name="at"/>.</summary>
    public DateTimeOffset NextAfter(DateTimeOffset at, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        var hour = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified);
        DateTime start;
        TimeSpan step;
        switch (_every)
        {
            case NodeKinds.EveryMinutes:
                start = hour.AddHours(-1);
                step = TimeSpan.FromMinutes(1);
                break;
            case NodeKinds.EveryHour:
                start = hour.AddHours(-1);
                step = TimeSpan.FromHours(1);
                break;
            case NodeKinds.EveryWeek:
                var back = ((int)local.DayOfWeek - (int)_day + 7) % 7;
                start = local.Date.AddDays(-back - 7) + _at.ToTimeSpan();
                step = TimeSpan.FromDays(7);
                break;
            default:
                start = local.Date.AddDays(-1) + _at.ToTimeSpan();
                step = TimeSpan.FromDays(1);
                break;
        }

        // Wall-clock candidates from a little before the frame, each turned into an instant; the first after the frame wins.
        for (var candidate = start; candidate < local.AddDays(15); candidate += step)
        {
            if (_every == NodeKinds.EveryMinutes && candidate.Minute % _minutes != 0)
            {
                continue;
            }

            if (LocalTimes.Instant(candidate, zone) is var instant && instant > at)
            {
                return instant;
            }
        }

        return at.AddDays(1);
    }
}

/// <summary>Turns the server's local wall times into instants.</summary>
internal static class LocalTimes
{
    /// <summary>The instant of a local wall time: a time a daylight saving change skips is the first valid one after it; a repeated one is its first.</summary>
    public static DateTimeOffset Instant(DateTime local, TimeZoneInfo zone)
    {
        var t = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var i = 0; i < 24 * 60 && zone.IsInvalidTime(t); i++)
        {
            t = t.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(t) ? zone.GetAmbiguousTimeOffsets(t).Max() : zone.GetUtcOffset(t);
        return new DateTimeOffset(t, offset);
    }
}

/// <summary>
/// Time window: yes from From until To on the chosen days (none: every day), the server's local time at the frame. A
/// window over midnight belongs to the day it starts; From equal to To is the whole day.
/// </summary>
internal sealed class TimeWindowNode : VisionNode
{
    private readonly HashSet<DayOfWeek>? _days;
    private readonly TimeOnly _from;
    private readonly TimeOnly _to;
    private DateTime _changeAt = DateTime.MinValue;
    private bool _yes;
    private string _live = string.Empty;

    public TimeWindowNode(PipelineNode node) : base(node, NodeKinds.TimeWindowKind, null)
    {
        _days = NodeSettings.Days(VisionGraph.Setting(node, NodeKinds.DaysKey)) is { Count: > 0 } days ? [.. days] : null;
        _from = NodeSettings.TimeOfDay(VisionGraph.Setting(node, NodeKinds.FromKey)) ?? new TimeOnly(8, 0);
        _to = NodeSettings.TimeOfDay(VisionGraph.Setting(node, NodeKinds.ToKey)) ?? new TimeOnly(18, 0);
    }

    /// <summary>Whether the local wall time <paramref name="local"/> is inside the window.</summary>
    public bool Inside(DateTime local)
    {
        var t = TimeOnly.FromDateTime(local);
        if (_from == _to)
        {
            return DayOn(local.DayOfWeek);
        }

        if (_from < _to)
        {
            return DayOn(local.DayOfWeek) && t >= _from && t < _to;
        }

        return (t >= _from && DayOn(local.DayOfWeek)) || (t < _to && DayOn(local.AddDays(-1).DayOfWeek));
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var local = frame.Local;
        if (local >= _changeAt || local < _changeAt.AddDays(-8))
        {
            // The next change is looked up once and kept until it comes (or the clock went back).
            _yes = Inside(local);
            (_changeAt, _live) = Until(local, _yes);
        }

        return ValueTask.FromResult(new NodeOutput { NodeId = Id, Kind = PortKind.Condition, On = _yes, Live = _live });
    }

    /// <summary>"Yes until 18:00", "No until 08:00", "No until Mon 08:00" (more than a day ahead); minutes are enough.</summary>
    private (DateTime At, string Live) Until(DateTime local, bool yes)
    {
        var minute = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
        for (var t = minute.AddMinutes(1); t < minute.AddDays(8); t = t.AddMinutes(1))
        {
            if (Inside(t) != yes)
            {
                var when = t - local > TimeSpan.FromDays(1)
                    ? t.ToString("ddd HH:mm", CultureInfo.InvariantCulture)
                    : t.ToString("HH:mm", CultureInfo.InvariantCulture);
                return (t, $"{LiveText.YesNo(yes)} until {when}");
            }
        }

        return (minute.AddDays(1), LiveText.YesNo(yes));
    }

    private bool DayOn(DayOfWeek day) => _days is null || _days.Contains(day);
}

/// <summary>Device event: yes while the chosen event of the event rules is on (<see cref="IEventStates"/>).</summary>
internal sealed class DeviceEventNode : VisionNode
{
    private readonly (string Type, Guid? Device)? _event;

    public DeviceEventNode(PipelineNode node) : base(node, NodeKinds.DeviceEventKind, null) =>
        _event = NodeSettings.EventOf(VisionGraph.Setting(node, NodeKinds.EventKey));

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var yes = _event is { } e && frame.EventStates?.IsOn(e.Type, e.Device) == true;
        return ValueTask.FromResult(new NodeOutput { NodeId = Id, Kind = PortKind.Condition, On = yes, Live = LiveText.YesNo(yes) });
    }
}

/// <summary>Hold: yes once its input said yes for Yes after, no once it said no for No after (live "Yes", "Yes in 3 s").</summary>
internal sealed class HoldNode : VisionNode
{
    private readonly TimeSpan _onAfter;
    private readonly TimeSpan _offAfter;
    private readonly EdgeState _state = new();

    public HoldNode(PipelineNode node, PortKind? input) : base(node, NodeKinds.HoldKind, input)
    {
        _onAfter = TimeSpan.FromSeconds(Math.Max(0, NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.OnAfterKey)) ?? 5));
        _offAfter = TimeSpan.FromSeconds(Math.Max(0, NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.OffAfterKey)) ?? 5));
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(frame);
        var condition = input.On == true;
        _state.Step(condition, frame.At, _onAfter, _offAfter);
        var live = _state.Left(condition, frame.At, _onAfter, _offAfter) is { } left
            ? $"{LiveText.YesNo(!_state.On)} {LiveText.InSeconds(left)}"
            : LiveText.YesNo(_state.On);
        return ValueTask.FromResult(new NodeOutput
        {
            NodeId = Id,
            Kind = PortKind.Condition,
            Value = input.Value,
            In = input.In,
            Out = input.Out,
            On = _state.On,
            Live = live,
        });
    }
}

/// <summary>Throttle: passes at most one crossing or pulse per At most one per; the others are dropped.</summary>
internal sealed class ThrottleNode : VisionNode
{
    private readonly TimeSpan _every;
    private DateTimeOffset? _lastPassed;
    private long _passed;
    private long _held;

    public ThrottleNode(PipelineNode node, PortKind? input) : base(node, NodeKinds.ThrottleKind, input) =>
        _every = TimeSpan.FromSeconds(Math.Max(1, NodeSettings.Number(VisionGraph.Setting(node, NodeKinds.EveryNSecondsKey)) ?? 60));

    private bool Take(DateTimeOffset at)
    {
        if (_lastPassed is { } last && at - last < _every)
        {
            _held++;
            return false;
        }

        _lastPassed = at;
        _passed++;
        return true;
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(frame);
        if (Input == PortKind.Pulse)
        {
            var pass = input.On == true && Take(frame.At);
            return ValueTask.FromResult(new NodeOutput { NodeId = Id, Kind = PortKind.Pulse, On = pass, Live = Live() });
        }

        List<Crossing>? passed = null;
        foreach (var c in input.Crossings)
        {
            if (Take(frame.At))
            {
                (passed ??= []).Add(c);
            }
        }

        return ValueTask.FromResult(new NodeOutput
        {
            NodeId = Id,
            Kind = PortKind.Crossings,
            Crossings = (IReadOnlyList<Crossing>?)passed ?? [],
            In = input.In,
            Out = input.Out,
            Live = Live(),
        });
    }

    /// <summary>"1 passed · 4 held back".</summary>
    private string Live() => $"{LiveText.Number(_passed)} passed · {LiveText.Number(_held)} held back";
}

/// <summary>Statistics: the highest, lowest, average or sum of a number over the frames since the last Reset pulse or the start.</summary>
internal sealed class StatisticsNode : VisionNode
{
    private readonly string _function;
    private long _frames;
    private double _sum;
    private double _max = double.MinValue;
    private double _min = double.MaxValue;

    public StatisticsNode(PipelineNode node, PortKind? input) : base(node, NodeKinds.StatisticsKind, input) =>
        _function = NodeSettings.Text(VisionGraph.Setting(node, NodeKinds.FunctionKey)) ?? NodeKinds.FnMax;

    public void Reset()
    {
        _frames = 0;
        _sum = 0;
        _max = double.MinValue;
        _min = double.MaxValue;
    }

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(frame);
        double v = input.Value ?? 0;
        _frames++;
        _sum += v;
        _max = Math.Max(_max, v);
        _min = Math.Min(_min, v);
        var (label, value) = _function switch
        {
            NodeKinds.FnMin => ("Lowest", _min),
            NodeKinds.FnAverage => ("Average", _sum / _frames),
            NodeKinds.FnSum => ("Sum", _sum),
            _ => ("Highest", _max),
        };
        var output = new NodeOutput
        {
            NodeId = Id,
            Kind = PortKind.Number,
            Value = (long)Math.Round(value, MidpointRounding.AwayFromZero),
            Live = $"{label} {value.ToString("#,0.#", CultureInfo.InvariantCulture)}",
        };
        if (Pulsed(frame, NodeKinds.ResetPort))
        {
            Reset();
        }

        return ValueTask.FromResult(output);
    }
}

/// <summary>And / Or: its input and its Second input, both or either yes.</summary>
internal sealed class LogicNode : VisionNode
{
    private readonly bool _and;

    public LogicNode(PipelineNode node, PortKind? input) : base(node, NodeKinds.LogicKind, input) =>
        _and = NodeSettings.Text(VisionGraph.Setting(node, NodeKinds.OperatorKey)) != NodeKinds.OpOr;

    public override ValueTask<NodeOutput> RunAsync(NodeOutput? input, FrameContext frame)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(frame);
        var a = input.On == true;
        var b = Control(frame, NodeKinds.SecondPort)?.On == true;
        var yes = _and ? a && b : a || b;
        return ValueTask.FromResult(new NodeOutput { NodeId = Id, Kind = PortKind.Condition, On = yes, Value = input.Value, Live = LiveText.YesNo(yes) });
    }
}
