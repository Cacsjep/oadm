using System.Globalization;

using Oadm.Plugins.Shared;
using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision.Events;

/// <summary>
/// The rule events of the Event nodes (docs/spec/vision-center.md "Events and the rule actions"): per Event node one type per
/// kind its input gives, in the folder Vision Center / pipeline name, on the pipeline's camera (<see cref="EventTypeInfo.FixedDeviceId"/>),
/// owned by the pipeline's owner. The first kind of a node carries the node's name; the others add what they tell
/// ("Gate: object left"). Types change with the pipelines: the plugin raises <c>EventTypesChanged</c>.
/// </summary>
public static class VisionEventTypes
{
    /// <summary>The kinds an Event node publishes for its input; Above limit only with a limit set.</summary>
    public static IReadOnlyList<VisionEventKind> KindsOf(PortKind input, PipelineMode mode, bool hasLimit) => input switch
    {
        PortKind.Objects or PortKind.TrackedObjects when mode == PipelineMode.OnAction => [VisionEventKind.ObjectsFound, VisionEventKind.NoObjectsFound],
        PortKind.Objects => [VisionEventKind.ObjectsPresent],
        PortKind.TrackedObjects => [VisionEventKind.ObjectsPresent, VisionEventKind.ObjectEntered, VisionEventKind.ObjectLeft],
        PortKind.Crossings => [VisionEventKind.LineCrossed],
        PortKind.Number => hasLimit ? [VisionEventKind.CountChanged, VisionEventKind.AboveLimit] : [VisionEventKind.CountChanged],
        _ => [],
    };

    /// <summary>On and off kinds; the others fire once.</summary>
    public static bool IsStateful(VisionEventKind kind) => kind is VisionEventKind.ObjectsPresent or VisionEventKind.AboveLimit;

    /// <summary>The event name: the node's name for its first kind, else the node's name and what the kind tells.</summary>
    public static string NameOf(string nodeName, VisionEventKind kind) => kind switch
    {
        VisionEventKind.ObjectEntered => nodeName + ": object entered",
        VisionEventKind.ObjectLeft => nodeName + ": object left",
        VisionEventKind.NoObjectsFound => nodeName + ": no objects found",
        VisionEventKind.AboveLimit => nodeName + ": above limit",
        _ => nodeName,
    };

    /// <summary>The folders of a pipeline's events in the rule dialog.</summary>
    public static IReadOnlyList<string> PathOf(string pipelineName) =>
        [VisionInfo.EventGroup, string.IsNullOrWhiteSpace(pipelineName) ? "Pipeline" : pipelineName];

