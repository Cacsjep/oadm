using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.VapixCommander.Client;

/// <summary>An enum option in a field's select box.</summary>
public sealed record FieldOptionItem(JsonElement Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One input of a command: text, number, password, yes/no or a select box, checked with the same rules as the server
/// (<see cref="FieldValues.Convert"/>) while typing. The error is reported on the bound input property
/// (<see cref="Text"/> or <see cref="SelectedOption"/>) and shows below the input once it was edited or the rollout
/// was started (<see cref="ShowErrors"/>).
/// </summary>
public sealed partial class FieldViewModel : ValidatingViewModel
{
    public FieldViewModel(CommandField field, bool hasStoredSecret = false)
    {
        ArgumentNullException.ThrowIfNull(field);
        Field = field;
        HasStoredSecret = hasStoredSecret;
        Validation.Rule(field.Type == FieldTypes.Enum ? nameof(SelectedOption) : nameof(Text), Check);
        Options = [.. (field.Options ?? []).Select(o => new FieldOptionItem(o.Value, string.IsNullOrEmpty(o.Label) ? FieldValues.TextOf(o.Value) : o.Label))];
        if (field.Default is { } value && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (field.Type == FieldTypes.Boolean)
            {
                BoolValue = value.ValueKind == JsonValueKind.True
                    || (value.ValueKind == JsonValueKind.String && value.GetString() is "yes" or "true" or "1" or "on");
            }
            else if (field.Type == FieldTypes.Enum)
            {
                SelectedOption = Options.FirstOrDefault(o => FieldValues.TextOf(o.Value) == FieldValues.TextOf(value));
            }
            else
            {
                Text = FieldValues.TextOf(value);
            }
        }
        else if (field.Type == FieldTypes.Enum && field.IsRequired)
        {
            SelectedOption = Options.FirstOrDefault();
        }

        Validate();
        Validation.Reset(); // defaults are not edits: errors show once the user edits the field or starts the rollout
    }

    public CommandField Field { get; }

    public string Name => Field.Name;

    public string Label => Field.IsRequired || Field.Type == FieldTypes.Boolean ? Field.Label : Field.Label + " (optional)";

    public string? Hint
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Field.Description))
            {
                parts.Add(Field.Description);
            }

            if (Field.Type is FieldTypes.Integer or FieldTypes.Number && (Field.Min is not null || Field.Max is not null))
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Field.Min?.ToString(CultureInfo.InvariantCulture) ?? "…"} to {Field.Max?.ToString(CultureInfo.InvariantCulture) ?? "…"}{(Field.Unit is null ? string.Empty : " " + Field.Unit)}"));
            }
            else if (Field.Unit is not null)
            {
                parts.Add(Field.Unit);
            }

            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    public bool HasStoredSecret { get; }

    public bool IsTextInput => Field.Type is FieldTypes.String or FieldTypes.Integer or FieldTypes.Number;

    public bool IsPassword => Field.Type == FieldTypes.Password;

    public bool IsBoolean => Field.Type == FieldTypes.Boolean;

    public bool IsEnum => Field.Type == FieldTypes.Enum;

    public string Placeholder => IsPassword && HasStoredSecret ? "Stored on the server (leave empty to use it)"
        : Field.Type switch
        {
            FieldTypes.Integer => "Whole number",
            FieldTypes.Number => "Number",
            _ => string.Empty,
        };

    public ObservableCollection<FieldOptionItem> Options { get; }

    [ObservableProperty]
    public partial string? Text { get; set; }

    [ObservableProperty]
    public partial bool BoolValue { get; set; }

    [ObservableProperty]
    public partial FieldOptionItem? SelectedOption { get; set; }

    /// <summary>The current error (shown or not yet shown); blocks the rollout.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>The rollout was started: the error shows below the input even when it was not edited.</summary>
    public void ShowErrors() => Validation.ShowAll();

    /// <summary>Raised after the value changed.</summary>
    public event EventHandler? ValueChanged;

    partial void OnTextChanged(string? value) => Changed();

    partial void OnBoolValueChanged(bool value) => Changed();

    partial void OnSelectedOptionChanged(FieldOptionItem? value) => Changed();

    /// <summary>The value to send, or null when empty (the server then uses the default or a stored password).</summary>
    public JsonElement? Value => Field.Type switch
    {
        FieldTypes.Boolean => JsonSerializer.SerializeToElement(BoolValue),
        FieldTypes.Enum => SelectedOption?.Value,
        _ => string.IsNullOrEmpty(Text) ? null : JsonSerializer.SerializeToElement(Text),
    };

    /// <summary>Checks the value like the server does; sets <see cref="Error"/>.</summary>
    public bool Validate()
    {
        Validation.Validate();
        Error = Validation.ErrorOf(Field.Type == FieldTypes.Enum ? nameof(SelectedOption) : nameof(Text));
        return Error is null;
    }

    private string? Check()
    {
        var value = Value;
        if (value is null)
        {
            var hasDefault = Field.Default is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } d
                && !(d.ValueKind == JsonValueKind.String && d.GetString()!.Length == 0) && !Field.IsSecret;
            return Field.IsRequired && !hasDefault && !(IsPassword && HasStoredSecret) ? "Required." : null;
        }

        var error = FieldValues.Convert(Field, value.Value, out _);
        return error is null ? null : char.ToUpperInvariant(error[0]) + error[1..];
    }

    private void Changed()
    {
        Validate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>One command of the rollout set: its definition and the entered values.</summary>
public sealed partial class RolloutCommandViewModel : ObservableObject
{
    public RolloutCommandViewModel(CommandListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
        Fields = [.. item.Command.Fields.Select(f => new FieldViewModel(f, item.StoredSecretFields.Contains(f.Name)))];
        foreach (var field in Fields)
        {
            field.ValueChanged += (_, _) => OnPropertyChanged(nameof(IsValid));
        }
    }

    public CommandListItem Item { get; }

    public CommandDefinition Command => Item.Command;

    public string Name => Command.Name;

    public string Category => Command.Category;

    public string SourceText => Item.Source switch
    {
        CommandSources.Saved => "Saved",
        CommandSources.Inline => "Raw request",
        _ => "Built-in",
    };

    /// <summary>"Read", "Write" or "Write · dangerous".</summary>
    public string KindText => Command.Dangerous ? "Write · dangerous" : Command.Writes ? "Write" : "Read";

    /// <summary>Header text of the selected command: kind of a changing command, then the description.</summary>
    public string? Detail => Command.Writes || Command.Dangerous
        ? string.IsNullOrEmpty(Command.Description) ? KindText : KindText + " · " + Command.Description
        : Command.Description;

    public bool Writes => Command.Writes;

    public bool Dangerous => Command.Dangerous;

    public ObservableCollection<FieldViewModel> Fields { get; }

    public bool HasFields => Fields.Count > 0;

    [ObservableProperty]
    public partial int Position { get; set; }

    public bool IsValid => Fields.All(f => !f.HasError);

    /// <summary>The rollout was started with this command: every field error shows below its input.</summary>
    public void ShowErrors()
    {
        foreach (var field in Fields)
        {
            field.ShowErrors();
        }
    }

    public CommandRef Ref => Item.Source == CommandSources.Inline
        ? new CommandRef { Source = CommandSources.Inline, Command = Command }
        : new CommandRef { Source = Item.Source, Id = Command.Id };

    /// <summary>Entered values (empty ones left out).</summary>
    public Dictionary<string, JsonElement> Values()
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            if (field.Value is { } value)
            {
                values[field.Name] = value;
            }
        }

        return values;
    }

    /// <summary>A copy of the command with the entered values as defaults (to save it as a preset).</summary>
    public CommandDefinition WithValuesAsDefaults()
    {
        var copy = Command.Clone();
        var values = Values();
        foreach (var field in copy.Fields)
        {
            if (values.TryGetValue(field.Name, out var value))
            {
                // Typed like the format: numbers and booleans unquoted.
                field.Default = FieldValues.Convert(field, value, out var typed) is null && typed is not null
                    ? JsonSerializer.SerializeToElement(typed, typed.GetType())
                    : value;
            }
        }

        return copy;
    }
}
