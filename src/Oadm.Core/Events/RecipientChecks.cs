using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Certificates;

namespace Oadm.Core.Events;

/// <summary>The checks of a recipient's values, for a save and a test.</summary>
public static class RecipientChecks
{
    /// <summary>A stored password never follows a changed target.</summary>
    public const string PasswordAgain = "Enter the password again. The target changed.";

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

    /// <summary>
    /// The checks that compare with the stored recipient, after <see cref="Errors"/>. Throws
    /// <see cref="RecipientValidationException"/> when a visible password left empty would go to a changed target, or a certificate
    /// value that is new is not on the list PKI offers: hidden fields count, a hidden value can become visible unchanged.
    /// </summary>
    public static async Task CheckAgainstStoredAsync(IReadOnlyList<ActionField> fields, JsonObject values, StoredRecipient? recipient, IRecipientCertificates? certificates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(values);
        if (recipient is not null && TargetChanged(fields, values, recipient.Settings))
        {
            var settings = WithDefaults(fields, values);
            var secrets = RecipientStore.ForClient(recipient).StoredSecretFields;
            if (fields.FirstOrDefault(f => EmptyStoredPassword(f, values, secrets) && ActionSettings.IsVisible(f, settings, statefulRule: true)) is { } field)
            {
                throw new RecipientValidationException(field.Key, PasswordAgain);
            }
        }

        HashSet<string>? client = null;
        HashSet<string>? trusted = null;
        foreach (var field in fields.Where(IsCertificate))
        {
            if (ActionSettings.Text(values, field.Key) is not { Length: > 0 } id
                || (recipient is not null && string.Equals(ActionSettings.Text(recipient.Settings, field.Key), id, StringComparison.Ordinal)))
            {
                continue;
            }

            if (certificates is null)
            {
                throw new RecipientValidationException(field.Key, RecipientSessionTexts.CertificateGone);
            }

            var listed = field.Type == ActionFieldType.ClientCertificate
                ? client ??= Ids(await certificates.ClientCertificatesAsync(ct).ConfigureAwait(false))
                : trusted ??= Ids(await certificates.TrustedCertificatesAsync(ct).ConfigureAwait(false));
            if (!listed.Contains(id))
            {
                throw new RecipientValidationException(field.Key, RecipientSessionTexts.CertificateGone);
            }
        }
    }

    /// <summary>Whether a non-password field differs from the stored values; a missing value counts as its default.</summary>
    public static bool TargetChanged(IReadOnlyList<ActionField> fields, JsonObject values, JsonObject stored)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(stored);
        var defaults = ActionSettings.Defaults(fields);
        return fields.Any(f => f.Type != ActionFieldType.Password && !string.Equals(
            ActionSettings.Text(values, f.Key) ?? ActionSettings.Text(defaults, f.Key) ?? string.Empty,
            ActionSettings.Text(stored, f.Key) ?? ActionSettings.Text(defaults, f.Key) ?? string.Empty,
            StringComparison.Ordinal));
    }

    /// <summary>
    /// The stored passwords a save drops: hidden fields left empty on a changed target. They cannot be asked for, and kept they would
    /// reach the new target once the field shows again.
    /// </summary>
    public static IReadOnlyList<string> ForgottenPasswords(IReadOnlyList<ActionField> fields, JsonObject values, StoredRecipient recipient)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(recipient);
        if (!TargetChanged(fields, values, recipient.Settings))
        {
            return [];
        }

        var settings = WithDefaults(fields, values);
        var secrets = RecipientStore.ForClient(recipient).StoredSecretFields;
        return [.. fields.Where(f => EmptyStoredPassword(f, values, secrets) && !ActionSettings.IsVisible(f, settings, statefulRule: true)).Select(f => f.Key)];
    }

    /// <summary>The certificate fields in use: visible, with a value; <paramref name="settings"/> hold the defaults.</summary>
    public static IEnumerable<(ActionField Field, string Id)> UsedCertificates(IReadOnlyList<ActionField> fields, JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var field in fields.Where(IsCertificate))
        {
            if (ActionSettings.IsVisible(field, settings, statefulRule: true) && ActionSettings.Text(settings, field.Key) is { Length: > 0 } id)
            {
                yield return (field, id);
            }
        }
    }

    private static bool IsCertificate(ActionField field) => field.Type is ActionFieldType.ClientCertificate or ActionFieldType.TrustedCertificate;

    /// <summary>A password field with a stored value and no new one; an encrypted value from a client counts as none, as in a save.</summary>
    private static bool EmptyStoredPassword(ActionField field, JsonObject values, IReadOnlyList<string> secrets) =>
        field.Type == ActionFieldType.Password && secrets.Contains(field.Key)
        && (ActionSettings.Text(values, field.Key) is not { Length: > 0 } text || text.StartsWith(RuleStore.SecretPrefix, StringComparison.Ordinal));

    private static HashSet<string> Ids(IReadOnlyList<ActionChoice> choices) => choices.Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>The action's default settings with a recipient's <paramref name="values"/> over them.</summary>
    public static JsonObject WithDefaults(IActionPlugin plugin, JsonObject values)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return WithDefaults(plugin.Fields, values);
    }

    /// <summary>The default values of <paramref name="fields"/> with <paramref name="values"/> over them.</summary>
    public static JsonObject WithDefaults(IReadOnlyList<ActionField> fields, JsonObject values)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(values);
        var settings = ActionSettings.Defaults(fields);
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
    /// The sorted rule names per recipient of <paramref name="recipientIds"/>. A setting links to a recipient when it parses
    /// to the recipient's id, as in a run, so an action of a plugin that is not installed still counts.
    /// </summary>
    public static Dictionary<Guid, List<string>> ByRecipient(IEnumerable<StoredRule> rules, IEnumerable<Guid> recipientIds)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(recipientIds);
        var known = recipientIds.ToHashSet();
        var names = new Dictionary<Guid, List<string>>();
        foreach (var rule in rules)
        {
            var seen = new HashSet<Guid>();
            foreach (var value in rule.Definition.Actions.SelectMany(a => a.Settings.Select(p => p.Value)))
            {
                if (value is JsonValue text && text.TryGetValue<string>(out var id) && Guid.TryParse(id, out var recipient) && known.Contains(recipient) && seen.Add(recipient))
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
