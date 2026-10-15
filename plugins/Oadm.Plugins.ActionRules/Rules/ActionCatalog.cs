using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.ActionRules.Rules;

/// <summary>Kinds of setting fields in the action forms.</summary>
public static class FieldKinds
{
    public const string Text = "text";
    public const string Multiline = "multiline";
    public const string Number = "number";
    public const string Bool = "bool";
    public const string Choice = "choice";
    public const string Password = "password";

    /// <summary>Never shown: the device's value stays (or the default for a new rule).</summary>
    public const string Hidden = "hidden";
}

/// <summary>A choice of a select field: the value as the device stores it and its label.</summary>
public sealed record FieldChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>How the forms show one setting of an action or recipient (from the bundled catalog).</summary>
public sealed record CatalogField
{
    public string? Label { get; init; }

    public string Kind { get; init; } = FieldKinds.Text;

    /// <summary>The value a new rule starts with, exactly as the device stores it.</summary>
    public string? Default { get; init; }

    public bool Required { get; init; }

    public decimal? Min { get; init; }

    public decimal? Max { get; init; }

    public IReadOnlyList<FieldChoice>? Choices { get; init; }

    /// <summary>The stored values of a check box.</summary>
    [JsonPropertyName("true")]
    public string? TrueValue { get; init; }

    [JsonPropertyName("false")]
    public string? FalseValue { get; init; }

    public string? Hint { get; init; }
}

/// <summary>An action or recipient template of the catalog: its name and its settings in form order.</summary>
public sealed record CatalogTemplate
{
    public string? Label { get; init; }

    public Dictionary<string, CatalogField> Fields { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The bundled action catalog (<c>Catalog/action-catalog.json</c>, embedded): labels, field kinds, choices and defaults of
/// every action and recipient template the camera's web interface knows, so the forms show "Repeat every (s)" instead of
/// "period". A template or setting it does not know is a text field named after the parameter.
/// </summary>
public static class ActionCatalog
{
    private static readonly Lazy<(Dictionary<string, CatalogTemplate> Actions, Dictionary<string, CatalogTemplate> Recipients)> Data = new(Load);

    /// <summary>How the form shows <paramref name="parameter"/> of the action <paramref name="template"/> (its recipient's settings too).</summary>
    public static CatalogField Field(string template, string? recipientTemplate, string parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (template is not null && Data.Value.Actions.TryGetValue(template, out var action) && action.Fields.TryGetValue(parameter, out var field))
        {
            return Complete(field, parameter);
        }

        if (recipientTemplate is not null && Data.Value.Recipients.TryGetValue(recipientTemplate, out var recipient) && recipient.Fields.TryGetValue(parameter, out var own))
        {
            return Complete(own, parameter);
        }

        return Fallback(parameter);
    }

    /// <summary>How a recipient form shows one of its settings.</summary>
    public static CatalogField RecipientField(string recipientTemplate, string parameter) => Field(string.Empty, recipientTemplate, parameter);

    /// <summary>
    /// The field for a value the form must give (a new rule, a setting a rule lacks): a hidden setting without a default is
    /// shown as a text field, so nothing is written empty without the user seeing it.
    /// </summary>
    public static CatalogField ForNewValue(CatalogField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Kind == FieldKinds.Hidden && string.IsNullOrEmpty(field.Default) ? field with { Kind = FieldKinds.Text } : field;
    }

    /// <summary>The settings in form order: the catalog's order, then those it does not list.</summary>
    public static IReadOnlyList<string> Order(string template, string? recipientTemplate, IEnumerable<string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var known = new List<string>();
        if (template is not null && Data.Value.Actions.TryGetValue(template, out var action))
        {
            known.AddRange(action.Fields.Keys);
        }

        if (recipientTemplate is not null && Data.Value.Recipients.TryGetValue(recipientTemplate, out var recipient))
        {
            known.AddRange(recipient.Fields.Keys);
        }

        var given = parameters.Distinct(StringComparer.Ordinal).ToList();
        var rank = known.Distinct(StringComparer.Ordinal).Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);
        return [.. given.OrderBy(n => rank.TryGetValue(n, out var i) ? i : int.MaxValue).ThenBy(n => given.IndexOf(n))];
    }

    /// <summary>The catalog's name of a recipient template ("FTP"), or null.</summary>
    public static string? RecipientLabel(string recipientTemplate) =>
        Data.Value.Recipients.TryGetValue(recipientTemplate, out var r) ? r.Label : null;

    /// <summary>The catalog's name of an action template, or null.</summary>
    public static string? ActionLabel(string template) =>
        Data.Value.Actions.TryGetValue(template, out var a) ? a.Label : null;

    private static CatalogField Complete(CatalogField field, string parameter) =>
        field with
        {
            Label = string.IsNullOrWhiteSpace(field.Label) ? Humanize(parameter) : field.Label,
            Kind = RuleText.IsSecret(parameter) && field.Kind != FieldKinds.Hidden ? FieldKinds.Password : field.Kind,
        };

    private static CatalogField Fallback(string parameter) => new()
    {
        Label = Humanize(parameter),
        Kind = RuleText.IsSecret(parameter) ? FieldKinds.Password : FieldKinds.Text,
    };

    /// <summary>"upload_path" -> "Upload path".</summary>
    public static string Humanize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var text = name.Replace('_', ' ').Trim();
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static (Dictionary<string, CatalogTemplate>, Dictionary<string, CatalogTemplate>) Load()
    {
        using var stream = typeof(ActionCatalog).Assembly.GetManifestResourceStream("Oadm.Plugins.ActionRules.Catalog.action-catalog.json");
        if (stream is null)
        {
            return (new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        }

        var file = JsonSerializer.Deserialize<CatalogFile>(stream, ActionRulesJson.Options) ?? new CatalogFile();
        return (new(file.Actions, StringComparer.Ordinal), new(file.Recipients, StringComparer.Ordinal));
    }

    private sealed record CatalogFile
    {
        public Dictionary<string, CatalogTemplate> Actions { get; init; } = [];

        public Dictionary<string, CatalogTemplate> Recipients { get; init; } = [];
    }
}
