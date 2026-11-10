using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Graph;

/// <summary>
/// The graph check of docs/spec/vision-center.md rule 8 and "Graph": one camera, one input per node, no loops, fitting
/// kinds, Track and Line only while Watching, a Line after a Track, models that exist and fit, settings within their
/// fields, unique names, the limits. The server's save, the runtime and the page's editor use this one check.
/// </summary>
/// <remarks>
/// One error per node, the first that applies in this order: its id (links name it, so nothing else can be judged
/// without it); an unknown kind; the camera or another source (Timer); its data input (none, a loop, more than one, a
/// pulse); the kind of its input; a Line without Track; a Dwell without Area; Watching only; the model nodes on its
/// path; its control links (a declared port, of the port's kind, one each, the required ones); its model; its classes;
/// its fields (those shown); its name. Loops count data and control links. Deterministic and cheap enough for the editor to run on every edit.
/// </remarks>
public static class VisionGraph
{
    /// <summary>The longest node id.</summary>
    public const int MaxIdLength = 32;

    /// <summary>Checks <paramref name="pipeline"/>; <paramref name="findModel"/> finds a stored model by id (null: none).</summary>
    public static GraphCheck Check(Pipeline pipeline, Func<string?, VisionModel?> findModel)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(findModel);
        if (pipeline.Version > Pipeline.CurrentVersion)
        {
            return new GraphCheck { GraphErrors = [GraphTexts.NewerVersion] };
        }

        var nodes = pipeline.Nodes;
        var n = nodes.Count;
        var graphErrors = new List<string>();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var inputKinds = new Dictionary<string, PortKind>(StringComparer.Ordinal);
        if (n > VisionLimits.MaxNodes)
        {
            return new GraphCheck { GraphErrors = [GraphTexts.TooManyNodes] }; // nothing more is checked of an oversized graph
        }

        if (pipeline.Links.Count > VisionLimits.MaxLinks)
        {
            return new GraphCheck { GraphErrors = [GraphTexts.TooManyLinks] };
        }

        // The nodes by id (the first of a duplicate id) and their kinds.
        var index = new Dictionary<string, int>(n, StringComparer.Ordinal);
        var kinds = new NodeKindInfo?[n];
        var camera = -1;
        for (var i = 0; i < n; i++)
        {
            var node = nodes[i];
            index.TryAdd(node.Id ?? string.Empty, i);
            kinds[i] = NodeKinds.Find(node.Kind);
            if (camera < 0 && kinds[i] == NodeKinds.CameraKind)
            {
                camera = i;
            }
        }

        if (camera < 0)
        {
            graphErrors.Add(GraphTexts.NoCamera);
        }

        // The links: inputs per node, children per node.
        var inputCount = new int[n];
        var parent = new int[n];
        Array.Fill(parent, -1);
        var children = new List<int>?[n];
        var controls = new List<(int From, string Port)>?[n];
        var missingEnd = false;
        foreach (var link in pipeline.Links)
        {
            if (link is null || !index.TryGetValue(link.From ?? string.Empty, out var from) || !index.TryGetValue(link.To ?? string.Empty, out var to))
            {
                missingEnd = true;
                continue;
            }

            if (link.ToPort is { } port)
            {
                (controls[to] ??= []).Add((from, port));
                continue;
            }

            inputCount[to]++;
            if (parent[to] < 0)
            {
                parent[to] = from;
            }

            (children[from] ??= []).Add(to);
        }

        if (missingEnd)
        {
            graphErrors.Add(GraphTexts.MissingLinkEnd);
        }

        // Loops: a node that leads back to itself over data or control links.
        var targets = new List<int>?[n];
        for (var i = 0; i < n; i++)
        {
            if (controls[i] is { } list)
            {
                foreach (var (from, _) in list)
                {
                    (targets[from] ??= []).Add(i);
                }
            }
        }

        var inLoop = new bool[n];
        var seen = new bool[n];
        var queue = new Queue<int>();
        for (var i = 0; i < n; i++)
        {
            Array.Clear(seen);
            queue.Clear();
            queue.Enqueue(i);
            while (queue.Count > 0 && !inLoop[i])
            {
                var p = queue.Dequeue();
                foreach (var next in (IEnumerable<int>?)children[p] ?? [])
                {
                    Visit(next);
                }

                foreach (var next in (IEnumerable<int>?)targets[p] ?? [])
                {
                    Visit(next);
                }
            }

            void Visit(int next)
            {
                if (next == i)
                {
                    inLoop[i] = true;
                }
                else if (!seen[next])
                {
                    seen[next] = true;
                    queue.Enqueue(next);
                }
            }
        }

