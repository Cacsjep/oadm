using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;

using Avalonia;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Client;

/// <summary>
/// The node canvas of a pipeline's draft (docs/spec/vision-center.md "Page", "Graph"): nodes, links, the selection, the node
/// list, linking by drag with the fitting inputs highlighted, undo and redo until the draft is saved, copy and paste within
/// the pipeline, Tidy up, Fit view, pan and zoom. The graph check runs after every change (<see cref="Checker"/>, the same
/// check as the server's save) and puts its errors on the nodes. Pure: the canvas control only routes input here.
/// </summary>
public sealed partial class CanvasViewModel : ObservableObject
{
    /// <summary>Undo keeps at most this many steps.</summary>
    public const int MaxUndo = 100;

    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private readonly Dictionary<string, NodeViewModel> _byId = new(StringComparer.Ordinal);
    private PipelineNode? _clipboard;
    private string? _editKey;
    private Snapshot? _moveStart;
    private bool _moved;

    public CanvasViewModel(IReadOnlyList<PipelineNode> nodes, IReadOnlyList<PipelineLink> links)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(links);
        Load(nodes, links);
    }

    /// <summary>The graph check of the draft (set by the editor: the whole pipeline with its mode and models).</summary>
    public Func<IReadOnlyList<PipelineNode>, IReadOnlyList<PipelineLink>, GraphCheck>? Checker { get; set; }

    /// <summary>The draft changed (nodes, links, settings, places, eyes): the editor marks it Not saved and previews it again.</summary>
    public event EventHandler? Changed;

    /// <summary>Only what is shown changed (a viewer's eye): the video follows, the draft stays as saved.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>A new Area or Line node: the video starts drawing its shape at once.</summary>
    public event EventHandler<NodeViewModel>? DrawRequested;

    public ObservableCollection<NodeViewModel> Nodes { get; } = [];

    public ObservableCollection<LinkViewModel> Links { get; } = [];

    public NodePickerViewModel Picker { get; } = new();

    /// <summary>Raised whenever the canvas must draw again (live lines, selection, places): the control redraws, nothing is rebuilt.</summary>
    [ObservableProperty]
    public partial int Version { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand), nameof(CopyCommand))]
    public partial NodeViewModel? SelectedNode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    public partial LinkViewModel? SelectedLink { get; set; }

    /// <summary>Errors of the whole graph (no camera, too many nodes), shown below the canvas.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGraphError))]
    public partial string? GraphError { get; private set; }

    public bool HasGraphError => GraphError is not null;

    /// <summary>Errors on nodes and of the graph: Save waits for none.</summary>
    [ObservableProperty]
    public partial int ErrorCount { get; private set; }

    /// <summary>The user may change the draft (the change right): without it the canvas only shows.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddNodeCommand), nameof(UndoCommand), nameof(RedoCommand), nameof(TidyUpCommand), nameof(DeleteSelectedCommand), nameof(CopyCommand), nameof(PasteCommand))]
    public partial bool CanEdit { get; set; } = true;

    // The view: canvas units to screen pixels (screen = world * Zoom + Pan).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    public partial double Zoom { get; private set; } = 1;

    [ObservableProperty]
    public partial double PanX { get; private set; }

    [ObservableProperty]
    public partial double PanY { get; private set; }

    /// <summary>"100 %" at the canvas's bottom left.</summary>
    public string ZoomText => VisionTexts.Zoom(Zoom);

    /// <summary>The canvas control's size in pixels (Fit view and a new node's place).</summary>
    public Size ViewportSize { get; set; } = new(800, 500);

    /// <summary>A link being dragged: its output node and where the pointer is (canvas units).</summary>
    public NodeViewModel? LinkDragFrom { get; private set; }

    public Point LinkDragTo { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public NodeViewModel? Find(string? id) => id is not null && _byId.TryGetValue(id, out var node) ? node : null;

    /// <summary>The link into <paramref name="node"/>'s data input (its only one), or null.</summary>
    public LinkViewModel? InputOf(NodeViewModel node) => Links.FirstOrDefault(l => ReferenceEquals(l.To, node) && l.ToPort is null);

    /// <summary>The link into <paramref name="node"/>'s control input <paramref name="port"/>, or null.</summary>
    public LinkViewModel? ControlLinkOf(NodeViewModel node, string port) =>
        Links.FirstOrDefault(l => ReferenceEquals(l.To, node) && l.ToPort == port);

    public IEnumerable<LinkViewModel> OutputsOf(NodeViewModel node) => Links.Where(l => ReferenceEquals(l.From, node));

    public IReadOnlyList<PipelineNode> ToNodes() => [.. Nodes.Select(n => n.ToNode())];

    public IReadOnlyList<PipelineLink> ToLinks() => [.. Links.Select(l => l.ToLink())];

    // ---- Selection ----

    partial void OnSelectedNodeChanged(NodeViewModel? oldValue, NodeViewModel? newValue)
    {
        oldValue?.IsSelected = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            SelectedLink = null;
        }

        _editKey = null;
        Bump();
    }

    partial void OnSelectedLinkChanged(LinkViewModel? oldValue, LinkViewModel? newValue)
    {
        oldValue?.IsSelected = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            SelectedNode = null;
        }

        Bump();
    }

    public void ClearSelection()
    {
        SelectedNode = null;
        SelectedLink = null;
    }

    // ---- Adding ----

    /// <summary>Add node in the toolbar: the node list with every kind, at the canvas's top left.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddNode()
    {
        if (Picker.IsOpen && Picker.From is null)
        {
            Picker.Close();
            return;
        }

        Picker.Open(null, null, 10, 8);
    }

    /// <summary>The + of <paramref name="from"/>: the node list with only the kinds that take its output, next to the +.</summary>
    public void OpenPickerAfter(NodeViewModel from)
    {
        ArgumentNullException.ThrowIfNull(from);
        if (!CanEdit)
        {
            return;
        }

        var plus = PlusOf(from);
        Picker.Open(from, from.OutputKind, (plus.Right * Zoom) + PanX + 8, (plus.Top * Zoom) + PanY - 10);
    }

    /// <summary>
    /// The + of the empty control input <paramref name="port"/> of <paramref name="into"/> (or its dot): the node list with only
    /// the kinds whose output the input takes, left of the +.
    /// </summary>
    public void OpenPickerInto(NodeViewModel into, string port)
    {
        ArgumentNullException.ThrowIfNull(into);
        var inputs = into.ControlInputs;
        var index = -1;
        for (var i = 0; i < inputs.Count; i++)
        {
            if (inputs[i].Port == port)
            {
                index = i;
            }
        }

        if (!CanEdit || index < 0)
        {
            return;
        }

        var plus = CanvasGeometry.ControlPlusRect(into.X, into.Y, index);
        Picker.OpenInto(into, port, inputs[index].Accepts, Math.Max(8, (plus.X * Zoom) + PanX - PickerWidth - 8), (plus.Top * Zoom) + PanY - 10);
    }

    /// <summary>The node list's width (VisionView.axaml): the list opens left of a control input's +.</summary>
    public const double PickerWidth = 340;

    /// <summary>A kind chosen in the node list: the new node (linked after the + node, or into the control input), selected.</summary>
    public NodeViewModel? Choose(NodeKindInfo kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var from = Picker.From;
        var into = Picker.Into;
        var port = Picker.IntoPort;
        Picker.Close();
        return into is not null && port is not null ? AddInto(kind, into, port) : Add(kind, from);
    }

    /// <summary>
    /// A new node of <paramref name="kind"/> up and left of <paramref name="into"/>, its output linked into the control input
    /// <paramref name="port"/> (a Timer into Reset); selected.
    /// </summary>
    public NodeViewModel? AddInto(NodeKindInfo kind, NodeViewModel into, string port)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(into);
        if (!CanEdit || kind.Id == NodeKinds.Camera || Nodes.Count >= VisionLimits.MaxNodes)
        {
            return null;
        }

        Record();
        var at = into.ControlPortAt(port) ?? CanvasGeometry.InputPort(into.X, into.Y);
        var node = AddViewModel(new PipelineNode
        {
            Id = NewId(),
            Kind = kind.Id,
            Name = FreeName(kind.Label),
            X = Math.Round(into.X - CanvasGeometry.ColumnStep),
            Y = Math.Round(at.Y - CanvasGeometry.NodeHeight - CanvasGeometry.RowGap),
        });
        Check(); // the new node's output kind
        if (TryPort(node, into, out var chosen, port) && chosen == port)
        {
            if (ControlLinkOf(into, port) is { } old)
            {
                Links.Remove(old);
            }

            Links.Add(new LinkViewModel(node, into, port));
        }

        AfterChange();
        SelectedNode = node;
        return node;
    }

    /// <summary>
    /// A new node of <paramref name="kind"/>: after <paramref name="from"/> (to its right, below its other outputs, linked) or in
    /// the middle of the view; named with the lowest free number, its defaults, selected. An Area or Line starts drawing.
    /// </summary>
    public NodeViewModel? Add(NodeKindInfo kind, NodeViewModel? from = null)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (!CanEdit || kind.Id == NodeKinds.Camera || Nodes.Count >= VisionLimits.MaxNodes)
        {
            return null;
        }

        Record();
        double x;
        double y;
        if (from is not null)
        {
            // Below its parent; a second child beside the first.
            x = from.X + (OutputsOf(from).Count() * CanvasGeometry.ColumnStep);
            y = from.Y + from.Height + CanvasGeometry.RowGap;
        }
        else
        {
            x = ((ViewportSize.Width / 2) - PanX) / Zoom - (CanvasGeometry.NodeWidth / 2);
            y = ((ViewportSize.Height / 2) - PanY) / Zoom - (CanvasGeometry.NodeHeight / 2);
        }

        var draws = kind.Id is NodeKinds.Area or NodeKinds.Line or NodeKinds.Direction;
        var node = AddViewModel(new PipelineNode { Id = NewId(), Kind = kind.Id, Name = FreeName(kind.Label), X = Math.Round(x), Y = Math.Round(y), ShowOnVideo = draws });
        if (from is not null && TryPort(from, node, out var port))
        {
            Links.Add(new LinkViewModel(from, node, port));
        }

        AfterChange();
        SelectedNode = node;
        if (draws)
        {
            DrawRequested?.Invoke(this, node);
        }

        return node;
    }

    /// <summary>"Area 1" with the lowest free number for most kinds, the kind's own name first for the model and Track nodes (the approved draft).</summary>
    public string FreeName(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var used = Nodes.Select(n => n.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plainFirst = label is "Detect objects" or "Classify" or "Track";
        if (plainFirst && !used.Contains(label))
        {
            return label;
        }

        for (var i = plainFirst ? 2 : 1; ; i++)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"{label} {i}");
            if (!used.Contains(name))
            {
                return name;
            }
        }
    }

    private string NewId()
    {
        for (var i = Nodes.Count + 1; ; i++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"n{i}");
            if (!_byId.ContainsKey(id))
            {
                return id;
            }
        }
    }

    // ---- Linking ----

    /// <summary>
    /// Whether a link from <paramref name="from"/>'s output fits <paramref name="to"/>'s input: another node that takes what the
    /// output carries now and is not before <paramref name="from"/> (no loop). A node with an input gets the new one instead.
    /// </summary>
    public bool CanLink(NodeViewModel from, NodeViewModel to) => TryPort(from, to, out _);

    /// <summary>Whether a link from <paramref name="from"/> fits the control input <paramref name="port"/> of <paramref name="to"/>.</summary>
    public bool FitsPort(NodeViewModel from, NodeViewModel to, string? port)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return TryPort(from, to, out var chosen, port) && chosen == port;
    }

    /// <summary>
    /// Whether a link from <paramref name="from"/> fits <paramref name="to"/> and into which input: a Timer's pulse only into a
    /// control input (<paramref name="port"/> its id), anything else only into the data input (<paramref name="port"/> null).
    /// </summary>
    public bool TryPort(NodeViewModel from, NodeViewModel to, out string? port, string? preferred = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        port = null;
        if (ReferenceEquals(from, to) || !from.HasOutputPort || from.OutputKind is not { } kind || IsBefore(to, from))
        {
            return false;
        }

        bool Free(string? p) => !Links.Any(l => ReferenceEquals(l.From, from) && ReferenceEquals(l.To, to) && l.ToPort == p);
        var data = to.Info is { Accepts.Count: > 0 } info && info.Takes(kind) && Free(null);
        var controls = to.ControlInputs.Where(c => c.Accepts == kind && Free(c.Port)).Select(c => c.Port).ToList();

        // A drop on a control input's dot takes that input; else the data input first, then the first control input that fits
        // (an empty one before one with a link).
        if (preferred is not null && controls.Contains(preferred))
        {
            port = preferred;
            return true;
        }

        if (data)
        {
            return true;
        }

        if (controls.Count == 0)
        {
            return false;
        }

        port = controls.FirstOrDefault(c => ControlLinkOf(to, c) is null) ?? controls[0];
        return true;
    }

    /// <summary>The control input of <paramref name="node"/> whose dot is under <paramref name="world"/>, or null.</summary>
    public static string? ControlPortAt(NodeViewModel node, Point world)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.ControlInputs.FirstOrDefault(c => node.ControlPortAt(c.Port) is { } at && CanvasGeometry.Distance(world, at) <= CanvasGeometry.PortReach)?.Port;
    }

    /// <summary>
    /// Whether <paramref name="node"/> leads to <paramref name="of"/> (or is it) over data or control links: a link from
    /// <paramref name="of"/> to it would close a loop, which the server refuses too.
    /// </summary>
    public bool IsBefore(NodeViewModel node, NodeViewModel of)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(of);
        var incoming = Links.ToLookup(l => l.To);
        var seen = new HashSet<NodeViewModel> { of };
        var queue = new Queue<NodeViewModel>([of]);
        while (queue.TryDequeue(out var at))
        {
            if (ReferenceEquals(at, node))
            {
                return true;
            }

            foreach (var link in incoming[at])
            {
                if (seen.Add(link.From))
                {
                    queue.Enqueue(link.From);
                }
            }
        }

        return false;
    }

    /// <summary>A drag starts at <paramref name="from"/>'s output: the inputs that fit light up, the others dim.</summary>
    public void BeginLinkDrag(NodeViewModel from, Point at)
    {
        ArgumentNullException.ThrowIfNull(from);
        if (!CanEdit || !from.HasOutputPort)
        {
            return;
        }

        Picker.Close();
        LinkDragFrom = from;
        LinkDragTo = at;
        foreach (var node in Nodes)
        {
            node.Fit = ReferenceEquals(node, from) ? LinkFit.None : CanLink(from, node) ? LinkFit.Fits : LinkFit.Dimmed;
        }

        Bump();
    }

    public void DragLinkTo(Point at)
    {
        if (LinkDragFrom is not null)
        {
            LinkDragTo = at;
            Bump();
        }
    }

    /// <summary>
    /// The drop: on a node that fits it links (replacing that node's input; on a control input's dot <paramref name="at"/> into
    /// that input); anywhere else nothing happens.
    /// </summary>
    public bool EndLinkDrag(NodeViewModel? target, Point? at = null)
    {
        var from = LinkDragFrom;
        LinkDragFrom = null;
        foreach (var node in Nodes)
        {
            node.Fit = LinkFit.None;
        }

        var linked = from is not null && target is not null && Link(from, target, at is { } p ? ControlPortAt(target, p) : null);
        Bump();
        return linked;
    }

    /// <summary>Links <paramref name="from"/> to <paramref name="to"/> when it fits; <paramref name="to"/>'s old input goes.</summary>
    public bool Link(NodeViewModel from, NodeViewModel to, string? preferredPort = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (!CanEdit || !TryPort(from, to, out var port, preferredPort))
        {
            return false;
        }

        Record();
        if ((port is null ? InputOf(to) : ControlLinkOf(to, port)) is { } old)
        {
            Links.Remove(old);
        }

        Links.Add(new LinkViewModel(from, to, port));
        AfterChange();
        return true;
    }

    // ---- Moving, eye, settings ----

    public void BeginMove()
    {
        _moveStart = Capture();
        _moved = false;
    }

    /// <summary>The node follows the pointer (canvas units); one undo step for the whole drag.</summary>
    public void MoveNode(NodeViewModel node, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!CanEdit || (node.X == x && node.Y == y))
        {
            return;
        }

        node.X = x;
        node.Y = y;
        _moved = true;
        Bump();
    }

    public void EndMove()
    {
        if (_moved && _moveStart is { } start)
        {
            Push(start);
            _editKey = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        _moveStart = null;
        _moved = false;
    }

    /// <summary>The eye: the node's result on the video, or not.</summary>
    public void ToggleEye(NodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.CanShowOnVideo)
        {
            return;
        }

        node.ShowOnVideo = !node.ShowOnVideo;
        Bump();
        if (CanEdit)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ViewChanged?.Invoke(this, EventArgs.Empty); // a viewer: only the video and the canvas follow, nothing to save
        }
    }

    /// <summary>
    /// A setting of a node's form changed: one undo step for a run of edits of the same field (typing a name or a number).
    /// A null value removes the setting (its default applies).
    /// </summary>
    public void SetSetting(NodeViewModel node, string key, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(key);
        var editKey = node.Id + "/" + key;
        if (_editKey != editKey)
        {
            Record();
        }

        var settings = (JsonObject)node.Settings.DeepClone();
        if (value is null)
        {
            settings.Remove(key);
        }
        else
        {
            settings[key] = value.DeepClone();
        }

        node.Settings = settings;
        AfterChange();
        _editKey = editKey;
    }

    /// <summary>The node's name from its form; one undo step while typing.</summary>
    public void Rename(NodeViewModel node, string name)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Name == name)
        {
            return;
        }

        var editKey = node.Id + "/name";
        if (_editKey != editKey)
        {
            Record();
        }

        node.Name = name;
        AfterChange();
        _editKey = editKey;
    }

    // ---- Deleting, undo, copy ----

    private bool CanDelete() => CanEdit && (SelectedLink is not null || SelectedNode is { IsCamera: false });

    /// <summary>Delete: the selected link, or the selected node with its links (never the Camera).</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeleteSelected()
    {
        if (SelectedLink is { } link)
        {
            Record();
            Links.Remove(link);
            SelectedLink = null;
            AfterChange();
            return;
        }

        if (SelectedNode is not { IsCamera: false } node)
        {
            return;
        }

        Record();
        foreach (var l in Links.Where(l => ReferenceEquals(l.From, node) || ReferenceEquals(l.To, node)).ToList())
        {
            Links.Remove(l);
        }

        Nodes.Remove(node);
        _byId.Remove(node.Id);
        SelectedNode = null;
        AfterChange();
    }

    private bool CanUndoNow() => CanEdit && CanUndo;

    private bool CanRedoNow() => CanEdit && CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndoNow))]
    private void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Push(Capture());
        Restore(_undo.Pop());
    }

    [RelayCommand(CanExecute = nameof(CanRedoNow))]
    private void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Push(Capture());
        Restore(_redo.Pop());
    }

    /// <summary>The draft was saved: undo starts again from here.</summary>
    public void ForgetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _editKey = null;
        NotifyHistory();
    }

    private bool CanCopy() => SelectedNode is { IsCamera: false };

    /// <summary>Ctrl+C: the selected node (not the Camera), within this pipeline.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        if (SelectedNode is { IsCamera: false } node)
        {
            _clipboard = node.ToNode();
            PasteCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanPaste() => CanEdit && _clipboard is not null && Nodes.Count < VisionLimits.MaxNodes;

    /// <summary>Ctrl+V: a copy of the copied node with a free name, a little below and right of it, without links.</summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste()
    {
        if (_clipboard is not { } copy || !CanPaste())
        {
            return;
        }

        Record();
        var label = NodeLook.Label(copy.Kind);
        var node = AddViewModel(copy with
        {
            Id = NewId(),
            Name = FreeName(label),
            X = copy.X + 40,
            Y = copy.Y + 40,
            Settings = (JsonObject)copy.Settings.DeepClone(),
        });
        _clipboard = copy with { X = copy.X + 40, Y = copy.Y + 40 };
        AfterChange();
        SelectedNode = node;
    }

    // ---- Layout and view ----

    /// <summary>
    /// Tidy up: from top to bottom by depth from the camera, the branches side by side with one gap between the cards
    /// (<see cref="CanvasGeometry.ColumnGap"/>), a parent centred above its children; each row as high as its tallest card and
    /// one gap (<see cref="CanvasGeometry.RowGap"/>) below it, room for a link and its count. The nodes without an input that feed
    /// a control input (Timer, Time window, Device event) go left of the node they feed, top-aligned with it, further left while
    /// a card is in the way; one that feeds a data input heads its own chain, top to bottom like the camera's. One undo step;
    /// the view fits afterwards.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void TidyUp()
    {
        Record();
        var column = 0;
        var depthOf = new Dictionary<NodeViewModel, int>();
        bool IsSource(NodeViewModel n) => n.Info is { Accepts.Count: 0 } && !n.IsCamera;

        // A source that feeds a data input (a Time window into Logic, a Device event into a count) heads its own chain, laid out
        // top to bottom like the camera's; the others (a Timer's pulse) go beside the node they feed.
        bool IsStart(NodeViewModel n) => IsSource(n) && !OutputsOf(n).Any(l => !l.IsControl);

        // A node fed by starts (into a control input) keeps a column free left of it for each of them.
        var startsOf = Nodes.Where(IsStart)
            .Select(s => (Start: s, Target: OutputsOf(s).Select(l => l.To).FirstOrDefault(t => !IsStart(t))))
            .Where(x => x.Target is not null)
            .GroupBy(x => x.Target!)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Start).ToList());
        double Lay(NodeViewModel node, int depth)
        {
            depthOf[node] = depth;
            column += startsOf.GetValueOrDefault(node)?.Count ?? 0;
            var children = OutputsOf(node).Where(l => !l.IsControl).Select(l => l.To).Where(c => !depthOf.ContainsKey(c)).ToList();
            if (children.Count == 0)
            {
                node.X = CanvasGeometry.Margin + (column++ * CanvasGeometry.ColumnStep);
                return node.X;
            }

            var xs = children.Select(c => Lay(c, depth + 1)).ToList();
            node.X = Math.Round((xs.Min() + xs.Max()) / 2);
            return node.X;
        }

        foreach (var camera in Nodes.Where(n => n.IsCamera).ToList())
        {
            Lay(camera, 0);
        }

        foreach (var source in Nodes.Where(n => IsSource(n) && !IsStart(n) && !depthOf.ContainsKey(n)).ToList())
        {
            Lay(source, 0);
        }

        foreach (var node in Nodes.Where(n => !depthOf.ContainsKey(n) && !IsStart(n) && InputOf(n) is null).ToList())
        {
            Lay(node, 0);
        }

        // Rows by the real card heights.
        var rows = depthOf.Count == 0 ? 0 : depthOf.Values.Max() + 1;
        var top = new double[rows];
        var y = CanvasGeometry.Margin;
        for (var d = 0; d < rows; d++)
        {
            top[d] = y;
            y += depthOf.Where(e => e.Value == d).Select(e => e.Key.Height).DefaultIfEmpty(CanvasGeometry.NodeHeight).Max() + CanvasGeometry.RowGap;
        }

        foreach (var (node, depth) in depthOf)
        {
            node.Y = top[depth];
        }

        // The starts left of the node they feed, top-aligned with it.
        var placed = depthOf.Keys.ToList();
        bool Free(Rect rect) => !placed.Any(p => p.Rect.Inflate(CanvasGeometry.ColumnGap / 2).Intersects(rect));
        foreach (var start in Nodes.Where(n => IsStart(n) && !placed.Contains(n)).ToList())
        {
            var target = OutputsOf(start).Select(l => l.To).FirstOrDefault(placed.Contains);
            var slot = target is not null && startsOf.TryGetValue(target, out var starts) ? starts.IndexOf(start) + 1 : 1;
            var x = target?.X - (slot * CanvasGeometry.ColumnStep) ?? CanvasGeometry.Margin + (column++ * CanvasGeometry.ColumnStep);
            var startY = target?.Y ?? CanvasGeometry.Margin;
            while (target is not null && !Free(CanvasGeometry.NodeRect(x, startY, start.Height)))
            {
                x -= CanvasGeometry.ColumnStep;
            }

            start.X = x;
            start.Y = startY;
            placed.Add(start);
        }

        foreach (var node in Nodes.Where(n => !placed.Contains(n)))
        {
            node.X = CanvasGeometry.Margin + (column++ * CanvasGeometry.ColumnStep);
            node.Y = CanvasGeometry.Margin;
        }

        AfterChange();
        FitView();
    }

    /// <summary>Fit view: every node in the view, at most 100 %, centred across and near the top (the graph grows downwards).</summary>
    [RelayCommand]
    public void FitView()
    {
        if (Nodes.Count == 0 || ViewportSize.Width <= 0 || ViewportSize.Height <= 0)
        {
            return;
        }

        var x0 = Nodes.Min(n => n.X) - 30;
        var y0 = Nodes.Min(n => n.Y) - 30;
        var x1 = Nodes.Max(n => n.X) + CanvasGeometry.NodeWidth + 30;
        var y1 = Nodes.Max(n => n.Y + n.Height) + CanvasGeometry.PlusGap + CanvasGeometry.PlusSize + 30; // the + below the lowest node
        var zoom = Math.Clamp(Math.Min(ViewportSize.Width / (x1 - x0), ViewportSize.Height / (y1 - y0)), CanvasGeometry.MinZoom, 1);
        Zoom = zoom;
        PanX = ((ViewportSize.Width - ((x1 - x0) * zoom)) / 2) - (x0 * zoom);
        PanY = Math.Min(TopRoom, (ViewportSize.Height - ((y1 - y0) * zoom)) / 2) - (y0 * zoom);
        Bump();
    }

    /// <summary>Fit view leaves at most this much above the graph (screen pixels; the node list opens there too).</summary>
    public const double TopRoom = 24;

    /// <summary>The wheel: zoom by <paramref name="factor"/> around <paramref name="screen"/> (it stays under the pointer), 25 % to 200 %.</summary>
    public void ZoomAt(Point screen, double factor)
    {
        var zoom = Math.Clamp(Zoom * factor, CanvasGeometry.MinZoom, CanvasGeometry.MaxZoom);
        if (zoom == Zoom)
        {
            return;
        }

        PanX = screen.X - ((screen.X - PanX) * zoom / Zoom);
        PanY = screen.Y - ((screen.Y - PanY) * zoom / Zoom);
        Zoom = zoom;
        Picker.Close();
        Bump();
    }

    public void PanTo(double x, double y)
    {
        PanX = x;
        PanY = y;
        Bump();
    }

    public Point ToWorld(Point screen) => new((screen.X - PanX) / Zoom, (screen.Y - PanY) / Zoom);

    public Point ToScreen(Point world) => new((world.X * Zoom) + PanX, (world.Y * Zoom) + PanY);

    // ---- Hit tests (canvas units) ----

    /// <summary>The topmost node card under <paramref name="world"/>.</summary>
    public NodeViewModel? NodeAt(Point world) =>
        Nodes.LastOrDefault(n => n.Rect.Contains(world));

    public NodeViewModel? OutputPortAt(Point world) =>
        Nodes.LastOrDefault(n => n.HasOutputPort && CanvasGeometry.Distance(world, n.OutputPoint) <= CanvasGeometry.PortReach / Math.Min(1, Zoom));

    /// <summary>The + shows on a node with an output that leads nowhere yet, or on the selected node.</summary>
    public bool ShowsPlus(NodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return CanEdit && node.HasOutputPort && node.OutputKind is not null && (node.IsSelected || !OutputsOf(node).Any());
    }

    /// <summary>Where the + of <paramref name="node"/> is (below its output, beside its links when it has some).</summary>
    public Rect PlusOf(NodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return CanvasGeometry.PlusRect(node.X, node.Y, node.HasError, OutputsOf(node).Any(), node.Height);
    }

    public NodeViewModel? PlusAt(Point world) =>
        Nodes.LastOrDefault(n => ShowsPlus(n) && PlusOf(n).Inflate(2).Contains(world));

    /// <summary>
    /// The empty control input under <paramref name="world"/>: its + (the selected node) or its dot (any node); a click adds a
    /// node into it.
    /// </summary>
    public (NodeViewModel Node, string Port)? EmptyControlAt(Point world)
    {
        if (!CanEdit)
        {
            return null;
        }

        for (var n = Nodes.Count - 1; n >= 0; n--)
        {
            var node = Nodes[n];
            var inputs = node.ControlInputs;
            for (var i = 0; i < inputs.Count; i++)
            {
                if (ControlLinkOf(node, inputs[i].Port) is not null)
                {
                    continue;
                }

                var dot = CanvasGeometry.ControlPort(node.X, node.Y, i);
                if (CanvasGeometry.Distance(world, dot) <= CanvasGeometry.PortReach
                    || (ShowsControlPlus(node) && CanvasGeometry.ControlPlusRect(node.X, node.Y, i).Inflate(2).Contains(world)))
                {
                    return (node, inputs[i].Port);
                }
            }
        }

        return null;
    }

    /// <summary>The + of the empty control inputs shows on the selected node.</summary>
    public bool ShowsControlPlus(NodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return CanEdit && node.IsSelected && node.ControlInputs.Count > 0;
    }

    public NodeViewModel? EyeAt(Point world) =>
        Nodes.LastOrDefault(n => n.CanShowOnVideo && CanvasGeometry.EyeRect(n.X, n.Y).Contains(world));

    public LinkViewModel? LinkAt(Point world) => Links.LastOrDefault(l =>
        CanvasGeometry.DistanceToCurve(world, l.Start, l.End, l.IsControl) <= CanvasGeometry.LinkReach / Zoom);

    /// <summary>
    /// The pointer over the canvas: the hand over everything a click acts on (the +, the eye, a node, a link), the crosshair
    /// over an output dot (a drag from it links), none over the empty canvas (a drag pans).
    /// </summary>
    public Avalonia.Input.StandardCursorType? CursorAt(Point world)
    {
        return PlusAt(world) is not null || EyeAt(world) is not null || EmptyControlAt(world) is not null ? Avalonia.Input.StandardCursorType.Hand
            : CanEdit && OutputPortAt(world) is not null ? Avalonia.Input.StandardCursorType.Cross
            : NodeAt(world) is not null || LinkAt(world) is not null ? Avalonia.Input.StandardCursorType.Hand
            : null;
    }

    // ---- The live outputs ----

    /// <summary>
    /// A preview's node outputs: each card's live line and each link's number of objects, in place (no node is rebuilt, the
    /// canvas draws once). A node with an error says it is not running.
    /// </summary>
    public void ApplyOutputs(IReadOnlyList<NodeOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var byId = outputs.GroupBy(o => o.NodeId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            node.Live = node.HasError ? VisionTexts.NotRunning
                : byId.TryGetValue(node.Id, out var output) ? output.Live
                : string.Empty;
        }

        foreach (var link in Links)
        {
            link.Count = byId.TryGetValue(link.From.Id, out var output) && output.Kind is PortKind.Objects or PortKind.TrackedObjects
                ? output.Objects.Count.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        Bump();
    }

    /// <summary>The mode or the models changed: the check runs again.</summary>
    public void Recheck() => Check();

    // ---- Internals ----

    private void Load(IReadOnlyList<PipelineNode> nodes, IReadOnlyList<PipelineLink> links)
    {
        var selected = SelectedNode?.Id;
        Nodes.Clear();
        Links.Clear();
        _byId.Clear();
        foreach (var node in nodes)
        {
            if (!_byId.ContainsKey(node.Id))
            {
                AddViewModel(node);
            }
        }

        foreach (var link in links)
        {
            if (Find(link.From) is { } from && Find(link.To) is { } to && !Links.Any(l => ReferenceEquals(l.To, to) && l.ToPort == link.ToPort))
            {
                Links.Add(new LinkViewModel(from, to, link.ToPort));
            }
        }

        RecomputeKinds();
        SelectedNode = Find(selected);
        SelectedLink = null;
    }

    private NodeViewModel AddViewModel(PipelineNode node)
    {
        var vm = new NodeViewModel(node);
        Nodes.Add(vm);
        _byId[vm.Id] = vm;
        return vm;
    }

    /// <summary>What each link carries now, from the camera along the links (a loop or a missing input carries nothing).</summary>
    private void RecomputeKinds()
    {
        foreach (var node in Nodes)
        {
            node.InputKind = null;
            node.OutputKind = null;
        }

        var done = new HashSet<NodeViewModel>();
        var queue = new Queue<NodeViewModel>(Nodes.Where(n => n.Info is { Accepts.Count: 0 })); // the Camera and Timers
        foreach (var start in queue)
        {
            start.OutputKind = start.Info?.Output(null);
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!done.Add(node))
            {
                continue;
            }

            foreach (var link in OutputsOf(node).Where(l => !l.IsControl))
            {
                var next = link.To;
                next.InputKind = node.OutputKind;
                next.OutputKind = node.OutputKind is { } input && next.Info is { } info && info.Takes(input) ? info.Output(input) : null;
                queue.Enqueue(next);
            }
        }
    }

    private void Check()
    {
        RecomputeKinds();
        GraphCheck? check = null;
        if (Checker is { } checker)
        {
            check = checker(ToNodes(), ToLinks());
        }

        foreach (var node in Nodes)
        {
            node.Error = check?.NodeErrors.GetValueOrDefault(node.Id);
            if (check?.InputKinds.TryGetValue(node.Id, out var input) == true && node.InputKind is null)
            {
                node.InputKind = input;
            }

            if (node.HasError)
            {
                node.Live = VisionTexts.NotRunning;
            }
            else if (node.Live == VisionTexts.NotRunning)
            {
                node.Live = string.Empty;
            }
        }

        GraphError = check is { GraphErrors.Count: > 0 } c ? string.Join(" ", c.GraphErrors) : null;
        ErrorCount = (check?.NodeErrors.Count ?? 0) + (check?.GraphErrors.Count ?? 0);
        Bump();
    }

    private void AfterChange()
    {
        Check();
        NotifyHistory();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Record()
    {
        Push(Capture());
        _editKey = null;
    }

    private void Push(Snapshot snapshot)
    {
        _undo.Push(snapshot);
        if (_undo.Count > MaxUndo)
        {
            var keep = _undo.Take(MaxUndo).Reverse().ToList();
            _undo.Clear();
            foreach (var s in keep)
            {
                _undo.Push(s);
            }
        }

        _redo.Clear();
        NotifyHistory();
    }

    private Snapshot Capture() => new(ToNodes(), ToLinks(), SelectedNode?.Id);

    private void Restore(Snapshot snapshot)
    {
        Picker.Close();
        var selected = snapshot.Selected;
        Load(snapshot.Nodes, snapshot.Links);
        SelectedNode = Find(selected);
        _editKey = null;
        Check();
        NotifyHistory();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
    }

    private void Bump() => Version++;

    private sealed record Snapshot(IReadOnlyList<PipelineNode> Nodes, IReadOnlyList<PipelineLink> Links, string? Selected);
}
