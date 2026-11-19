using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Plugins.VapixCommander;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>One account of Users: name, role, PTZ control and its password (typed once, kept on the server).</summary>
public sealed partial class UserRowViewModel : ValidatingViewModel
{
    private readonly UsersFieldViewModel _owner;

    public UserRowViewModel(UsersFieldViewModel owner, string? name, string? role, bool ptz, Guid? secretId)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Name = name ?? string.Empty;
        Role = UsersFieldViewModel.Roles.FirstOrDefault(r => string.Equals(r.Value, role, StringComparison.OrdinalIgnoreCase)) ?? UsersFieldViewModel.Roles[0];
        Ptz = ptz;
        Password = new SecretFieldViewModel(ProvisioningTexts.AccountPassword, secretId) { Help = ProvisioningTexts.SecretHelp };
        Password.Changed += (_, _) => _owner.RowChanged();
        Validation.Rule(nameof(Name), () => _owner.NameError(this));
        Validation.Reset();
        Validation.Validate();
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial Choice Role { get; set; }

    [ObservableProperty]
    public partial bool Ptz { get; set; }

    public SecretFieldViewModel Password { get; }



    public bool IsValid => Validation.IsValid && Password.IsValid;

    public string? FirstError => Validation.FirstError is { } e ? e : Password.FirstError is { } p ? $"{Name.Trim()}: {p}" : null;

    public void ShowErrors()
    {
        Validation.ShowAll();
        Password.ShowErrors();
    }

    public void Revalidate() => Validation.Validate();

    public JsonObject ToJson() => new()
    {
        ["name"] = Name.Trim(),
        ["role"] = Role.Value,
        ["ptz"] = Ptz,
        ["secretId"] = Password.StoredId?.ToString(),
    };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Name) or nameof(Role) or nameof(Ptz))
        {
            _owner.RowChanged();
        }
    }
}

/// <summary>The accounts of Users (spec node 8): Add account, a row each, Remove; at most 10, never root, each name once.</summary>
public sealed partial class UsersFieldViewModel : NodeFieldViewModel
{
    public static readonly IReadOnlyList<Choice> Roles =
    [
        new("viewer", "Viewer"),
        new("operator", "Operator"),
        new("administrator", "Administrator"),
    ];

    public UsersFieldViewModel(JsonArray? accounts)
        : base("accounts", ProvisioningTexts.Accounts)
    {
        foreach (var account in accounts?.OfType<JsonObject>() ?? [])
        {
            Rows.Add(new UserRowViewModel(this, (string?)account["name"], (string?)account["role"], account["ptz"]?.GetValueKind() == JsonValueKind.True,
                FieldRules.ToGuid(account["secretId"])));
        }

        Rows.CollectionChanged += (_, _) => RowChanged();
        Rule(nameof(Count), () => Rows.Count == 0 ? ProvisioningTexts.AddAccountFirst
            : Rows.Count > UsersNode.MaxAccounts ? $"Add at most {UsersNode.MaxAccounts} accounts." : null);
        Ready();
    }

    public ObservableCollection<UserRowViewModel> Rows { get; } = [];

    public int Count => Rows.Count;

    public string? Error => ErrorOf(nameof(Count));

    public override bool IsValid => base.IsValid && Rows.All(r => r.IsValid);

    public override string? FirstError => base.FirstError ?? Rows.Select(r => r.FirstError).FirstOrDefault(e => e is not null);

    public override IEnumerable<SecretFieldViewModel> Secrets => Rows.Select(r => r.Password);

    public override JsonNode? Value => new JsonArray([.. Rows.Select(r => (JsonNode?)r.ToJson())]);

    [RelayCommand]
    public void AddAccount()
    {
        Rows.Add(new UserRowViewModel(this, string.Empty, null, false, null));
        Validation.Touch(nameof(Count));
    }

    [RelayCommand]
    public void RemoveAccount(UserRowViewModel? row)
    {
        if (row is not null)
        {
            Rows.Remove(row);
        }
    }

    public override void ShowErrors()
    {
        base.ShowErrors();
        foreach (var row in Rows)
        {
            row.ShowErrors();
        }
    }

