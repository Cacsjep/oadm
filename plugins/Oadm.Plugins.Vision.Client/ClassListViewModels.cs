using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Vision.Client;

/// <summary>
/// The class names field, one name per line (docs/spec/vision-center.md "Class lists"): the one field of a class list's dialog
/// and of a model's own names, never a copy. With <see cref="Count"/> it must hold exactly that many names (a model's
/// classes); without, 1 to <see cref="VisionLimits.MaxClasses"/>. The error below the field.
/// </summary>
public sealed partial class ClassNamesEditor : ValidatingViewModel
{
    public ClassNamesEditor(IReadOnlyList<string> names, int? count, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(names);
        Count = count;
        IsReadOnly = isReadOnly;
        Text = string.Join(Environment.NewLine, names);
        Validation.Rule(nameof(Text), Problem);
        Validation.Validate();
    }

    /// <summary>The number of names it must hold; null: any number up to the limit.</summary>
    public int? Count { get; }

    public bool IsReadOnly { get; }

    public static string Hint => VisionTexts.ClassesHint;

    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>The names as the lines say, trimmed (trailing empty lines left out).</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            var lines = Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).ToList();
            while (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }
    }

    /// <summary>"91 names" below a read-only list.</summary>
    public string CountText => VisionTexts.NameCount(Names.Count);

    /// <summary>An error came or went (the dialog's main button follows).</summary>
    public event EventHandler? ValidityChanged;

    /// <summary>The server refused the names: its reason below the field.</summary>
    public void SetServerError(string? message) => Validation.SetServerError(nameof(Text), message);

    public void ShowAllErrors() => Validation.ShowAll();

    public string? FirstError => Validation.FirstError;

    protected override void OnValidationChanged() => ValidityChanged?.Invoke(this, EventArgs.Empty);

    private string? Problem()
    {
        var names = Names;
        return names.Count == 0 ? VisionTexts.EnterNames
            : names.Any(n => n.Length == 0) || (Count is { } c && names.Count != c) ? VisionTexts.WrongClassCount
            : names.Count > VisionLimits.MaxClasses ? VisionTexts.TooManyNames
            : null;
    }
}

/// <summary>A class list in the Models card: name, its line ("91 names · Built-in", "80 names · 2 models") and its ⋯ menu.</summary>
public sealed partial class ClassListRow : ObservableObject
{
    public ClassListRow(ClassList list, IReadOnlyList<string> usedBy) => Apply(list, usedBy);

    [ObservableProperty]
    public partial ClassList List { get; private set; } = new();

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Line { get; private set; } = string.Empty;

    public IReadOnlyList<string> UsedBy { get; private set; } = [];

    /// <summary>A refusal of Delete, in the row until the next action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefusal))]
    public partial string? Refusal { get; set; }

    public bool HasRefusal => Refusal is not null;

    public void Apply(ClassList list, IReadOnlyList<string> usedBy)
    {
        ArgumentNullException.ThrowIfNull(list);
        List = list;
        UsedBy = usedBy ?? [];
        Name = list.Name;
        Line = VisionTexts.ClassListLine(list, UsedBy.Count);
    }
}

/// <summary>
/// New, Edit and View of a class list: Name and the names, one per line (<see cref="ClassNamesEditor"/>); errors below the
/// fields, the server's refusal of a field below it. View shows a built-in list read only, with Close alone.
/// </summary>
public sealed partial class ClassListEditViewModel : ValidatingViewModel
{
    private readonly Func<string, bool> _taken;

    public ClassListEditViewModel(ClassList? list, Func<string, bool> taken, bool isReadOnly = false)
    {
        _taken = taken ?? throw new ArgumentNullException(nameof(taken));
        Id = list?.Id ?? string.Empty;
        IsReadOnly = isReadOnly;
        Name = list?.Name ?? string.Empty;
        Names = new ClassNamesEditor(list?.Names ?? [], null, isReadOnly);
        Names.ValidityChanged += (_, _) => OkCommand.NotifyCanExecuteChanged();
        Validation.Rule(nameof(Name), () => Name.Trim().Length == 0 ? GraphTexts.EnterName
            : Name.Trim().Length > VisionLimits.MaxNameLength ? GraphTexts.NameTooLong
            : _taken(Name.Trim()) ? VisionTexts.ClassListNameTaken
            : null);
        Validation.Validate();
    }

    /// <summary>OK (true) or Cancel (false): the view closes the window.</summary>
    public event EventHandler<bool>? CloseRequested;

    public string Id { get; }

    public bool IsNew => Id.Length == 0;

    public bool IsReadOnly { get; }

    public bool CanChange => !IsReadOnly;

    public string Title => IsReadOnly ? Name : IsNew ? VisionTexts.NewClassList : VisionTexts.EditClassList;

    public string ConfirmText => IsNew ? VisionTexts.Create : VisionTexts.Save;

    public static int MaxNameLength => VisionLimits.MaxNameLength;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial string Name { get; set; }

    public ClassNamesEditor Names { get; }