    /// <summary>The Event nodes of <paramref name="pipeline"/> with each of their kinds (their input from <paramref name="check"/>).</summary>
    public static IEnumerable<(PipelineNode Node, VisionEventKind Kind)> EventsOf(Pipeline pipeline, GraphCheck check)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(check);
        foreach (var node in pipeline.Nodes.Where(n => n.Kind == NodeKinds.Event))
        {
            if (!check.InputKinds.TryGetValue(node.Id, out var input))
            {
                continue;
            }

            var hasLimit = Pipelines.PipelineRules.Number(node, NodeKinds.LimitKey) is not null;
            foreach (var kind in KindsOf(input, pipeline.Mode, hasLimit))
            {
                yield return (node, kind);
            }
        }
    }

    /// <summary>The event types of every Event node of <paramref name="pipeline"/>.</summary>
    public static IEnumerable<EventTypeInfo> TypesOf(Pipeline pipeline, GraphCheck check) =>
        EventsOf(pipeline, check).Select(e => TypeOf(pipeline, e.Node, e.Kind));

    /// <summary>The ids of the on and off types of <paramref name="pipeline"/>.</summary>
    public static IReadOnlyList<string> StatefulIdsOf(Pipeline pipeline, GraphCheck check) =>
        [.. EventsOf(pipeline, check).Where(e => IsStateful(e.Kind)).Select(e => VisionInfo.EventTypeId(pipeline.Id, e.Node.Id, e.Kind))];

    public static EventTypeInfo TypeOf(Pipeline pipeline, PipelineNode node, VisionEventKind kind)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(node);
        var name = NameOf(node.Name, kind);
        return new EventTypeInfo(
            VisionInfo.EventTypeId(pipeline.Id, node.Id, kind),
            name,
            VisionInfo.EventGroup,
            IsStateful(kind) ? EventKind.Stateful : EventKind.Stateless,
            EventScope.Device,
            FieldsOf(kind))
        {
            Description = DescriptionOf(pipeline.Name, node.Name, kind),
            DeviceIds = [pipeline.DeviceId],
            FixedDeviceId = pipeline.DeviceId,
            OwnerScopeKey = OwnerScope.Of(pipeline.OwnerScopeKey),
            Path = PathOf(pipeline.Name),
        };
    }

    /// <summary>The values of each kind (docs/spec/vision-center.md "Events").</summary>
    public static IReadOnlyList<EventField> FieldsOf(VisionEventKind kind) => kind switch
    {
        VisionEventKind.ObjectsPresent or VisionEventKind.ObjectsFound => [Count, Classes],
        VisionEventKind.NoObjectsFound => [],
        VisionEventKind.ObjectEntered or VisionEventKind.ObjectLeft => [Class, ObjectId, TimeInside],
        VisionEventKind.LineCrossed => [Direction, Class, ObjectId, Score],
        VisionEventKind.CountChanged => [Count, In, Out],
        VisionEventKind.AboveLimit => [Count],
        _ => [],
    };

    /// <summary>The label of an event value (the page's table of what an Event node sent).</summary>
    public static string LabelOf(string field) => field switch
    {
        VisionInfo.CountField => "Count",
        VisionInfo.ClassesField => "Classes",
        VisionInfo.ClassField => "Class",
        VisionInfo.ObjectField => "Object",
        VisionInfo.ScoreField => "Score",
        VisionInfo.DirectionField => "Direction",
        VisionInfo.TimeInsideField => "Time inside",
        VisionInfo.InField => "In",
        VisionInfo.OutField => "Out",
        _ => field,
    };

    private static string DescriptionOf(string pipeline, string node, VisionEventKind kind) => kind switch
    {
        VisionEventKind.ObjectsPresent => $"Objects are at {node} of the pipeline {pipeline}.",
        VisionEventKind.ObjectsFound => $"Run pipeline found objects at {node} of the pipeline {pipeline}.",
        VisionEventKind.NoObjectsFound => $"Run pipeline found no objects at {node} of the pipeline {pipeline}.",
        VisionEventKind.ObjectEntered => $"An object came to {node} of the pipeline {pipeline}.",
        VisionEventKind.ObjectLeft => $"An object left {node} of the pipeline {pipeline}.",
        VisionEventKind.LineCrossed => $"An object crossed the line at {node} of the pipeline {pipeline}.",
        VisionEventKind.CountChanged => $"The count at {node} of the pipeline {pipeline} changed.",
        VisionEventKind.AboveLimit => $"The count at {node} of the pipeline {pipeline} is at its limit or above.",
        _ => string.Empty,
    };

    private static readonly EventField Count = new(VisionInfo.CountField, "Count", EventFieldType.Number) { Kind = EventValueKind.Number };
    private static readonly EventField Classes = new(VisionInfo.ClassesField, "Classes") { Kind = EventValueKind.Text };
    private static readonly EventField Class = new(VisionInfo.ClassField, "Class") { Kind = EventValueKind.Text };
    private static readonly EventField ObjectId = new(VisionInfo.ObjectField, "Object") { Kind = EventValueKind.Text };
    private static readonly EventField TimeInside = new(VisionInfo.TimeInsideField, "Time inside", EventFieldType.Number) { Kind = EventValueKind.Number };
    private static readonly EventField Direction = new(VisionInfo.DirectionField, "Direction", EventFieldType.Choice, ["In", "Out"]) { Kind = EventValueKind.Text };
    private static readonly EventField Score = new(VisionInfo.ScoreField, "Score", EventFieldType.Number) { Kind = EventValueKind.Number };
    private static readonly EventField In = new(VisionInfo.InField, "In", EventFieldType.Number) { Kind = EventValueKind.Number };
    private static readonly EventField Out = new(VisionInfo.OutField, "Out", EventFieldType.Number) { Kind = EventValueKind.Number };
}

/// <summary>"Pipeline problem" of each pipeline and "Any pipeline problem" (as Reader problem of Text recognition).</summary>
public static class PipelineProblemEvents
{
    public static string TypeIdOf(string pipelineId) => VisionInfo.PipelineProblemTypePrefix + pipelineId;

    public static EventTypeInfo TypeOf(Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return new EventTypeInfo(
            TypeIdOf(pipeline.Id),
            VisionInfo.PipelineProblemName,
            VisionInfo.EventGroup,
            EventKind.Stateful,
            EventScope.Device,
            [
                new EventField(VisionInfo.PipelineField, "Pipeline") { Filter = false },
                new EventField(VisionInfo.ProblemField, "Problem"),
            ])
        {
            Description = $"The pipeline {pipeline.Name} cannot run.",
            DeviceIds = [pipeline.DeviceId],
            FixedDeviceId = pipeline.DeviceId,
            OwnerScopeKey = OwnerScope.Of(pipeline.OwnerScopeKey),
            Path = VisionEventTypes.PathOf(pipeline.Name),
        };
    }

    /// <summary>One state for the server; it names pipelines of every owner, so it is for users with all devices.</summary>
    public static EventTypeInfo AnyType { get; } = new(
        VisionInfo.AnyPipelineProblemTypeId,
        VisionInfo.AnyPipelineProblemName,
        VisionInfo.EventGroup,
        EventKind.Stateful,
        EventScope.Server,
        [
            new EventField(VisionInfo.CountField, "Pipelines", EventFieldType.Number) { Kind = EventValueKind.Number, Filter = false },
            new EventField(VisionInfo.PipelineField, "Pipeline") { Filter = false },
        ])
    {
        Description = "At least one vision pipeline cannot run.",
        OwnerScopeKey = ICallContext.AllDevicesScopeKey,
    };

    public static IReadOnlyDictionary<string, string> Data(Pipeline pipeline, string problem)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VisionInfo.PipelineField] = pipeline.Name,
            [VisionInfo.ProblemField] = problem,
        };
    }

    public static IReadOnlyDictionary<string, string> AnyData(IReadOnlyCollection<string> pipelines)
    {
        ArgumentNullException.ThrowIfNull(pipelines);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VisionInfo.CountField] = pipelines.Count.ToString(CultureInfo.InvariantCulture),
            [VisionInfo.PipelineField] = string.Join(", ", pipelines),
        };
    }
}