        // Kinds from the camera on, through nodes with one input; the model nodes and the models before each node.
        var input = new PortKind?[n];
        var output = new PortKind?[n];
        var reached = new bool[n];
        var modelsOnPath = new int[n];
        var detectBefore = new int[n];
        var classifyBefore = new int[n];
        var areaBefore = new int[n];
        Array.Fill(detectBefore, -1);
        Array.Fill(classifyBefore, -1);
        Array.Fill(areaBefore, -1);
        {
            // From every source: the Camera, Timers, Time windows, Device events.
            var stack = new Stack<int>();
            for (var i = n - 1; i >= 0; i--)
            {
                if (kinds[i] is { Accepts.Count: 0 } source && (source != NodeKinds.CameraKind || i == camera))
                {
                    reached[i] = true;
                    output[i] = source.Output(null);
                    stack.Push(i);
                }
            }

            while (stack.Count > 0)
            {
                var p = stack.Pop();
                if (children[p] is not { } list || output[p] is not { } kindOut)
                {
                    continue;
                }

                foreach (var c in list)
                {
                    if (reached[c] || inputCount[c] != 1 || inLoop[c] || kinds[c] is not { } kind || kind.Accepts.Count == 0)
                    {
                        continue;
                    }

                    reached[c] = true;
                    input[c] = kindOut;
                    inputKinds[nodes[c].Id ?? string.Empty] = kindOut;
                    modelsOnPath[c] = modelsOnPath[p] + (kind.RunsModel ? 1 : 0);
                    detectBefore[c] = kinds[p] == NodeKinds.DetectKind ? p : detectBefore[p];
                    classifyBefore[c] = kinds[p] == NodeKinds.ClassifyKind ? p : classifyBefore[p];
                    areaBefore[c] = kinds[p] == NodeKinds.AreaKind ? p : areaBefore[p];
                    if (kind.Takes(kindOut))
                    {
                        output[c] = kind.Output(kindOut);
                    }

                    stack.Push(c);
                }
            }
        }

        // The usable model of each model node (the classes of the nodes after it are checked against it).
        var models = new VisionModel?[n];
        for (var i = 0; i < n; i++)
        {
            if (kinds[i] is not { } kind)
            {
                continue;
            }

            foreach (var field in kind.Fields)
            {
                if (field.Type == NodeFieldType.Model
                    && NodeSettings.Text(Setting(nodes[i], field.Key)) is { Length: > 0 } id
                    && findModel(id) is { Missing: false } model
                    && (field.ModelKind is not { } wanted || model.Kind == wanted))
                {
                    models[i] = model;
                }
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var node = nodes[i];
            var nameError = NameError(node.Name, names);
            var idError = IdError(node.Id, ids);
            var error = idError
                ?? NodeError(pipeline, nodes, i, kinds, camera, inputCount, parent, inLoop, input, reached, modelsOnPath, detectBefore, classifyBefore, models, findModel, controls[i], output, areaBefore)
                ?? nameError;
            if (error is not null)
            {
                errors.TryAdd(node.Id ?? string.Empty, error);
            }
        }

        return new GraphCheck { NodeErrors = errors, GraphErrors = graphErrors, InputKinds = inputKinds };
    }

    /// <summary>The value of a node's setting <paramref name="key"/>, or its field's default.</summary>
    public static JsonNode? Setting(PipelineNode node, string key)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(key);
        if (node.Settings is { } settings && settings.TryGetPropertyValue(key, out var value) && value is not null)
        {
            return value;
        }

