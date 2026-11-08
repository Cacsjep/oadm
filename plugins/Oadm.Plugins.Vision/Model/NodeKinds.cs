namespace Oadm.Plugins.Vision.Model;

/// <summary>What a link carries (docs/spec/vision-center.md "Typed links").</summary>
public enum PortKind
{
    Picture = 0,

    /// <summary>Boxes with class, score and attributes, without ids.</summary>
    Objects = 1,

    /// <summary>Objects after a Track node: each keeps an id while it moves.</summary>
    TrackedObjects = 2,

    /// <summary>Objects that crossed a line, with their direction.</summary>
    Crossings = 3,

    /// <summary>A live value.</summary>
    Number = 4,

    /// <summary>Yes or no: whether a Compare node's condition holds.</summary>
    Condition = 5,

    /// <summary>A Timer node's pulse: goes only into a control input (Count's Reset, Event's Send now).</summary>
    Pulse = 6,
}

/// <summary>The type of a node setting; the page draws its field from it, the server checks it.</summary>
public enum NodeFieldType
{
    Text = 0,
    Number = 1,
    Choice = 2,

    /// <summary>The id of a stored model of <see cref="NodeFieldInfo.ModelKind"/>.</summary>
    Model = 3,

    /// <summary>Class names of the node's model (or of the model before it); an empty list means every class.</summary>
    Classes = 4,

    /// <summary>A polygon or line drawn on the video: a list of <see cref="RelativePoint"/>.</summary>
    Shape = 5,

    /// <summary>A time of day "HH:mm".</summary>
    TimeOfDay = 6,

    Bool = 7,
}

/// <summary>A choice of a <see cref="NodeFieldType.Choice"/> field.</summary>
public sealed record NodeChoice(string Value, string Label);

/// <summary>
/// One setting a node kind declares (like an event rule action's fields): the page draws the form from these, the server
/// checks them, so a node kind added later needs no editor change.
/// </summary>
public sealed record NodeFieldInfo
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required NodeFieldType Type { get; init; }

    /// <summary>"Optional" in the form; a required field without a value is an error below it.</summary>
    public bool Optional { get; init; }

    /// <summary>The default as JSON text ("50", "\"bottom\"", "[]"); null: none.</summary>
    public string? Default { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>A unit shown after the number ("%", "s", "frames").</summary>
    public string? Unit { get; init; }

    public IReadOnlyList<NodeChoice> Choices { get; init; } = [];

    /// <summary>For <see cref="NodeFieldType.Model"/>: which models it offers.</summary>
    public ModelKind? ModelKind { get; init; }

    /// <summary>For <see cref="NodeFieldType.Shape"/>: the fewest and most points.</summary>
    public int MinPoints { get; init; }

    public int MaxPoints { get; init; }

    /// <summary>Shown only when the node's input is one of these kinds; empty: always.</summary>
    public IReadOnlyList<PortKind> OnlyForInput { get; init; } = [];

    /// <summary>One short hint below the field; null: none.</summary>
    public string? Hint { get; init; }

    /// <summary>For a <see cref="NodeFieldType.Number"/> with <see cref="Min"/> and <see cref="Max"/>: a slider with the number beside it.</summary>
    public bool Slider { get; init; }

    /// <summary>Shown only while the field <see cref="VisibleWhenKey"/> holds one of <see cref="VisibleWhenValues"/>; null: always.</summary>
    public string? VisibleWhenKey { get; init; }

    public IReadOnlyList<string> VisibleWhenValues { get; init; } = [];
}

/// <summary>A node kind: what it takes and gives, its settings, where the node list shows it.</summary>
public sealed record NodeKindInfo
{
    public required string Id { get; init; }

    /// <summary>The name in the node list and the default node name ("Detect objects").</summary>
    public required string Label { get; init; }

    /// <summary>The node list's group: Models, Objects, Geometry, Output; null: not in the list (Camera).</summary>
    public string? Group { get; init; }

    /// <summary>One line in the node list.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The input kinds it takes; empty: no input (Camera).</summary>
    public IReadOnlyList<PortKind> Accepts { get; init; } = [];

    /// <summary>
    /// What its output carries for a given input kind (null input for Camera); null result: no output (Event).
    /// </summary>
    public required Func<PortKind?, PortKind?> Output { get; init; }

    /// <summary>Works only while Watching (Track, Line, Count on crossings).</summary>
    public bool NeedsStream { get; init; }

