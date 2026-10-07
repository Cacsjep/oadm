using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Oadm.Plugins.VapixCommander.Client;

/// <summary>A key/value row of the query or header table.</summary>
public sealed partial class KeyValueRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Key { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;
}

/// <summary>A {{placeholder}} of the raw request: name, type and the value used for "Send" (saved as the default).</summary>
public sealed partial class RawFieldViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Type { get; set; } = FieldTypes.String;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    public bool IsPassword => Type == FieldTypes.Password;

    /// <summary>"{{name}}" as used in the request.</summary>
    public string Placeholder => "{{" + Name + "}}";

    partial void OnTypeChanged(string value) => OnPropertyChanged(nameof(IsPassword));

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Placeholder));
}

/// <summary>
/// Postman-like raw request editor: method, path, query and header tables, body (json/form/text/xml), timeout and
/// response kind. "Make field" turns a value into a {{placeholder}}; placeholders typed by hand get a field
/// automatically. "Save as command" builds a format-v1 command (requires prefilled from the path).
/// </summary>
public sealed partial class RawEditorViewModel : ObservableObject
{
    private static readonly Dictionary<string, (string Api, string Version, string Kind)> KnownCgis = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/axis-cgi/param.cgi"] = ("param-cgi", "1.0", ResponseKinds.ParamCgi),
        ["/axis-cgi/basicdeviceinfo.cgi"] = ("basic-device-info", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/apidiscovery.cgi"] = ("api-discovery", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/network_settings.cgi"] = ("network-settings", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/pwdgrp.cgi"] = ("user-management", "1.0", ResponseKinds.Text),
        ["/axis-cgi/firmwaremanagement.cgi"] = ("fwmgr", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/time.cgi"] = ("time-service", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/ntp.cgi"] = ("ntp", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/mqtt/client.cgi"] = ("mqtt-client", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/lightcontrol.cgi"] = ("light-control", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/io/portmanagement.cgi"] = ("io-port-management", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/daynight.cgi"] = ("daynight", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/systemready.cgi"] = ("systemready", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/applications/list.cgi"] = ("application", "1.0", ResponseKinds.Xml),
        ["/axis-cgi/applications/control.cgi"] = ("application", "1.0", ResponseKinds.Text),
        ["/axis-cgi/restart.cgi"] = ("param-cgi", "1.0", ResponseKinds.Text),
    };

