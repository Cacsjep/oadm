using System.Globalization;
using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Vision.Client;

/// <summary>A stored model as a choice of a Model field.</summary>
public sealed record ModelOption(string Id, string Name);

/// <summary>What a node's form needs from the page: the models, the class names before a node, drawing on the video.</summary>
public interface INodeFormHost
{
    /// <summary>The stored models of <paramref name="kind"/> (not Missing ones too: they show, the check says to upload them).</summary>
    IReadOnlyList<ModelOption> ModelsOf(ModelKind kind);

    /// <summary>The class names of the node's own model, or of the model before it; empty without one.</summary>
    IReadOnlyList<string> ClassesFor(NodeViewModel node);

    /// <summary>Draw: the node's shape is drawn again on the video.</summary>
    void StartDrawing(NodeViewModel node);
}

/// <summary>
/// The selected node's settings (docs/spec/vision-center.md "Nodes"): its name, then one field per <see cref="NodeFieldInfo"/>
/// of its kind that shows for its input, drawn from the field's type (a node kind added later needs no change here). Every
/// edit goes to the node at once (the preview follows, undo keeps it); errors show directly below the field once it was
/// edited, never on an untouched form.
/// </summary>
public sealed partial class NodeFormViewModel : ValidatingViewModel
{
    private readonly CanvasViewModel _canvas;
    private bool _loading;

    public NodeFormViewModel(CanvasViewModel canvas, NodeViewModel node, INodeFormHost host)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        Node = node ?? throw new ArgumentNullException(nameof(node));
        ArgumentNullException.ThrowIfNull(host);
        _loading = true;
        Name = node.Name;
        _loading = false;
        Validation.Rule(nameof(Name), NameProblem);
        Validation.Validate();
        var input = node.InputKind;
        Fields = node.Info is { } info
            ? [.. info.Fields.Where(f => Graph.VisionGraph.Applies(f, input)).Select(f => FieldViewModel.Create(f, node, host, Write))]
            : [];
    }

    public NodeViewModel Node { get; }

    /// <summary>"Detect objects" beside the node's name in the heading.</summary>
    public string KindLabel => Node.KindLabel;

    [ObservableProperty]
    public partial string Name { get; set; }

    public static int MaxNameLength => VisionLimits.MaxNameLength;

    public IReadOnlyList<FieldViewModel> Fields { get; }

    public bool HasFields => Fields.Count > 0;

    /// <summary>The form's first error (the name's, then each field's in order).</summary>
    public string? FirstError => Validation.FirstError ?? Fields.Select(f => f.FirstError).FirstOrDefault(e => e is not null);

    /// <summary>Save was tried: every error shows.</summary>
    public void ShowAllErrors()
    {
        Validation.ShowAll();
        foreach (var field in Fields)
        {
            field.ShowAllErrors();
        }
    }

    /// <summary>The shape of the node changed on the video: the shape field follows.</summary>
    public void Refresh()
    {
        foreach (var field in Fields)
        {
            field.Reload();
        }
    }

    partial void OnNameChanged(string value)
    {
        if (!_loading && NameProblem() is null)
        {
            _canvas.Rename(Node, value.Trim());
        }
    }

    private string? NameProblem()
    {
        var name = Name.Trim();
        return name.Length == 0 ? GraphTexts.EnterName
            : name.Length > VisionLimits.MaxNameLength ? GraphTexts.NameTooLong
            : _canvas.Nodes.Any(n => !ReferenceEquals(n, Node) && string.Equals(n.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) ? GraphTexts.NameTaken
            : null;
    }

    private void Write(string key, JsonNode? value) => _canvas.SetSetting(Node, key, value);
}

/// <summary>One field of a node's form; its error below its input (the theme shows the bound property's error).</summary>
public abstract partial class FieldViewModel : ValidatingViewModel
{
    private readonly Action<string, JsonNode?> _write;