    /// <summary>Runs a model (Detect objects, Classify): counts against <see cref="VisionLimits.MaxModelNodesPerPath"/>.</summary>
    public bool RunsModel { get; init; }

    /// <summary>Gets the eye toggle (draws on the video).</summary>
    public bool Draws { get; init; } = true;

    public IReadOnlyList<NodeFieldInfo> Fields { get; init; } = [];

    /// <summary>Whether it takes <paramref name="input"/>.</summary>
    public bool Takes(PortKind input) => Accepts.Contains(input);

    /// <summary>
    /// Extra inputs beside the data input (decision 2026-11-08): each takes only a Timer's <see cref="PortKind.Pulse"/>, at most one
    /// link each. Count: "reset"; Event: "send".
    /// </summary>
    public IReadOnlyList<ControlInput> ControlInputs { get; init; } = [];
}

/// <summary>
/// The node kinds of this version (docs/spec/vision-center.md "Nodes"). One catalogue for the server's check, the runtime
/// and the page. Settings keys are camelCase and part of the stored format: never rename one.
/// </summary>
public static class NodeKinds
{
    public const string Camera = "camera";
    public const string Detect = "detect";
    public const string Classify = "classify";
    public const string Filter = "filter";
    public const string Track = "track";
    public const string Area = "area";
    public const string Line = "line";
    public const string Count = "count";
    public const string Compare = "compare";
    public const string Timer = "timer";
    public const string Event = "event";

    // Settings keys.
    public const string ModelKey = "model";
    public const string MinScoreKey = "minScore";
    public const string ClassesKey = "classes";
    public const string MinSizeKey = "minSize";
    public const string MaxSizeKey = "maxSize";
    public const string LabelKey = "label";
    public const string ConfirmKey = "confirmFrames";
    public const string KeepLostKey = "keepLostSeconds";
    public const string PointsKey = "points";
    public const string AnchorKey = "anchor";
    public const string DirectionsKey = "directions";
    public const string ResetAtKey = "resetAt";
    public const string MinObjectsKey = "minObjects";
    public const string AtLeastKey = "atLeastSeconds";
    public const string OffAfterKey = "offAfterSeconds";
    public const string LimitKey = "limit";
    public const string OperatorKey = "operator";
    public const string ValueKey = "value";
    public const string Value2Key = "value2";
    public const string EveryKey = "every";
    public const string MinutesKey = "minutes";
    public const string AtKey = "at";
    public const string DayKey = "day";
    public const string ResetPort = "reset";
    public const string SendPort = "send";

    public const string AnchorBottom = "bottom";
    public const string AnchorCentre = "centre";
    public const string AnchorOverlap = "overlap";
    public const string DirectionsBoth = "both";
    public const string DirectionsIn = "in";
    public const string DirectionsOut = "out";
    public const string OpGreater = "greater";
    public const string OpAtLeast = "atLeast";
    public const string OpEqual = "equal";
    public const string OpAtMost = "atMost";
    public const string OpLess = "less";
    public const string OpBetween = "between";
    public const string EveryMinutes = "minutes";
    public const string EveryHour = "hour";
    public const string EveryDay = "day";
    public const string EveryWeek = "week";

    private static readonly PortKind[] ObjectKinds = [PortKind.Objects, PortKind.TrackedObjects];

    /// <summary>Objects stay tracked or untracked through a node that only passes or marks them.</summary>
    private static PortKind? Same(PortKind? input) => input;

    private static NodeFieldInfo MinScore(string def) => new()
    {
        Key = MinScoreKey, Label = "Minimum score", Type = NodeFieldType.Number, Default = def, Min = 1, Max = 99, Unit = "%", Slider = true,
    };

    private static NodeFieldInfo Classes(string label = "Classes") => new()
    {
        Key = ClassesKey, Label = label, Type = NodeFieldType.Classes, Default = "[]", Optional = true,
    };

    public static readonly NodeKindInfo CameraKind = new()
    {
        Id = Camera, Label = "Camera", Draws = false, Output = _ => PortKind.Picture,
    };

    public static readonly NodeKindInfo DetectKind = new()
    {
        Id = Detect, Label = "Detect objects", Group = "Models", Description = "Boxes from a detection model",
        Accepts = [PortKind.Picture], Output = _ => PortKind.Objects, RunsModel = true,
        Fields =
        [
            new() { Key = ModelKey, Label = "Model", Type = NodeFieldType.Model, ModelKind = Model.ModelKind.Detection },
            MinScore("50"),
            Classes(),
        ],
    };

