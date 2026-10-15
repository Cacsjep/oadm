using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.ActionRules.Rules;

namespace Oadm.Plugins.ActionRules.Client;

/// <summary>
/// One setting of an action or recipient, shown as the action catalog says (text, number, check box, select, password).
/// In the change forms Change checked = this value on every item, unchecked = each keeps its own; the rule builder sets
/// every setting. A setting the new action has and some rules lack must be set (<see cref="IsRequired"/>).
/// </summary>
public sealed partial class ParameterField : ObservableObject
{
    private readonly CatalogField _field;
    private bool _edited;

    public ParameterField(string name, CatalogField field, bool isRequired, string hint, string value)
    {
        ArgumentNullException.ThrowIfNull(field);
        Name = name;
        _field = field;
        Label = field.Label ?? ActionCatalog.Humanize(name);
        IsRequired = isRequired;
        Hint = hint.Length > 0 ? hint : field.Hint ?? string.Empty;
        var choices = (field.Choices ?? []).ToList();
        if (field.Kind == FieldKinds.Choice && value.Length > 0 && !choices.Exists(c => c.Value == value))
        {
            choices.Add(new FieldChoice(value, value)); // a value the catalog does not know stays choosable
        }

        Choices = choices;
        Value = value;
        Change = isRequired;
        _edited = false;
    }

    public string Name { get; }

    public string Label { get; }

    public string Kind => _field.Kind;

    public bool IsSecret => Kind == FieldKinds.Password;

    /// <summary>A text box: text, numbers (kept exactly as typed) and settings the catalog does not know.</summary>
    public bool IsText => Kind is not (FieldKinds.Multiline or FieldKinds.Bool or FieldKinds.Choice or FieldKinds.Password or FieldKinds.Hidden);

    public bool IsMultiline => Kind == FieldKinds.Multiline;

    public bool IsBool => Kind == FieldKinds.Bool;

    public bool IsChoice => Kind == FieldKinds.Choice;

    /// <summary>Not shown: the device's value stays, a new rule gets the default.</summary>
    public bool IsHidden => Kind == FieldKinds.Hidden;

    public bool IsShown => !IsHidden;

    public bool IsRequired { get; }

    public bool CanToggle => !IsRequired;

    public string Hint { get; }

    public IReadOnlyList<FieldChoice> Choices { get; }

    /// <summary>The rule builder: an empty value the catalog marks as required is an error.</summary>
    public bool NeedsValue { get; init; }

    [ObservableProperty]
    public partial bool Change { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; }

    /// <summary>A check box as the device stores it ("1" / "0", "true" / "false").</summary>
    public bool BoolValue
    {
        get => Value == TrueValue;
        set => Value = value ? TrueValue : FalseValue;
    }

    public FieldChoice? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => c.Value == Value);
        set
        {
            if (value is not null)
            {
                Value = value.Value;
            }
        }
    }

    private string TrueValue => _field.TrueValue ?? "1";

    private string FalseValue => _field.FalseValue ?? "0";

    /// <summary>Set by the form when Apply was tried: every error shows.</summary>
    public bool ShowErrors
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(Error));
        }
    }

    /// <summary>What is wrong with the value, shown or not (blocks Apply).</summary>
    public string? Problem
    {
        get
        {
            if (!Change || IsHidden)
            {
                return null;
            }

            var value = Value ?? string.Empty;
            if (value.Length > RulePatchApply.MaxValueLength)
            {
                return "The value is too long.";
            }

            if (value.Length == 0)
            {
                return IsChoice ? "Choose a value." : NeedsValue && _field.Required ? "Enter a value." : null;
            }

            if (Kind == FieldKinds.Number)
            {
                var ok = decimal.TryParse(value, NumberStyles.Integer | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number);
                if (!ok || number < (_field.Min ?? decimal.MinValue) || number > (_field.Max ?? decimal.MaxValue))
                {
                    return (_field.Min, _field.Max) switch
                    {
                        ({ } min, { } max) => string.Create(CultureInfo.CurrentCulture, $"Enter a number from {min:G} to {max:G}."),
                        ({ } min, null) => string.Create(CultureInfo.CurrentCulture, $"Enter a number of {min:G} or more."),
                        (null, { } max) => string.Create(CultureInfo.CurrentCulture, $"Enter a number up to {max:G}."),
                        _ => "Enter a number.",
                    };
                }
            }

            return null;
        }
    }

    /// <summary>The error below the field: once it was edited or Apply was tried.</summary>
    public string? Error => _edited || ShowErrors ? Problem : null;

    partial void OnValueChanged(string value)
    {
        _edited = true;
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(SelectedChoice));
    }

    partial void OnChangeChanged(bool value)
    {
        // A check box always holds one of its two values once it is set.
        if (value && IsBool && Value != TrueValue && Value != FalseValue)
        {
            Value = FalseValue;
        }

        OnPropertyChanged(nameof(Error));
    }
}
