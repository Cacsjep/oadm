using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Sdk.Events;

namespace Oadm.Plugins.Vision.Events;

/// <summary>
/// Hands the Event nodes' emissions of the saved pipelines to the event rules (never a preview's). On and off types get one
/// full report (<see cref="IEventPublisher.ReplaceStates"/>, their "ready") when their pipeline starts or cannot run, then
/// only edges; a pipeline that stops turns its types off. Every member may run beside the others.
/// </summary>
internal sealed class VisionEventSink(IEventPublisher? publisher)
{
    private readonly Lock _sync = new();

    /// <summary>The on and off types reported so far, with whether they are on.</summary>
    private readonly Dictionary<string, bool> _states = new(StringComparer.Ordinal);

    /// <summary>The emissions published so far (tests wait on it).</summary>
    public int Published => Volatile.Read(ref _published);

    private int _published;

    /// <summary>Publishes the emissions of one frame or run of the saved <paramref name="pipeline"/>.</summary>
    public void Publish(Pipeline pipeline, IReadOnlyList<Emission> emissions)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(emissions);
        if (publisher is null || emissions.Count == 0)
        {
            return;
        }

        foreach (var emission in emissions)
        {
            var type = VisionInfo.EventTypeId(pipeline.Id, emission.NodeId, emission.Kind);
            switch (emission.State)
            {
                case EmissionState.Fired:
                    publisher.Fire(type, pipeline.DeviceId, emission.Values);
                    break;
                case EmissionState.On:
                case EmissionState.Off:
                    var on = emission.State == EmissionState.On;
                    lock (_sync)
                    {
                        if (!_states.ContainsKey(type))
                        {
                            // An edge before the full report: the report first, so the type is ready.
                            publisher.ReplaceStates(type, []);
                        }

                        _states[type] = on;
                    }

                    publisher.SetState(type, pipeline.DeviceId, on, emission.Values);
                    break;
            }

            Interlocked.Increment(ref _published);
        }
    }

    /// <summary>
    /// The full report of the on and off types <paramref name="types"/> of <paramref name="pipeline"/>: on as
    /// <paramref name="current"/> says (the evaluator's states after a start: none), the rest off. Sent again only when a type
    /// changed since its last report.
    /// </summary>
    public void Report(Pipeline pipeline, IReadOnlyList<string> types, IReadOnlyList<Emission> current)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(current);
        if (publisher is null)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var type in types)
            {
                var on = current.FirstOrDefault(e => e.State == EmissionState.On && VisionInfo.EventTypeId(pipeline.Id, e.NodeId, e.Kind) == type);
                if (_states.TryGetValue(type, out var was) && was == (on is not null) && on is null)
                {
                    continue;
                }

                _states[type] = on is not null;
                if (on is not null)
                {
                    publisher.SetState(type, pipeline.DeviceId, true, on.Values);
                }

                publisher.ReplaceStates(type, on is not null ? [pipeline.DeviceId] : []);
            }
        }
    }

    /// <summary>Forgets types that are gone (a deleted pipeline or node): a later type of the same id reports again.</summary>
    public void Keep(IReadOnlySet<string> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        lock (_sync)
        {
            foreach (var gone in _states.Keys.Where(k => !types.Contains(k)).ToList())
            {
                _states.Remove(gone);
            }
        }
    }
}

/// <summary>
/// Tells the event rules of the pipelines' problems (as Text recognition's reader problems): one full report per type first,
/// then only changes. <see cref="PipelineRules.Starting"/> is no problem: the graphics card's test only delays the pipelines.
/// </summary>
internal sealed class PipelineProblemReporter(IEventPublisher publisher)
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, (bool On, string Text, Guid? Device)> _reported = new(StringComparer.Ordinal);

    public void Report(IReadOnlyList<Pipeline> pipelines, Func<string, string?> problemOf)
    {
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(problemOf);
        lock (_sync)
        {
            var failing = new List<string>();
            var waiting = false;
            foreach (var pipeline in pipelines)
            {
                var problem = problemOf(pipeline.Id);
                if (problem == PipelineRules.Starting)
                {
                    waiting = true;
                    continue;
                }

                if (problem is not null)
                {
                    failing.Add(pipeline.Name);
                }

                Set(PipelineProblemEvents.TypeIdOf(pipeline.Id), pipeline.DeviceId, pipeline.Name + "\u001f" + problem, problem is null ? null : PipelineProblemEvents.Data(pipeline, problem), problem is not null);
            }

            if (!waiting)
            {
                Set(VisionInfo.AnyPipelineProblemTypeId, null, string.Join('\u001f', failing), failing.Count == 0 ? null : PipelineProblemEvents.AnyData(failing), failing.Count > 0);
            }

            var current = pipelines.Select(p => PipelineProblemEvents.TypeIdOf(p.Id)).Append(VisionInfo.AnyPipelineProblemTypeId).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _reported.Keys.Where(k => !current.Contains(k)).ToList())
            {
                _reported.Remove(gone);
            }
        }
    }

    private void Set(string typeId, Guid? deviceId, string text, IReadOnlyDictionary<string, string>? data, bool on)
    {
        var first = !_reported.TryGetValue(typeId, out var last);
        var moved = !first && last.Device != deviceId;
        if (!first && !moved && last.On == on && (!on || last.Text == text))
        {
            return;
        }

        _reported[typeId] = (on, text, deviceId);
        if (on)
        {
            publisher.SetState(typeId, deviceId, true, data);
        }

        if (first || moved || !on)
        {
            publisher.ReplaceStates(typeId, on ? [deviceId ?? Guid.Empty] : []);
        }
    }
}
