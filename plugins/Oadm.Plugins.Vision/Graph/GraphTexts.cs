using System.Globalization;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Graph;

/// <summary>The texts of the graph check: shown directly below a node on the canvas, or below the canvas (CLAUDE.md "Wording").</summary>
public static class GraphTexts
{
    // The whole graph.
    public const string NewerVersion = "This pipeline needs a newer Vision Center.";
    public const string NoCamera = "Add a Camera node.";
    public static readonly string TooManyNodes = $"A pipeline has at most {VisionLimits.MaxNodes} nodes.";

    /// <summary>More links than a graph of the most nodes can use (data and control links).</summary>
    public static readonly string TooManyLinks = $"A pipeline has at most {VisionLimits.MaxLinks} links.";
    public const string MissingLinkEnd = "A link leads to a node that does not exist.";

    // A node.
    public const string UnknownKind = "This node needs a newer Vision Center.";
    public const string SecondCamera = "A pipeline has only one Camera node.";
    public const string CameraInput = "The Camera node takes no input.";
    public const string NoInput = "Link an input to this node.";
    public const string Loop = "Links cannot form a loop.";
    public const string Merge = "This node takes only one input.";
    public const string NoOutputBefore = "The node before this one has no output.";
    public const string PulseIntoData = "A Timer links only to Reset, Send now or Throttle.";
    public const string DwellNeedsArea = "Dwell needs an Area node before it.";
    public const string ChooseDays = "Choose the days.";
    public const string ChooseEvent = "Choose an event.";
    public const string UnknownPort = "This node has no such input.";
    public const string BetweenOrder = "And must not be below Value.";
    public const string LineNeedsTrack = "A line needs a Track node before it.";
    public const string CrossingsNeedStream = "Counting crossings works only while Watching.";
    public static readonly string TooManyModels = $"A path has at most {VisionLimits.MaxModelNodesPerPath} model nodes.";
    public const string ChooseModel = "Choose a model.";
    public const string ChooseDetectionModel = "Choose a detection model.";
    public const string ChooseClassificationModel = "Choose a classification model.";
    public const string ChooseClasses = "Choose the classes.";
    public const string LabelNeedsClassify = "Class of Classify needs a Classify node before it.";
    public const string EnterNumber = "Enter a number.";
    public const string EnterText = "Enter a text.";
    public const string NoControlCharacters = Oadm.Sdk.Events.RuleDefinition.NameInvalidText; // one phrase with the rules and recipients
    public const string ChooseOption = "Choose one of the options.";
    public const string ChooseYesNo = "Choose on or off.";
    public const string EnterTime = "Enter a time like 06:00.";
    public const string PointsInside = "Keep the points inside the picture.";
    public const string MinAboveMax = "The minimum size is above the maximum size.";
    public const string EnterName = "Enter a name.";
    public static readonly string NameTooLong = $"Use at most {VisionLimits.MaxNameLength} characters.";
    public const string NameTaken = "Another node has this name.";
    public const string InvalidId = "This node's id is not valid.";
    public const string IdTaken = "Two nodes have the same id.";

    /// <summary>"This node takes a picture.", "... objects or crossings."</summary>
    public static string Takes(IReadOnlyList<PortKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var names = new List<string>(kinds.Count);
        foreach (var k in kinds)
        {
            var name = KindName(k);
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        var list = names.Count switch
        {
            0 => "no input",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1],
        };
        return $"This node takes {list}.";
    }

    /// <summary>"The Timer node takes no input."</summary>
    public static string SourceInput(NodeKindInfo kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return $"The {kind.Label} node takes no input.";
    }

    /// <summary>"Reset takes a pulse." (a Timer), "Only while takes a condition."</summary>
    public static string ControlTakes(ControlInput port)
    {
        ArgumentNullException.ThrowIfNull(port);
        return $"{port.Label} takes {KindName(port.Accepts)}.";
    }

    /// <summary>"Reset takes one link."</summary>
    public static string OneControlLink(ControlInput port)
    {
        ArgumentNullException.ThrowIfNull(port);
        return $"{port.Label} takes one link.";
    }

    /// <summary>"Link the second input."</summary>
    public static string LinkRequired(ControlInput port)
    {
        ArgumentNullException.ThrowIfNull(port);
        return $"Link the {port.Label.ToLowerInvariant()} input.";
    }

    /// <summary>"Track works only while Watching."</summary>
    public static string NeedsStream(NodeKindInfo kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return $"{kind.Label} works only while Watching.";
    }

    /// <summary>"Upload the model Gate people again."</summary>
    public static string UploadAgain(string modelName) => $"Upload the model {modelName} again.";

    /// <summary>"The model Gate people has no class bicycle."</summary>
    public static string NoSuchClass(string modelName, string className) => $"The model {modelName} has no class {className}.";

    /// <summary>"Enter a number from 1 to 99."</summary>
    public static string NumberRange(double min, double max) =>
        string.Create(CultureInfo.InvariantCulture, $"Enter a number from {min:0.##} to {max:0.##}.");

    /// <summary>"Draw the area on the video."</summary>
    public static string Draw(NodeFieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return $"Draw the {field.Label.ToLowerInvariant()} on the video.";
    }

    /// <summary>"An area has 3 to 16 points."</summary>
    public static string PointCount(NodeFieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var label = field.Label.ToLowerInvariant();
        var article = label.Length > 0 && "aeiou".Contains(label[0], StringComparison.Ordinal) ? "An" : "A";
        return $"{article} {label} has {field.MinPoints} to {field.MaxPoints} points.";
    }

    private static string KindName(PortKind kind) => kind switch
    {
        PortKind.Picture => "a picture",
        PortKind.Objects or PortKind.TrackedObjects => "objects",
        PortKind.Crossings => "crossings",
        PortKind.Number => "a number",
        PortKind.Condition => "a condition",
        PortKind.Pulse => "a pulse",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