    public static readonly NodeKindInfo ClassifyKind = new()
    {
        Id = Classify, Label = "Classify", Group = "Models", Description = "A class for each object",
        Accepts = [PortKind.Picture, .. ObjectKinds], Output = Same, RunsModel = true,
        Fields =
        [
            new() { Key = ModelKey, Label = "Model", Type = NodeFieldType.Model, ModelKind = Model.ModelKind.Classification },
            new()
            {
                Key = PointsKey, Label = "Area", Type = NodeFieldType.Shape, Optional = true, MinPoints = 3, MaxPoints = 16,
                OnlyForInput = [PortKind.Picture], Hint = "None: the whole picture.",
            },
        ],
    };

    public static readonly NodeKindInfo FilterKind = new()
    {
        Id = Filter, Label = "Filter", Group = "Objects", Description = "Keep objects by class, score or size",
        Accepts = ObjectKinds, Output = Same,
        Fields =
        [
            Classes(),
            MinScore("50"),
            new() { Key = MinSizeKey, Label = "Minimum size", Type = NodeFieldType.Number, Optional = true, Min = 0, Max = 100, Unit = "%", Slider = true, Hint = "Share of the picture's height." },
            new() { Key = MaxSizeKey, Label = "Maximum size", Type = NodeFieldType.Number, Optional = true, Min = 0, Max = 100, Unit = "%", Slider = true },
            new() { Key = LabelKey, Label = "Class of Classify", Type = NodeFieldType.Text, Optional = true },
        ],
    };

    public static readonly NodeKindInfo TrackKind = new()
    {
        Id = Track, Label = "Track", Group = "Objects", Description = "An id that stays while it moves",
        Accepts = ObjectKinds, Output = _ => PortKind.TrackedObjects, NeedsStream = true,
        Fields =
        [
            new() { Key = ConfirmKey, Label = "Confirm after", Type = NodeFieldType.Number, Default = "3", Min = 1, Max = 30, Unit = "frames", Slider = true },
            new() { Key = KeepLostKey, Label = "Keep lost objects", Type = NodeFieldType.Number, Default = "1", Min = 0, Max = 30, Unit = "s", Slider = true },
        ],
    };

    public static readonly NodeKindInfo AreaKind = new()
    {
        Id = Area, Label = "Area", Group = "Geometry", Description = "Objects inside a drawn area",
        Accepts = ObjectKinds, Output = Same,
        Fields =
        [
            new() { Key = PointsKey, Label = "Area", Type = NodeFieldType.Shape, MinPoints = 3, MaxPoints = 16, Hint = "Drag the corners on the video." },
            new()
            {
                Key = AnchorKey, Label = "Inside by", Type = NodeFieldType.Choice, Default = "\"bottom\"",
                Choices = [new(AnchorBottom, "Bottom centre"), new(AnchorCentre, "Centre"), new(AnchorOverlap, "Any overlap")],
            },
        ],
    };

    public static readonly NodeKindInfo LineKind = new()
    {
        Id = Line, Label = "Line", Group = "Geometry", Description = "Objects that cross a drawn line",
        Accepts = ObjectKinds, Output = _ => PortKind.Crossings, NeedsStream = true,
        Fields =
        [
            new() { Key = PointsKey, Label = "Line", Type = NodeFieldType.Shape, MinPoints = 2, MaxPoints = 8, Hint = "Drag the ends on the video." },
            new()
            {
                Key = DirectionsKey, Label = "Count", Type = NodeFieldType.Choice, Default = "\"both\"",
                Choices = [new(DirectionsBoth, "In and Out"), new(DirectionsIn, "In only"), new(DirectionsOut, "Out only")],
            },
        ],
    };

    public static readonly NodeKindInfo CountKind = new()
    {
        Id = Count, Label = "Count", Group = "Output", Description = "A live number",
        ControlInputs = [new(ResetPort, "Reset")],
        Accepts = [.. ObjectKinds, PortKind.Crossings], Output = _ => PortKind.Number,
        Fields =
        [
            new() { Key = ResetAtKey, Label = "Reset daily at", Type = NodeFieldType.TimeOfDay, Optional = true, OnlyForInput = [PortKind.Crossings] }, // goes with the Timer node (graph stage)
        ],
    };