        var field = FieldOf(NodeKinds.Find(node.Kind), key);
        return field?.Default is { } def ? JsonNode.Parse(def) : null;
    }

    /// <summary>The field <paramref name="key"/> of <paramref name="kind"/>; null: none.</summary>
    public static NodeFieldInfo? FieldOf(NodeKindInfo? kind, string key)
    {
        if (kind is null)
        {
            return null;
        }

        foreach (var f in kind.Fields)
        {
            if (string.Equals(f.Key, key, StringComparison.Ordinal))
            {
                return f;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="field"/> applies to a node whose input is <paramref name="input"/> (null: not known).</summary>
    public static bool Applies(NodeFieldInfo field, PortKind? input)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.OnlyForInput.Count == 0 || (input is { } k && field.OnlyForInput.Contains(k));
    }

    /// <summary>Whether <paramref name="id"/> is a valid node id: letters and digits, 1 to <see cref="MaxIdLength"/>.</summary>
    public static bool ValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string? NodeError(
        Pipeline pipeline, IReadOnlyList<PipelineNode> nodes, int i, NodeKindInfo?[] kinds, int camera, int[] inputCount, int[] parent,
        bool[] inLoop, PortKind?[] input, bool[] reached, int[] modelsOnPath, int[] detectBefore, int[] classifyBefore,
        VisionModel?[] models, Func<string?, VisionModel?> findModel, List<(int From, string Port)>? controls, PortKind?[] output,
        int[] areaBefore)
    {
        var node = nodes[i];
        if (kinds[i] is not { } kind)
        {
            return GraphTexts.UnknownKind;
        }

        if (kind == NodeKinds.CameraKind)
        {
            if (i != camera)
            {
                return GraphTexts.SecondCamera;
            }

            if (inputCount[i] > 0)
            {
                return GraphTexts.CameraInput;
            }
        }
        else if (kind.Accepts.Count == 0)
        {
            // Another source (Timer, Time window, Device event): no data input, its own stream rule.
            if (inputCount[i] > 0)
            {
                return GraphTexts.SourceInput(kind);
            }

            if (pipeline.Mode == PipelineMode.OnAction && kind.NeedsStream)
            {
                return GraphTexts.NeedsStream(kind);
            }
        }
        else
        {
            if (inputCount[i] == 0)
            {
                return GraphTexts.NoInput;
            }

            if (inLoop[i])
            {
                return GraphTexts.Loop;
            }

            if (inputCount[i] > 1)
            {
                return GraphTexts.Merge;
            }

            if (reached[i] && input[i] is { } kindIn)
            {
                if (kindIn == PortKind.Pulse && !kind.Takes(kindIn))
                {
                    return GraphTexts.PulseIntoData;
                }

                if (!kind.Takes(kindIn))
                {
                    return GraphTexts.Takes(kind.Accepts);
                }

                if (kind == NodeKinds.LineKind && kindIn != PortKind.TrackedObjects)
                {
                    return GraphTexts.LineNeedsTrack;
                }

                if (kind == NodeKinds.DwellKind && areaBefore[i] < 0)
                {
                    return GraphTexts.DwellNeedsArea;
                }

                if (pipeline.Mode == PipelineMode.OnAction)
                {
                    if (kind.NeedsStream)
                    {
                        return GraphTexts.NeedsStream(kind);
                    }

                    if (kind == NodeKinds.CountKind && kindIn == PortKind.Crossings)
                    {
                        return GraphTexts.CrossingsNeedStream;
                    }
                }

                if (kind.RunsModel && modelsOnPath[i] > VisionLimits.MaxModelNodesPerPath)
                {
                    return GraphTexts.TooManyModels;
                }
            }
            else if (parent[i] >= 0 && kinds[parent[i]] == NodeKinds.EventKind)
            {
                return GraphTexts.NoOutputBefore;
            }
        }

        if (ControlError(kind, kinds, controls, output) is { } controlError)
        {
            return controlError;
        }

        var nodeInput = reached[i] ? input[i] : null;

        // Models.
        foreach (var field in kind.Fields)
        {
            if (field.Type != NodeFieldType.Model || !Applies(field, nodeInput))
            {
                continue;
            }

            var id = NodeSettings.Text(Setting(node, field.Key));
            var model = string.IsNullOrEmpty(id) ? null : findModel(id);
            if (model is null)
            {
                return field.ModelKind switch
                {
                    ModelKind.Detection => GraphTexts.ChooseDetectionModel,
                    ModelKind.Classification => GraphTexts.ChooseClassificationModel,
                    _ => GraphTexts.ChooseModel,
                };
            }

            if (field.ModelKind is { } wanted && model.Kind != wanted)
            {
                return wanted == ModelKind.Detection ? GraphTexts.ChooseDetectionModel : GraphTexts.ChooseClassificationModel;
            }

            if (model.Missing)
            {
                return GraphTexts.UploadAgain(model.Name);
            }
        }

        // Classes: in the node's own detection model, else in the one of the Detect node before it.
        foreach (var field in kind.Fields)
        {
            if (field.Type != NodeFieldType.Classes || !Applies(field, nodeInput))
            {
                continue;
            }

            var model = models[i] ?? (detectBefore[i] >= 0 ? models[detectBefore[i]] : null);
            if (model is null || NodeSettings.Texts(Setting(node, field.Key)) is not { } chosen)
            {
                continue;
            }

            foreach (var c in chosen)
            {
                if (!HasClass(model, c))
                {
                    return GraphTexts.NoSuchClass(model.Name, c);
                }
            }
        }

        // The class of Classify a Filter keeps.
        if (kind == NodeKinds.FilterKind && NodeSettings.Text(Setting(node, NodeKinds.LabelKey)) is { Length: > 0 } label)
        {
            if (reached[i] && classifyBefore[i] < 0)
            {
                return GraphTexts.LabelNeedsClassify;
            }

            if (classifyBefore[i] >= 0 && models[classifyBefore[i]] is { } classifier && !HasClass(classifier, label))
            {
                return GraphTexts.NoSuchClass(classifier.Name, label);
            }
        }

        // Every field's type and range.
        foreach (var field in kind.Fields)
        {
            if (Applies(field, nodeInput) && Visible(node, field) && FieldError(node, field) is { } fieldError)
            {
                return fieldError;
            }
        }

        if (kind == NodeKinds.FilterKind
            && NodeSettings.Number(Setting(node, NodeKinds.MinSizeKey)) is { } min
            && NodeSettings.Number(Setting(node, NodeKinds.MaxSizeKey)) is { } max
            && min > max)
        {
            return GraphTexts.MinAboveMax;
        }

        if (kind == NodeKinds.CompareKind
            && NodeSettings.Text(Setting(node, NodeKinds.OperatorKey)) == NodeKinds.OpBetween
            && NodeSettings.Number(Setting(node, NodeKinds.ValueKey)) is { } low
            && NodeSettings.Number(Setting(node, NodeKinds.Value2Key)) is { } high
            && low > high)
        {
            return GraphTexts.BetweenOrder;
        }

        return null;
    }

    /// <summary>Whether <paramref name="field"/> shows for the node's other settings (<see cref="NodeFieldInfo.VisibleWhenKey"/>).</summary>
    public static bool Visible(PipelineNode node, NodeFieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(field);
        if (field.VisibleWhenKey is not { } key)
        {
            return true;
        }

        var value = NodeSettings.Text(Setting(node, key));
        foreach (var v in field.VisibleWhenValues)
        {
            if (string.Equals(v, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The control links into a node: a declared port, the kind it takes, one link per port, the required ones linked.</summary>
    private static string? ControlError(NodeKindInfo kind, NodeKindInfo?[] kinds, List<(int From, string Port)>? controls, PortKind?[] output)
    {
        controls ??= [];
        for (var c = 0; c < controls.Count; c++)
        {
            var (from, port) = controls[c];
            ControlInput? declared = null;
            foreach (var ci in kind.ControlInputs)
            {
                if (string.Equals(ci.Port, port, StringComparison.Ordinal))
                {
                    declared = ci;
                }
            }

            if (declared is null)
            {
                return GraphTexts.UnknownPort;
            }

            // A source whose kind is known gives that kind; one with an error of its own shows it there.
            if ((output[from] is { } carried && carried != declared.Accepts) || kinds[from] == NodeKinds.EventKind)
            {
                return GraphTexts.ControlTakes(declared);
            }

            for (var d = 0; d < c; d++)
            {
                if (string.Equals(controls[d].Port, port, StringComparison.Ordinal))
                {
                    return GraphTexts.OneControlLink(declared);
                }
            }
        }

        foreach (var ci in kind.ControlInputs)
        {
            if (ci.Required && !controls.Exists(c => string.Equals(c.Port, ci.Port, StringComparison.Ordinal)))
            {
                return GraphTexts.LinkRequired(ci);
            }
        }

        return null;
    }

    private static string? FieldError(PipelineNode node, NodeFieldInfo field)
    {
        JsonNode? value = null;
        node.Settings?.TryGetPropertyValue(field.Key, out value);
        if (value is null)
        {
            if (field.Default is not null || field.Optional)
            {
                return null;
            }

            return field.Type switch
            {
                NodeFieldType.Number => GraphTexts.EnterNumber,
                NodeFieldType.Text => GraphTexts.EnterText,
                NodeFieldType.Choice => GraphTexts.ChooseOption,
                NodeFieldType.Model => GraphTexts.ChooseModel,
                NodeFieldType.Classes => null,
                NodeFieldType.Shape => GraphTexts.Draw(field),
                NodeFieldType.TimeOfDay => GraphTexts.EnterTime,
                NodeFieldType.Bool => GraphTexts.ChooseYesNo,
                NodeFieldType.EventType => GraphTexts.ChooseEvent,
                _ => null,
            };
        }

        switch (field.Type)
        {
            case NodeFieldType.Number:
                if (NodeSettings.Number(value) is not { } d)
                {
                    return GraphTexts.EnterNumber;
                }

                if ((field.Min is { } lo && d < lo) || (field.Max is { } hi && d > hi))
                {
                    return GraphTexts.NumberRange(field.Min ?? double.MinValue, field.Max ?? double.MaxValue);
                }

                return null;

            case NodeFieldType.Text:
                if (NodeSettings.Text(value) is not { } s)
                {
                    return GraphTexts.EnterText;
                }

                if (s.Length > VisionLimits.MaxNameLength)
                {
                    return GraphTexts.NameTooLong;
                }

                return HasControl(s) ? GraphTexts.NoControlCharacters : null;

            case NodeFieldType.Choice:
                var choice = NodeSettings.Text(value);
                foreach (var c in field.Choices)
                {
                    if (string.Equals(c.Value, choice, StringComparison.Ordinal))
                    {
                        return null;
                    }
                }

                return GraphTexts.ChooseOption;

            case NodeFieldType.Model:
                return NodeSettings.Text(value) is null ? GraphTexts.ChooseModel : null;

            case NodeFieldType.Classes:
                return NodeSettings.Texts(value) is null ? GraphTexts.ChooseClasses : null;

            case NodeFieldType.Shape:
                if (NodeSettings.Points(value) is not { } points)
                {
                    return GraphTexts.Draw(field);
                }

                if (points.Count == 0 && field.Optional)
                {
                    return null;
                }

                if (points.Count < field.MinPoints || (field.MaxPoints > 0 && points.Count > field.MaxPoints))
                {
                    return points.Count == 0 ? GraphTexts.Draw(field) : GraphTexts.PointCount(field);
                }

                foreach (var p in points)
                {
                    if (p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1)
                    {
                        return GraphTexts.PointsInside;
                    }
                }

                return null;

            case NodeFieldType.TimeOfDay:
                return NodeSettings.TimeOfDay(value) is null ? GraphTexts.EnterTime : null;

            case NodeFieldType.Bool:
                return NodeSettings.Bool(value) is null ? GraphTexts.ChooseYesNo : null;

            case NodeFieldType.Days:
                return NodeSettings.Days(value) is null ? GraphTexts.ChooseDays : null;

            case NodeFieldType.EventType:
                return NodeSettings.EventOf(value) is null ? GraphTexts.ChooseEvent : null;

            default:
                return null;
        }
    }

    private static string? NameError(string? name, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return GraphTexts.EnterName;
        }

        if (name.Length > VisionLimits.MaxNameLength)
        {
            return GraphTexts.NameTooLong;
        }

        if (HasControl(name))
        {
            return GraphTexts.NoControlCharacters;
        }

        return seen.Add(name) ? null : GraphTexts.NameTaken;
    }

    private static string? IdError(string? id, HashSet<string> seen)
    {
        if (!ValidId(id))
        {
            return GraphTexts.InvalidId;
        }

        return seen.Add(id!) ? null : GraphTexts.IdTaken;
    }

    private static bool HasClass(VisionModel model, string name)
    {
        if (string.Equals(name, VisionLimits.NoClassName, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in model.ClassNames)
        {
            if (string.Equals(c, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasControl(string s)
    {
        foreach (var c in s)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }
}
