using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Actions;

namespace Oadm.Core.Events;

/// <summary>The checks of a recipient's values, for a save and a test.</summary>
public static class RecipientChecks
{
    /// <summary>
    /// Errors per field: the host's checks of the type's fields, then the test action's own
    /// <see cref="IActionPlugin.Validate"/> with its default values, of which only the type's fields count.
    /// <paramref name="storedSecrets"/>: password fields with a stored value.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Errors(CatalogRecipientType type, JsonObject values, IReadOnlyCollection<string> storedSecrets)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(values);
        var fields = type.Type.Fields;
        var errors = ActionSettings.Check(fields, values, statefulRule: true, storedSecrets);
        if (errors.Count > 0 || type.TestAction is not { Available: true } action)
        {
            return errors;
        }

        try
        {
            using var settings = JsonDocument.Parse(WithDefaults(action.Plugin, values).ToJsonString());
            return action.Plugin.Validate(settings.RootElement, statefulRule: true)
                .Where(e => fields.Any(f => f.Key == e.Key))
                .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [string.Empty] = string.IsNullOrWhiteSpace(ex.Message) ? $"The recipient cannot be checked ({ex.GetType().Name})." : ex.Message,
            };
        }
    }

    /// <summary>The action's default settings with a recipient's <paramref name="values"/> over them.</summary>
    public static JsonObject WithDefaults(IActionPlugin plugin, JsonObject values)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(values);
        var settings = ActionSettings.Defaults(plugin.Fields);
        foreach (var (key, value) in values)
        {
            settings[key] = value?.DeepClone();
        }

        return settings;
    }
}

/// <summary>Which rules use which recipient: one walk over the rules per list, not one per recipient.</summary>
public static class RecipientUse
{
    /// <summary>
    /// The sorted rule names per recipient of <paramref name="recipientIds"/>. A setting links to a recipient when its text
    /// is the recipient's id, so an action of a plugin that is not installed still counts.
    /// </summary>
    public static Dictionary<Guid, List<string>> ByRecipient(IEnumerable<StoredRule> rules, IEnumerable<Guid> recipientIds)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(recipientIds);
        var byText = recipientIds.Distinct().ToDictionary(id => id.ToString(), id => id, StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<Guid, List<string>>();
        foreach (var rule in rules)
        {
            var seen = new HashSet<Guid>();
            foreach (var value in rule.Definition.Actions.SelectMany(a => a.Settings.Select(p => p.Value)))
            {
                if (value is JsonValue text && text.TryGetValue<string>(out var id) && byText.TryGetValue(id, out var recipient) && seen.Add(recipient))
                {
                    if (!names.TryGetValue(recipient, out var list))
                    {
                        names[recipient] = list = [];
                    }

                    list.Add(rule.Definition.Name);
                }
            }
        }

        foreach (var list in names.Values)
        {
            list.Sort(StringComparer.CurrentCultureIgnoreCase);
        }

        return names;
    }
}