    /// <summary>The error of a row's name (the rules of the server: 1 to 14 letters and digits, never root, each once).</summary>
    internal string? NameError(UserRowViewModel row)
    {
        var name = row.Name.Trim();
        if (name.Length == 0)
        {
            return ProvisioningTexts.EnterAccountName;
        }

        if (name.Length > 14 || !name.All(char.IsAsciiLetterOrDigit))
        {
            return ProvisioningTexts.AccountNameRule;
        }

        if (string.Equals(name, "root", StringComparison.OrdinalIgnoreCase))
        {
            return ProvisioningTexts.RootIsSetPassword;
        }

        return Rows.TakeWhile(r => r != row).Any(r => string.Equals(r.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            ? $"The account {name} is there twice."
            : null;
    }

    internal void RowChanged()
    {
        foreach (var row in Rows)
        {
            row.Revalidate();
        }

        Validation.Validate();
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Error));
    }
}

/// <summary>A value of a VAPIX command's field: text, number, yes or no, or one of its options.</summary>
public sealed partial class CommandValueViewModel : ValidatingViewModel
{
    private readonly Action _changed;

    public CommandValueViewModel(CommandField field, JsonElement? value, Action changed)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        Options = [.. (field.Options ?? []).Select(o => new Choice(FieldValues.TextOf(o.Value), o.Label))];
        var start = value ?? field.Default;
        Text = start is { } s && s.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? FieldValues.TextOf(s) : string.Empty;
        IsChecked = start is { ValueKind: JsonValueKind.True } || (start is { ValueKind: JsonValueKind.String } t && string.Equals(t.GetString(), field.TrueValue ?? "true", StringComparison.OrdinalIgnoreCase));
        Selected = Options.FirstOrDefault(o => o.Value == Text);
        Validation.Rule(nameof(Text), () => IsText ? Error() : null);
        Validation.Rule(nameof(Selected), () => IsEnum ? Error() : null);
        Validation.Reset();
        Validation.Validate();
    }

    public CommandField Field { get; }

    /// <summary>The label; "Optional" in the hint for an optional field.</summary>
    public string Label => Field.Label.Length > 0 ? Field.Label : Field.Name;

    public string? Hint => Field.Description ?? (Field.IsRequired ? null : ProvisioningTexts.Optional);

    public bool IsText => Field.Type is FieldTypes.String or FieldTypes.Integer or FieldTypes.Number;

    public bool IsBool => Field.Type == FieldTypes.Boolean;

    public bool IsEnum => Field.Type == FieldTypes.Enum;

    public IReadOnlyList<Choice> Options { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial Choice? Selected { get; set; }

    /// <summary>The value as the Commander takes it; null when nothing is entered.</summary>
    public JsonElement? Value => Field.Type switch
    {
        FieldTypes.Boolean => JsonSerializer.SerializeToElement(IsChecked),
        FieldTypes.Enum => Selected is { } o ? JsonSerializer.SerializeToElement(o.Value) : null,
        FieldTypes.Integer when long.TryParse(Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => JsonSerializer.SerializeToElement(l),
        FieldTypes.Number when double.TryParse(Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => JsonSerializer.SerializeToElement(d),
        _ => Text.Trim().Length == 0 ? null : JsonSerializer.SerializeToElement(Text.Trim()),
    };

    public bool IsValid => Validation.IsValid;

    public string? FirstError => Validation.FirstError is { } e ? $"{Label}: {e}" : null;

    public void ShowErrors() => Validation.ShowAll();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Text) or nameof(IsChecked) or nameof(Selected))
        {
            _changed();
        }
    }

    /// <summary>The Commander's own check of the value (its type, range, pattern and options).</summary>
    private string? Error()
    {
        if (Value is not { } raw)
        {
            return Field.IsRequired && Field.Default is null ? ProvisioningTexts.EnterValue
                : IsText && Text.Trim().Length > 0 ? ProvisioningTexts.EnterNumber
                : null;
        }

        return FieldValues.Convert(Field, raw, out _) is { Length: > 0 } error ? char.ToUpperInvariant(error[0]) + error[1..] : null;
    }
}

/// <summary>One command of VAPIX commands: the command of the library and the values of its fields.</summary>
public sealed partial class CommandStepViewModel : ObservableObject
{
    private readonly VapixCommandsFieldViewModel _owner;