    public static readonly NodeKindInfo TimerKind = new()
    {
        Id = Timer, Label = "Timer", Group = "Time", Description = "A pulse on a schedule, server time",
        Accepts = [], Output = _ => PortKind.Pulse, Draws = false, NeedsStream = true,
        Fields =
        [
            new()
            {
                Key = EveryKey, Label = "Every", Type = NodeFieldType.Choice, Default = "\"day\"",
                Choices = [new(EveryMinutes, "Few minutes"), new(EveryHour, "Hour"), new(EveryDay, "Day"), new(EveryWeek, "Week")],
            },
            new() { Key = MinutesKey, Label = "Minutes", Type = NodeFieldType.Number, Default = "15", Min = 1, Max = 59, Unit = "min", Slider = true, VisibleWhenKey = EveryKey, VisibleWhenValues = [EveryMinutes] },
            new() { Key = AtKey, Label = "At", Type = NodeFieldType.TimeOfDay, Default = "\"00:00\"", VisibleWhenKey = EveryKey, VisibleWhenValues = [EveryDay, EveryWeek] },
            new() { Key = DayKey, Label = "Day", Type = NodeFieldType.Choice, Default = "\"mon\"", Choices = [new("mon", "Monday"), new("tue", "Tuesday"), new("wed", "Wednesday"), new("thu", "Thursday"), new("fri", "Friday"), new("sat", "Saturday"), new("sun", "Sunday")], VisibleWhenKey = EveryKey, VisibleWhenValues = [EveryWeek] },
        ],
    };

    public static readonly NodeKindInfo CompareKind = new()
    {
        Id = Compare, Label = "Compare", Group = "Logic", Description = "Yes while a number meets a condition",
        Accepts = [PortKind.Number], Output = _ => PortKind.Condition, Draws = false,
        Fields =
        [
            new()
            {
                Key = OperatorKey, Label = "Condition", Type = NodeFieldType.Choice, Default = "\"greater\"",
                Choices = [new(OpGreater, "Greater than"), new(OpAtLeast, "At least"), new(OpEqual, "Equal to"), new(OpAtMost, "At most"), new(OpLess, "Less than"), new(OpBetween, "Between")],
            },
            new() { Key = ValueKey, Label = "Value", Type = NodeFieldType.Number, Default = "10", Min = -1_000_000, Max = 1_000_000 },
            new() { Key = Value2Key, Label = "And", Type = NodeFieldType.Number, Default = "20", Min = -1_000_000, Max = 1_000_000, VisibleWhenKey = OperatorKey, VisibleWhenValues = [OpBetween] },
        ],
    };

    public static readonly NodeKindInfo EventKind = new()
    {
        Id = Event, Label = "Event", Group = "Output", Description = "An event for the event rules",
        ControlInputs = [new(SendPort, "Send now")],
        Accepts = [.. ObjectKinds, PortKind.Crossings, PortKind.Number, PortKind.Condition], Output = _ => null, Draws = false,
        Fields =
        [
            new() { Key = MinObjectsKey, Label = "Minimum objects", Type = NodeFieldType.Number, Default = "1", Min = 1, Max = 300, OnlyForInput = ObjectKinds },
            new() { Key = AtLeastKey, Label = "At least", Type = NodeFieldType.Number, Default = "1", Min = 0, Max = 3600, Unit = "s", OnlyForInput = [.. ObjectKinds, PortKind.Condition] },
            new() { Key = OffAfterKey, Label = "Off after", Type = NodeFieldType.Number, Default = "2", Min = 0, Max = 3600, Unit = "s", OnlyForInput = [.. ObjectKinds, PortKind.Condition] },
            new() { Key = LimitKey, Label = "Limit", Type = NodeFieldType.Number, Optional = true, Min = 0, Max = 1_000_000, OnlyForInput = [PortKind.Number] },
        ],
    };

    /// <summary>Every kind, in the order of the node list.</summary>
    public static readonly IReadOnlyList<NodeKindInfo> All =
        [CameraKind, DetectKind, ClassifyKind, FilterKind, TrackKind, AreaKind, LineKind, CountKind, CompareKind, TimerKind, EventKind];

    /// <summary>The kind of <paramref name="id"/>; null for a kind this version does not know.</summary>
    public static NodeKindInfo? Find(string? id) => All.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal));
}

/// <summary>A control input of a node: its port id and its label on the canvas.</summary>
public sealed record ControlInput(string Port, string Label);