    protected FieldViewModel(NodeFieldInfo field, NodeViewModel node, Action<string, JsonNode?> write)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        Node = node ?? throw new ArgumentNullException(nameof(node));
        _write = write ?? throw new ArgumentNullException(nameof(write));
    }

    public NodeFieldInfo Field { get; }

    public NodeViewModel Node { get; }

    public string Label => Field.Label;

    public string? Hint => Field.Hint;

    public string? Unit => Field.Unit;

    public bool HasUnit => !string.IsNullOrEmpty(Field.Unit);

    /// <summary>"Optional" once, as the input's placeholder.</summary>
    public string? Placeholder => Field.Optional ? VisionTexts.Optional : null;

    /// <summary>Loading the stored value: the change is not an edit.</summary>
    protected bool Loading { get; set; }

    public string? FirstError => Validation.FirstError;

    public void ShowAllErrors() => Validation.ShowAll();

    /// <summary>The node's stored value again (after undo or a shape drawn on the video).</summary>
    public virtual void Reload()
    {
    }

    protected JsonNode? Stored => NodeValues.Get(Node.Settings, Field);

    protected void Write(JsonNode? value)
    {
        if (!Loading)
        {
            _write(Field.Key, value);
        }
    }

    /// <summary>The field of <paramref name="field"/>'s type.</summary>
    public static FieldViewModel Create(NodeFieldInfo field, NodeViewModel node, INodeFormHost host, Action<string, JsonNode?> write)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(host);
        return field.Type switch
        {
            NodeFieldType.Number => new NumberFieldViewModel(field, node, write),
            NodeFieldType.Choice => new ChoiceFieldViewModel(field, node, write),
            NodeFieldType.Model => new ModelFieldViewModel(field, node, host, write),
            NodeFieldType.Classes => new ClassesFieldViewModel(field, node, host, write),
            NodeFieldType.Shape => new ShapeFieldViewModel(field, node, host, write),
            NodeFieldType.TimeOfDay => new TimeFieldViewModel(field, node, write),
            NodeFieldType.Bool => new BoolFieldViewModel(field, node, write),
            _ => new TextFieldViewModel(field, node, write),
        };
    }
}

public sealed partial class TextFieldViewModel : FieldViewModel
{
    public TextFieldViewModel(NodeFieldInfo field, NodeViewModel node, Action<string, JsonNode?> write)
        : base(field, node, write)
    {
        Reload();
        Validation.Rule(nameof(Value), () => !Field.Optional && Value.Trim().Length == 0 ? GraphTexts.EnterText : null);
        Validation.Validate();
    }

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    public override void Reload()
    {
        Loading = true;
        Value = NodeValues.Text(Stored) ?? string.Empty;
        Loading = false;
    }

    partial void OnValueChanged(string value) => Write(value.Trim().Length == 0 ? null : JsonValue.Create(value.Trim()));
}

/// <summary>A number with its unit after it; within the field's minimum and maximum; empty when optional.</summary>
public sealed partial class NumberFieldViewModel : FieldViewModel
{
    public NumberFieldViewModel(NodeFieldInfo field, NodeViewModel node, Action<string, JsonNode?> write)
        : base(field, node, write)
    {
        Reload();
        Validation.Rule(nameof(Text), Problem);
        Validation.Validate();
    }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    public override void Reload()
    {
        Loading = true;
        Text = NodeValues.Number(Stored) is { } value ? NodeValues.Format(value) : string.Empty;
        Loading = false;
    }

    private string? Problem()
    {
        var text = Text.Trim();
        if (text.Length == 0)
        {
            return Field.Optional ? null : NumberProblem;
        }

        return !TryParse(text, out var value) || (Field.Min is { } min && value < min) || (Field.Max is { } max && value > max)
            ? NumberProblem
            : null;
    }

    private string NumberProblem => Field.Min is { } min && Field.Max is { } max ? GraphTexts.NumberRange(min, max) : GraphTexts.EnterNumber;

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    partial void OnTextChanged(string value)
    {
        if (Loading || Problem() is not null)
        {
            return; // an invalid number stays in the field with its error; the node keeps its value
        }

        var text = value.Trim();
        Write(text.Length == 0 ? null : TryParse(text, out var number) ? JsonValue.Create(number) : null);
    }
}

public sealed partial class ChoiceFieldViewModel : FieldViewModel
{
    public ChoiceFieldViewModel(NodeFieldInfo field, NodeViewModel node, Action<string, JsonNode?> write)
        : base(field, node, write)
    {
        Choices = field.Choices;
        Reload();
        Validation.Rule(nameof(Selected), () => Selected is null && !Field.Optional ? GraphTexts.ChooseOption : null);
        Validation.Validate();
    }

    public IReadOnlyList<NodeChoice> Choices { get; }

    [ObservableProperty]
    public partial NodeChoice? Selected { get; set; }

    public override void Reload()
    {
        Loading = true;
        var value = NodeValues.Text(Stored);
        Selected = Choices.FirstOrDefault(c => c.Value == value) ?? (Field.Optional || Choices.Count == 0 ? null : Choices[0]);
        Loading = false;
    }

    partial void OnSelectedChanged(NodeChoice? value) => Write(value is null ? null : JsonValue.Create(value.Value));
}

