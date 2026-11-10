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

    /// <summary>Choose event: the event types in the dialog, then the device of a per-device event; null when cancelled.</summary>
    Task<EventTypeValue?> ChooseEventAsync(EventTypeValue? current) => Task.FromResult<EventTypeValue?>(null);

    /// <summary>"Door open · AXIS P3265 · 10.0.0.48" of a chosen event.</summary>
    string EventTypeText(EventTypeValue value) => value.Type;
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
        foreach (var field in Fields)
        {
            field.ValidityChanged += (_, _) => ValidityChanged?.Invoke(this, EventArgs.Empty);
        }

        UpdateShown();
    }

    /// <summary>An error of the form came or went: Save follows.</summary>
    public event EventHandler? ValidityChanged;

    public NodeViewModel Node { get; }

    /// <summary>"Detect objects" beside the node's name in the heading.</summary>
    public string KindLabel => Node.KindLabel;

    /// <summary>The kind beside the name only when the name does not already say it.</summary>
    public bool ShowKindLabel => !string.Equals(Node.Name.Trim(), KindLabel, StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    public partial string Name { get; set; }

    public static int MaxNameLength => VisionLimits.MaxNameLength;

    public IReadOnlyList<FieldViewModel> Fields { get; }

    public bool HasFields => Fields.Count > 0;

    /// <summary>The form's first error (the name's, then each field's in order).</summary>
    public string? FirstError => Validation.FirstError ?? Fields.Where(f => f.IsShown).Select(f => f.FirstError).FirstOrDefault(e => e is not null);

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

        UpdateShown();
    }

    /// <summary>
    /// A field with <see cref="NodeFieldInfo.VisibleWhenKey"/> shows only while that field holds one of its values (Timer: Minutes
    /// for every few minutes, At for every day or week; Compare: And for between).
    /// </summary>
    private void UpdateShown()
    {
        foreach (var field in Fields)
        {
            field.IsShown = field.Field.VisibleWhenKey is not { } key
                || (VisionGraph.FieldOf(Node.Info, key) is { } other
                    && NodeValues.Text(NodeValues.Get(Node.Settings, other)) is { } value
                    && field.Field.VisibleWhenValues.Contains(value));
        }
    }

    partial void OnNameChanged(string value)
    {
        if (!_loading && NameProblem() is null)
        {
            _canvas.Rename(Node, value.Trim());
        }
    }

    protected override void OnValidationChanged() => ValidityChanged?.Invoke(this, EventArgs.Empty);

    private string? NameProblem()
    {
        var name = Name.Trim();
        return name.Length == 0 ? GraphTexts.EnterName
            : name.Length > VisionLimits.MaxNameLength ? GraphTexts.NameTooLong
            : _canvas.Nodes.Any(n => !ReferenceEquals(n, Node) && string.Equals(n.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) ? GraphTexts.NameTaken
            : null;
    }

    private void Write(string key, JsonNode? value)
    {
        _canvas.SetSetting(Node, key, value);
        if (key == NodeKinds.ModelKey)
        {
            // Another model: the class names of the Classes field follow it (a new node has none until its model).
            foreach (var classes in Fields.OfType<ClassesFieldViewModel>())
            {
                classes.Reload();
            }
        }

        UpdateShown();
    }
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

    /// <summary>What the field does, in its info icon.</summary>
    public string? Info => Field.Info;

    public string? Unit => Field.Unit;

    public bool HasUnit => !string.IsNullOrEmpty(Field.Unit);

    /// <summary>"Optional" once, as the input's placeholder.</summary>
    public string? Placeholder => Field.Optional ? VisionTexts.Optional : null;

    /// <summary>Loading the stored value: the change is not an edit.</summary>
    protected bool Loading { get; set; }

    public string? FirstError => Validation.FirstError;

    /// <summary>The field shows: always, or while another field holds one of its values (VisibleWhen).</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;

    /// <summary>An error of the field came or went.</summary>
    public event EventHandler? ValidityChanged;

    public void ShowAllErrors() => Validation.ShowAll();

    protected override void OnValidationChanged() => ValidityChanged?.Invoke(this, EventArgs.Empty);

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
            NodeFieldType.Days => new DaysFieldViewModel(field, node, write),
            NodeFieldType.EventType => new EventTypeFieldViewModel(field, node, host, write),
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

    /// <summary>A slider with the number beside it (the field's <see cref="NodeFieldInfo.Slider"/> with a range).</summary>
    public bool IsSlider => Field.Slider && Field.Min is not null && Field.Max is not null;

    public bool IsPlain => !IsSlider;

    public double Minimum => Field.Min ?? 0;

    public double Maximum => Field.Max ?? 100;

    /// <summary>The shown error of the number (the slider row shows it below itself).</summary>
    public string? TextError => ErrorOf(nameof(Text));

    public bool HasTextError => TextError is not null;

    public override void Reload()
    {
        Loading = true;
        Text = NodeValues.Number(Stored) is { } value ? NodeValues.Format(value) : string.Empty;
        Loading = false;
    }

    protected override void OnValidationChanged()
    {
        base.OnValidationChanged();
        OnPropertyChanged(nameof(TextError));
        OnPropertyChanged(nameof(HasTextError));
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
        _host = host;
        Models = host.ModelsOf(field.ModelKind ?? ModelKind.Detection);
        Reload();
        Validation.Rule(nameof(Selected), () => Selected is null && !Field.Optional ? GraphTexts.ChooseModel : null);
        Validation.Validate();
    }

    private readonly INodeFormHost _host;

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

    partial void OnSelectedChanged(ModelOption? value)
    {
        Write(value is null ? null : JsonValue.Create(value.Id));
    }
}