    [GeneratedRegex("^/config/rest/([a-z0-9-]+)/v([0-9]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RestPath();

    private bool _requiresEdited;
    private bool _kindEdited;
    private bool _writesEdited;
    private bool _suppressEdits;

    public RawEditorViewModel()
    {
        QueryRows.CollectionChanged += (_, e) => RowsChanged(e.NewItems);
        HeaderRows.CollectionChanged += (_, e) => RowsChanged(e.NewItems);
        QueryRows.Add(new KeyValueRowViewModel { Key = "action", Value = "list" });
        QueryRows.Add(new KeyValueRowViewModel { Key = "group", Value = "Brand" });
        ApplyPathDefaults();
    }

    public static IReadOnlyList<string> Methods => HttpMethods.All;

    public static IReadOnlyList<string> BodyTypeOptions => BodyTypes.All;

    public static IReadOnlyList<string> ResponseKindOptions => ResponseKinds.All;

    public static IReadOnlyList<string> FieldTypeOptions => FieldTypes.All.Where(t => t != FieldTypes.Enum).ToList();

    public static IReadOnlyList<string> Categories => CommandCategories.All;

    [ObservableProperty]
    public partial string Method { get; set; } = "GET";

    [ObservableProperty]
    public partial string Path { get; set; } = "/axis-cgi/param.cgi";

    public ObservableCollection<KeyValueRowViewModel> QueryRows { get; } = [];

    public ObservableCollection<KeyValueRowViewModel> HeaderRows { get; } = [];

    [ObservableProperty]
    public partial KeyValueRowViewModel? SelectedQueryRow { get; set; }

    [ObservableProperty]
    public partial KeyValueRowViewModel? SelectedHeaderRow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBody))]
    public partial string BodyType { get; set; } = BodyTypes.None;

    [ObservableProperty]
    public partial string BodyText { get; set; } = string.Empty;

    public bool HasBody => BodyType != BodyTypes.None;

    [ObservableProperty]
    public partial decimal? TimeoutSeconds { get; set; } = CommandLimits.DefaultTimeoutSeconds;

    [ObservableProperty]
    public partial string ResponseKind { get; set; } = ResponseKinds.ParamCgi;

    public ObservableCollection<RawFieldViewModel> Fields { get; } = [];

    public bool HasFields => Fields.Count > 0;

    // "Save as command"
    [ObservableProperty]
    public partial bool IsSaveOpen { get; set; }

    [ObservableProperty]
    public partial string SaveName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SaveCategory { get; set; } = CommandCategories.Custom;

    [ObservableProperty]
    public partial string SaveDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RequiresApi { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RequiresVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Writes { get; set; }

    [ObservableProperty]
    public partial bool Dangerous { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>The page saves the built command (<see cref="CommanderViewModel"/> handles it).</summary>
    public event EventHandler<CommandDefinition>? SaveRequested;

    partial void OnPathChanged(string value) => ApplyPathDefaults();

    partial void OnMethodChanged(string value) => ApplyPathDefaults();

    partial void OnBodyTextChanged(string value) => SyncFields();

    partial void OnRequiresApiChanged(string value) => _requiresEdited |= !_suppressEdits;

    partial void OnRequiresVersionChanged(string value) => _requiresEdited |= !_suppressEdits;

    partial void OnResponseKindChanged(string value) => _kindEdited |= !_suppressEdits;

    partial void OnWritesChanged(bool value) => _writesEdited |= !_suppressEdits;

    [RelayCommand]
    private void AddQueryRow() => QueryRows.Add(new KeyValueRowViewModel());

    [RelayCommand]
    private void RemoveQueryRow()
    {
        if (SelectedQueryRow is { } row)
        {
            QueryRows.Remove(row);
            SyncFields();
        }
    }

    [RelayCommand]
    private void AddHeaderRow() => HeaderRows.Add(new KeyValueRowViewModel());

    [RelayCommand]
    private void RemoveHeaderRow()
    {
        if (SelectedHeaderRow is { } row)
        {
            HeaderRows.Remove(row);
            SyncFields();
        }
    }

    /// <summary>Turns the selected query value into a {{placeholder}} field (name from the key, type from the value).</summary>
    [RelayCommand]
    private void MakeQueryField()
    {
        if (SelectedQueryRow is { } row)
        {
            MakeField(row);
        }
    }

    [RelayCommand]
    private void MakeHeaderField()
    {
        if (SelectedHeaderRow is { } row)
        {
            MakeField(row);
        }
    }

    [RelayCommand]
    private void OpenSave()
    {
        IsSaveOpen = true;
        if (string.IsNullOrWhiteSpace(SaveName))
        {
            SaveName = Method + " " + Path;
        }
    }

    [RelayCommand]
    private void CancelSave() => IsSaveOpen = false;

    [RelayCommand]
    private void Save()
    {
        var (command, problems) = Build(forSave: true);
        if (problems.Count > 0)
        {
            Error = string.Join(" ", problems);
            return;
        }

        Error = null;
        SaveRequested?.Invoke(this, command);
    }

    /// <summary>Called by the page after a successful save.</summary>
    public void SaveCompleted() => IsSaveOpen = false;

    /// <summary>Turns a row's value into a placeholder; returns the new field.</summary>
    public RawFieldViewModel? MakeField(KeyValueRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (Placeholders.Whole(row.Value) is { } existing)
        {
            return Fields.FirstOrDefault(f => f.Name == existing);
        }

        var name = UniqueName(FieldName(row.Key));
        var field = new RawFieldViewModel
        {
            Name = name,
            Label = Labelize(row.Key),
            Type = GuessType(row.Value),
            Value = row.Value,
        };
        Fields.Add(field);
        row.Value = "{{" + name + "}}";
        OnPropertyChanged(nameof(HasFields));
        return field;
    }

    /// <summary>The command as edited; problems from <see cref="CommandValidator"/> (empty when valid).</summary>
    public (CommandDefinition Command, IReadOnlyList<string> Problems) Build(bool forSave)
    {
        SyncFields();
        var problems = new List<string>();
        JsonNode? body = null;
        switch (BodyType)
        {
            case BodyTypes.Json:
                try
                {
                    body = JsonNode.Parse(string.IsNullOrWhiteSpace(BodyText) ? "{}" : BodyText);
                }
                catch (JsonException ex)
                {
                    problems.Add("The JSON body is not valid: " + ex.Message);
                }

                break;
            case BodyTypes.Form:
                body = new JsonObject(ParseForm(BodyText).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value))));
                break;
            case BodyTypes.Text or BodyTypes.Xml:
                body = JsonValue.Create(BodyText);
                break;
        }

        var command = new CommandDefinition
        {
            Id = forSave ? string.Empty : "raw.request",
            Version = 1,
            Name = forSave ? SaveName.Trim() : "Raw request",
            Category = forSave ? SaveCategory : CommandCategories.Custom,
            Description = forSave && !string.IsNullOrWhiteSpace(SaveDescription) ? SaveDescription.Trim() : null,
            Requires = string.IsNullOrWhiteSpace(RequiresApi) ? [] : [new ApiRequirement { Api = RequiresApi.Trim(), MinVersion = RequiresVersion.Trim() }],
            Writes = Writes,
            Dangerous = Dangerous,
            Fields = [.. Fields.Select(ToField)],
            Request = new CommandRequest
            {
                Method = Method,
                Path = Path.Trim(),
                Query = !QueryRows.Any(r => !string.IsNullOrWhiteSpace(r.Key)) ? null
                    : QueryRows.Where(r => !string.IsNullOrWhiteSpace(r.Key)).GroupBy(r => r.Key.Trim()).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal),
                Headers = !HeaderRows.Any(r => !string.IsNullOrWhiteSpace(r.Key)) ? null
                    : HeaderRows.Where(r => !string.IsNullOrWhiteSpace(r.Key)).GroupBy(r => r.Key.Trim()).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal),
                BodyType = BodyType == BodyTypes.None ? null : BodyType,
                Body = body,
                TimeoutSeconds = TimeoutSeconds is { } t ? (int)t : null,
            },
            Response = new CommandResponse { Kind = ResponseKind },
        };

        if (problems.Count == 0)
        {
            // The id is generated by the server on save; the raw request needs no name.
            var check = command.Clone();
            check.Id = "custom.new";
            if (!forSave)
            {
                check.Name = "Raw request";
            }

            problems.AddRange(CommandValidator.Validate(check));
        }

        return (command, problems);
    }

    /// <summary>Values for "Send" (the field values; empty ones left out).</summary>
    public Dictionary<string, JsonElement> Values() =>
        Fields.Where(f => !string.IsNullOrEmpty(f.Value)).ToDictionary(f => f.Name, f => JsonSerializer.SerializeToElement(f.Value), StringComparer.Ordinal);

    /// <summary>Adds a field for every {{placeholder}} typed by hand and removes unused ones.</summary>
    public void SyncFields()
    {
        var used = Placeholders.In(Path)
            .Concat(QueryRows.SelectMany(r => Placeholders.In(r.Key).Concat(Placeholders.In(r.Value))))
            .Concat(HeaderRows.SelectMany(r => Placeholders.In(r.Key).Concat(Placeholders.In(r.Value))))
            .Concat(Placeholders.In(BodyText))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var name in used.Where(n => Fields.All(f => f.Name != n)))
        {
            Fields.Add(new RawFieldViewModel { Name = name, Label = Labelize(name) });
        }

        foreach (var stale in Fields.Where(f => !used.Contains(f.Name)).ToList())
        {
            Fields.Remove(stale);
        }

        OnPropertyChanged(nameof(HasFields));
    }

    private static CommandField ToField(RawFieldViewModel field)
    {
        var result = new CommandField
        {
            Name = field.Name,
            Label = string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label,
            Type = field.Type,
        };
        if (!string.IsNullOrEmpty(field.Value))
        {
            result.Default = field.Type switch
            {
                FieldTypes.Integer when long.TryParse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => JsonSerializer.SerializeToElement(l),
                FieldTypes.Number when double.TryParse(field.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => JsonSerializer.SerializeToElement(d),
                FieldTypes.Boolean => JsonSerializer.SerializeToElement(field.Value is "yes" or "true" or "1" or "on"),
                _ => JsonSerializer.SerializeToElement(field.Value),
            };
        }

        return result;
    }

    private void ApplyPathDefaults()
    {
        var path = (Path ?? string.Empty).Split('?')[0].Trim();
        (string Api, string Version, string Kind)? known = KnownCgis.TryGetValue(path, out var hit) ? hit : null;
        if (known is null && RestPath().Match(path) is { Success: true } rest)
        {
            known = (rest.Groups[1].Value.ToLowerInvariant(), rest.Groups[2].Value + ".0", ResponseKinds.Rest);
        }

        _suppressEdits = true;
        try
        {
            if (!_requiresEdited && known is { } k)
            {
                RequiresApi = k.Api;
                RequiresVersion = k.Version;
            }

            if (!_kindEdited)
            {
                ResponseKind = known?.Kind ?? (path.StartsWith("/vapix/", StringComparison.OrdinalIgnoreCase) ? ResponseKinds.Xml : ResponseKinds.Text);
            }

            if (!_writesEdited)
            {
                var action = QueryRows.FirstOrDefault(r => r.Key == "action")?.Value ?? string.Empty;
                Writes = Method != "GET" && !path.EndsWith("basicdeviceinfo.cgi", StringComparison.OrdinalIgnoreCase)
                    || action is "update" or "add" or "remove" or "set" or "start" or "stop";
            }
        }
        finally
        {
            _suppressEdits = false;
        }
    }

    private void RowsChanged(System.Collections.IList? added)
    {
        Watch(added);
        if (!_suppressEdits)
        {
            SyncFields();
        }
    }

    private void Watch(System.Collections.IList? rows)
    {
        foreach (KeyValueRowViewModel row in rows ?? Array.Empty<KeyValueRowViewModel>())
        {
            row.PropertyChanged += (_, _) =>
            {
                ApplyPathDefaults();
                SyncFields();
            };
        }
    }

    private string UniqueName(string name)
    {
        var unique = name;
        for (var i = 2; Fields.Any(f => f.Name == unique); i++)
        {
            unique = name + i.ToString(CultureInfo.InvariantCulture);
        }

        return unique;
    }

    /// <summary>"ImageSource.I0.DayNight.ShiftLevel" becomes "shiftLevel".</summary>
    private static string FieldName(string key)
    {
        var last = key.Split('.', '/', '[', ']').LastOrDefault(s => s.Length > 0) ?? "value";
        var letters = new string([.. last.Where(char.IsAsciiLetterOrDigit)]);
        if (letters.Length == 0 || !char.IsAsciiLetter(letters[0]))
        {
            letters = "value" + letters;
        }

        return char.ToLowerInvariant(letters[0]) + letters[1..];
    }

    private static string Labelize(string key)
    {
        var last = key.Split('.').LastOrDefault(s => s.Length > 0) ?? key;
        return string.IsNullOrEmpty(last) ? key : char.ToUpperInvariant(last[0]) + last[1..];
    }

    private static string GuessType(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? FieldTypes.Integer
        : value is "yes" or "no" or "true" or "false" ? FieldTypes.Boolean
        : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? FieldTypes.Number
        : FieldTypes.String;

    private static IEnumerable<KeyValuePair<string, string>> ParseForm(string text)
    {
        foreach (var line in (text ?? string.Empty).Split(['&', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            yield return eq < 0
                ? new(line.Trim(), string.Empty)
                : new(line[..eq].Trim(), line[(eq + 1)..].TrimEnd('\r'));
        }
    }
}