    /// <summary>The list as it would be saved.</summary>
    public ClassList ToList() => new() { Id = Id, Name = Name.Trim(), Names = Names.Names };

    /// <summary>The server's refusals by field ("name", "names"); anything else comes back for the host's message.</summary>
    public string? ShowServerErrors(IReadOnlyDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        string? other = null;
        foreach (var (key, message) in errors)
        {
            switch (key)
            {
                case "name":
                    Validation.SetServerError(nameof(Name), message);
                    break;
                case "names":
                    Names.SetServerError(message);
                    break;
                default:
                    other ??= message;
                    break;
            }
        }

        return other;
    }

    private bool CanOk() => !IsReadOnly && IsFormValid && Names.IsFormValid;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        Validation.ShowAll();
        Names.ShowAllErrors();
        if (CanOk())
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    protected override void OnValidationChanged() => OkCommand.NotifyCanExecuteChanged();
}

/// <summary>A choice of a model's Class list dialog: a list that fits its class count, or its own names (null id).</summary>
public sealed record ClassListChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The dialogs of a model's ⋯ menu: Rename (one Name field: required, unique, at most 64 characters) and Class list (a select
/// of the lists with as many names as the model has classes, plus "Own names", which shows the model's names to edit).
/// Errors below the fields, the main button off while there is one.
/// </summary>
public sealed partial class ModelEditViewModel : ValidatingViewModel
{
    private readonly Func<string, bool> _taken;

    private readonly IReadOnlyList<ClassList> _lists;

    private ModelEditViewModel(bool isRename, VisionModel model, IReadOnlyList<ClassList> lists, Func<string, bool> taken)
    {
        _lists = lists;
        IsRename = isRename;
        _taken = taken;
        Name = model.Name;
        Own = new ClassNamesEditor(model.ClassNames, model.ClassNames.Count);
        Own.ValidityChanged += (_, _) => OkCommand.NotifyCanExecuteChanged();
        Choices = [new ClassListChoice(null, VisionTexts.OwnNames), .. lists.Where(l => l.Names.Count == model.ClassNames.Count).Select(l => new ClassListChoice(l.Id, l.Name))];
        SelectedChoice = Choices.FirstOrDefault(c => c.Id is not null && c.Id == model.ClassListId) ?? Choices[0];
        if (isRename)
        {
            Validation.Rule(nameof(Name), () => Name.Trim().Length == 0 ? GraphTexts.EnterName
                : Name.Trim().Length > VisionLimits.MaxNameLength ? GraphTexts.NameTooLong
                : _taken(Name.Trim()) ? VisionTexts.ModelNameTaken
                : null);
        }

        Validation.Validate();
    }

    public static ModelEditViewModel ForRename(VisionModel model, Func<string, bool> taken)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new(true, model, [], taken ?? throw new ArgumentNullException(nameof(taken)));
    }

    /// <summary>The Class list dialog of <paramref name="model"/> with the stored <paramref name="lists"/>.</summary>
    public static ModelEditViewModel ForClassList(VisionModel model, IReadOnlyList<ClassList> lists)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(lists);
        return new(false, model, lists, _ => false);
    }

    /// <summary>OK (true) or Cancel (false): the view closes the window.</summary>
    public event EventHandler<bool>? CloseRequested;

    public bool IsRename { get; }

    public bool IsClassList => !IsRename;

    public string Title => IsRename ? VisionTexts.RenameModel : VisionTexts.ClassListTitle;

    public string ConfirmText => IsRename ? VisionTexts.Rename : VisionTexts.Save;

    public static int MaxNameLength => VisionLimits.MaxNameLength;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial string Name { get; set; }

    /// <summary>"Own names", then the lists that fit the model's class count.</summary>
    public IReadOnlyList<ClassListChoice> Choices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOwn), nameof(ChosenNames))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial ClassListChoice? SelectedChoice { get; set; }

    /// <summary>"Own names" chosen: the names field shows.</summary>
    public bool IsOwn => IsClassList && SelectedChoice?.Id is null;

    /// <summary>The model's own names, editable (as many as it has classes).</summary>
    public ClassNamesEditor Own { get; }

    /// <summary>A list chosen: its names, read only.</summary>
    public string ChosenNames => SelectedChoice?.Id is { } id && _lists.FirstOrDefault(l => l.Id == id) is { } list
        ? string.Join(Environment.NewLine, list.Names)
        : string.Empty;

    /// <summary>The model's class list after OK: the list's id, null for its own names.</summary>
    public string? ClassListId => SelectedChoice?.Id;

    /// <summary>The names in effect after OK.</summary>
    public IReadOnlyList<string> ClassNames => SelectedChoice?.Id is { } id && _lists.FirstOrDefault(l => l.Id == id) is { } list ? list.Names : Own.Names;

    private bool CanOk() => IsFormValid && (!IsOwn || Own.IsFormValid);

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        Validation.ShowAll();
        if (IsOwn)
        {
            Own.ShowAllErrors();
        }

        if (CanOk())
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    protected override void OnValidationChanged() => OkCommand.NotifyCanExecuteChanged();
}