/// <summary>The classes of the model before the node, ticked in a select with check boxes; none ticked: every class.</summary>
public sealed partial class ClassesFieldViewModel : FieldViewModel
{
    public ClassesFieldViewModel(NodeFieldInfo field, NodeViewModel node, INodeFormHost host, Action<string, JsonNode?> write)
        : base(field, node, write)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Choices = new CheckChoiceList(VisionTexts.EveryClass, VisionTexts.EveryClass);
        Reload();
        Choices.Changed += (_, _) =>
        {
            Write(NodeValues.ToJson(Choices.ChosenValues.OfType<string>()));
            OnPropertyChanged(nameof(Summary));
        };
    }

    private readonly INodeFormHost _host;

    /// <summary>The class names of the node's model (or of the model before it), read again on every reload.</summary>
    public IReadOnlyList<string> Names { get; private set; } = [];

    public CheckChoiceList Choices { get; }

    public string Summary => Choices.Summary;

    public override void Reload()
    {
        Names = _host.ClassesFor(Node);
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

/// <summary>Weekdays as the shared day chips, Monday first; none on means every day (the field's hint says so).</summary>
public sealed partial class DaysFieldViewModel : FieldViewModel
{
    private static readonly string[] Keys = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    public DaysFieldViewModel(NodeFieldInfo field, NodeViewModel node, Action<string, JsonNode?> write)
        : base(field, node, write)
    {
        Days = DayChoice.Week();
        foreach (var day in Days)
        {
            day.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DayChoice.IsChecked) && !Loading)
                {
                    Write(NodeValues.ToJson(Chosen));
                }
            };
        }

        Reload();
    }

    public IReadOnlyList<DayChoice> Days { get; }

    /// <summary>The keys of the days that are on ("mon", ...), in week order.</summary>
    public IReadOnlyList<string> Chosen => [.. Days.Select((d, i) => (d, i)).Where(x => x.d.IsChecked).Select(x => Keys[x.i])];

    public override void Reload()
    {
        Loading = true;
        var stored = NodeValues.Strings(Stored);
        for (var i = 0; i < Days.Count; i++)
        {
            Days[i].IsChecked = stored.Contains(Keys[i]);
        }

        Loading = false;
    }
}

/// <summary>An event of the event rules with its device: a button that opens the event list and shows the choice.</summary>
public sealed partial class EventTypeFieldViewModel : FieldViewModel
{
    private readonly INodeFormHost _host;

    public EventTypeFieldViewModel(NodeFieldInfo field, NodeViewModel node, INodeFormHost host, Action<string, JsonNode?> write)
        : base(field, node, write)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Reload();
        Validation.Rule(nameof(Value), () => Value is null && !Field.Optional ? VisionTexts.ChooseAnEvent : null);
        Validation.Validate();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial EventTypeValue? Value { get; private set; }

    /// <summary>The button's text: the chosen event and its device, or Choose event.</summary>
    public string Text => Value is { } value ? _host.EventTypeText(value) : VisionTexts.ChooseEvent;

    /// <summary>The shown error (below the button).</summary>
    public string? ValueError => ErrorOf(nameof(Value));

    public override void Reload()
    {
        Loading = true;
        Value = EventTypeValue.Of(Stored);
        Loading = false;
    }

    protected override void OnValidationChanged()
    {
        base.OnValidationChanged();
        OnPropertyChanged(nameof(ValueError));
    }

    [RelayCommand]
    private async Task ChooseAsync()
    {
        if (await _host.ChooseEventAsync(Value).ConfigureAwait(true) is { } chosen)
        {
            Value = chosen;
            Write(chosen.ToJson());
        }
    }
}