    public CommandStepViewModel(VapixCommandsFieldViewModel owner, ProfileCommand? command, IReadOnlyDictionary<string, JsonElement>? values, string? missingId)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        MissingId = command is null ? missingId : null;
        _values = values;
        Command = command;
    }

    private readonly IReadOnlyDictionary<string, JsonElement>? _values;

    public IReadOnlyList<ProfileCommand> Commands => _owner.Commands;

    [ObservableProperty]
    public partial ProfileCommand? Command { get; set; }

    /// <summary>A stored command the library no longer has.</summary>
    public string? MissingId { get; }

    public ObservableCollection<CommandValueViewModel> Values { get; } = [];

    public string? Error => Command is null
        ? (MissingId is { } id ? $"{id} is not in the VAPIX Commander library." : ProvisioningTexts.ChooseCommand)
        : null;

    public bool IsValid => Command is not null && Values.All(v => v.IsValid);

    public string? FirstError => Error ?? (Values.Select(v => v.FirstError).FirstOrDefault(e => e is not null) is { } first ? $"{Command?.Name}: {first}" : null);

    public void ShowErrors()
    {
        foreach (var value in Values)
        {
            value.ShowErrors();
        }

        OnPropertyChanged(nameof(Error));
    }

    public JsonObject ToJson()
    {
        var values = new JsonObject();
        foreach (var value in Values)
        {
            if (value.Value is { } v)
            {
                values[value.Field.Name] = JsonNode.Parse(v.GetRawText());
            }
        }

        return new JsonObject { ["commandId"] = Command?.Id ?? MissingId, ["values"] = values };
    }

    partial void OnCommandChanged(ProfileCommand? value)
    {
        Values.Clear();
        foreach (var field in value?.Fields ?? [])
        {
            JsonElement? start = _values is not null && _values.TryGetValue(field.Name, out var v) ? v : null;
            Values.Add(new CommandValueViewModel(field, start, _owner.RowChanged));
        }

        OnPropertyChanged(nameof(Error));
        _owner.RowChanged();
    }
}

/// <summary>
/// The commands of VAPIX commands (spec node 13): Add command, the command from the VAPIX Commander library (only those a
/// profile can run: no dangerous ones, none with a password or a file) and its fields, Remove.
/// </summary>
public sealed partial class VapixCommandsFieldViewModel : NodeFieldViewModel
{
    public VapixCommandsFieldViewModel(JsonArray? steps, IReadOnlyList<ProfileCommand> commands)
        : base("commands", ProvisioningTexts.Commands)
    {
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        foreach (var step in steps?.OfType<JsonObject>() ?? [])
        {
            var id = (string?)step["commandId"];
            var values = step["values"] is JsonObject o
                ? o.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value))
                : null;
            Rows.Add(new CommandStepViewModel(this, commands.FirstOrDefault(c => c.Id == id), values, id));
        }

        Rows.CollectionChanged += (_, _) => RowChanged();
        Rule(nameof(Count), () => Rows.Count == 0 ? ProvisioningTexts.AddCommandFirst
            : Rows.Count > VapixCommandsNode.MaxCommands ? $"Add at most {VapixCommandsNode.MaxCommands} commands." : null);
        Ready();
    }

    public IReadOnlyList<ProfileCommand> Commands { get; }

    public ObservableCollection<CommandStepViewModel> Rows { get; } = [];

    public int Count => Rows.Count;

    public string? Error => ErrorOf(nameof(Count));

    public override bool IsValid => base.IsValid && Rows.All(r => r.IsValid);

    public override string? FirstError => base.FirstError ?? Rows.Select(r => r.FirstError).FirstOrDefault(e => e is not null);

    public override JsonNode? Value => new JsonArray([.. Rows.Select(r => (JsonNode?)r.ToJson())]);

    [RelayCommand]
    public void AddCommand()
    {
        Rows.Add(new CommandStepViewModel(this, null, null, null));
        Validation.Touch(nameof(Count));
    }

    [RelayCommand]
    public void RemoveCommand(CommandStepViewModel? row)
    {
        if (row is not null)
        {
            Rows.Remove(row);
        }
    }

    public override void ShowErrors()
    {
        base.ShowErrors();
        foreach (var row in Rows)
        {
            row.ShowErrors();
        }
    }

    internal void RowChanged()
    {
        Validation.Validate();
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Error));
    }
}

/// <summary>Number texts of the forms.</summary>
internal static class NumberText
{
    public static string Of(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number ? node.ToJsonString() : string.Empty;

    public static string Of(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