public sealed partial class ModelFieldViewModel : FieldViewModel
{
    public ModelFieldViewModel(NodeFieldInfo field, NodeViewModel node, INodeFormHost host, Action<string, JsonNode?> write)
        : base(field, node, write)
    {
        ArgumentNullException.ThrowIfNull(host);
        Models = host.ModelsOf(field.ModelKind ?? ModelKind.Detection);
        Reload();
        Validation.Rule(nameof(Selected), () => Selected is null && !Field.Optional ? GraphTexts.ChooseModel : null);
        Validation.Validate();
    }

    public IReadOnlyList<ModelOption> Models { get; }

    public static string ChoosePlaceholder => VisionTexts.ChooseModel;

    [ObservableProperty]
    public partial ModelOption? Selected { get; set; }

    public override void Reload()
    {
        Loading = true;
        var id = NodeValues.Text(Stored);
        Selected = Models.FirstOrDefault(m => m.Id == id);
        Loading = false;
    }

    partial void OnSelectedChanged(ModelOption? value) => Write(value is null ? null : JsonValue.Create(value.Id));
}

/// <summary>The classes of the model before the node, ticked in a select with check boxes; none ticked: every class.</summary>
public sealed partial class ClassesFieldViewModel : FieldViewModel
{
    public ClassesFieldViewModel(NodeFieldInfo field, NodeViewModel node, INodeFormHost host, Action<string, JsonNode?> write)
        : base(field, node, write)
    {
        ArgumentNullException.ThrowIfNull(host);
        Names = host.ClassesFor(node);
        Choices = new CheckChoiceList(VisionTexts.EveryClass, VisionTexts.EveryClass);
        Reload();
        Choices.Changed += (_, _) =>
        {
            Write(NodeValues.ToJson(Choices.ChosenValues.OfType<string>()));
            OnPropertyChanged(nameof(Summary));
        };
    }

    public IReadOnlyList<string> Names { get; }

    public CheckChoiceList Choices { get; }

    public string Summary => Choices.Summary;

    public override void Reload()
    {
        var chosen = NodeValues.Strings(Stored);
        Choices.Set(Names.Select(n => new CheckChoice(n, n)), all: chosen.Count == 0, chosen);
        OnPropertyChanged(nameof(Summary));
    }
}

/// <summary>A shape on the video: the field shows how many points it has and Draw; the shape is edited on the video.</summary>
public sealed partial class ShapeFieldViewModel : FieldViewModel
{
    private readonly INodeFormHost _host;

    public ShapeFieldViewModel(NodeFieldInfo field, NodeViewModel node, INodeFormHost host, Action<string, JsonNode?> write)
        : base(field, node, write)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Reload();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShape), nameof(ShapeText))]
    public partial int PointCount { get; private set; }

    public bool HasShape => PointCount >= Math.Max(2, Field.MinPoints);

    /// <summary>The field's line: the hint once drawn ("Drag the corners on the video."), else what to do first.</summary>
    public string ShapeText => HasShape ? Field.Hint ?? string.Empty : GraphTexts.Draw(Field);

    public override void Reload() => PointCount = NodeValues.Points(Stored).Count;

    [RelayCommand]
    private void Draw() => _host.StartDrawing(Node);
}

public sealed partial class TimeFieldViewModel : FieldViewModel
{
    public TimeFieldViewModel(NodeFieldInfo field, NodeViewModel node, Action<string, JsonNode?> write)
        : base(field, node, write)
    {
        Reload();
        Validation.Rule(nameof(Text), () => Text.Trim().Length == 0 ? (Field.Optional ? null : GraphTexts.EnterTime)
            : Parse(Text) is null ? GraphTexts.EnterTime : null);
        Validation.Validate();
    }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    public override void Reload()
    {
        Loading = true;
        Text = NodeValues.Text(Stored) ?? string.Empty;
        Loading = false;
    }

    /// <summary>"6:00" or "06:00" as "06:00"; null when it is no time of day.</summary>
    public static string? Parse(string text) =>
        TimeOnly.TryParseExact(text?.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
            : null;

    partial void OnTextChanged(string value)
    {
        if (Loading)
        {
            return;
        }

        if (value.Trim().Length == 0)
        {
            Write(null);
        }
        else if (Parse(value) is { } time)
        {
            Write(JsonValue.Create(time));
        }
    }
}

public sealed partial class BoolFieldViewModel : FieldViewModel
{
    public BoolFieldViewModel(NodeFieldInfo field, NodeViewModel node, Action<string, JsonNode?> write)
        : base(field, node, write) => Reload();

    [ObservableProperty]
    public partial bool Value { get; set; }

    public override void Reload()
    {
        Loading = true;
        Value = NodeValues.Bool(Stored) ?? false;
        Loading = false;
    }

    partial void OnValueChanged(bool value) => Write(JsonValue.Create(value));
}
