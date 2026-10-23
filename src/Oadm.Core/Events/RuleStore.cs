using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Events;

/// <summary>A rule as the server keeps it: password values encrypted (<see cref="RuleStore.SecretPrefix"/>).</summary>
public sealed record StoredRule(RuleDefinition Definition, DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? LastFiredUtc);

/// <summary>
/// The event rules in the database (table EventRules) and the subjects each rule is on for (EventRuleStates). Password
/// values of action settings are stored encrypted with the master key and never leave the server: <see cref="ForClient"/>
/// removes them and lists the fields in <see cref="RuleAction.StoredSecretFields"/>; a saved rule keeps a stored value
/// when its field comes back empty. Writes are serialized.
/// </summary>
public sealed class RuleStore(IDbContextFactory<OadmDbContext> dbFactory, ISecretProtector secrets, TimeProvider time) : IDisposable
{
    /// <summary>Marks an encrypted value inside the stored settings.</summary>
    public const string SecretPrefix = "oadm-secret:";

    /// <summary>At most this many rules.</summary>
    public const int MaxRules = 500;

    private readonly SemaphoreSlim _write = new(1, 1);

    public void Dispose() => _write.Dispose();

    public async Task<IReadOnlyList<StoredRule>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.EventRules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToStored).OrderBy(r => r.Definition.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<StoredRule?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.EventRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        return row is null ? null : ToStored(row);
    }

    /// <summary>
    /// Creates (empty id) or replaces a rule. Password fields (<paramref name="passwordFields"/> per action) are
    /// encrypted; an empty one listed in <see cref="RuleAction.StoredSecretFields"/> keeps the stored value of the action
    /// with the same <see cref="RuleAction.Key"/>. Returns the stored rule and the previous version (null when new).
    /// Throws <see cref="KeyNotFoundException"/> for an unknown id and <see cref="RuleValidationException"/> over
    /// <see cref="MaxRules"/>.
    /// </summary>
    public async Task<(StoredRule Rule, StoredRule? Previous)> SaveAsync(
        RuleDefinition incoming, Func<RuleAction, IReadOnlyCollection<string>> passwordFields, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(passwordFields);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var now = time.GetUtcNow().UtcDateTime;
            EventRuleEntity? row = null;
            StoredRule? previous = null;
            if (incoming.Id != Guid.Empty)
            {
                row = await db.EventRules.FirstOrDefaultAsync(r => r.Id == incoming.Id, ct).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The rule no longer exists.");
                previous = ToStored(row);
            }
            else if (await db.EventRules.CountAsync(ct).ConfigureAwait(false) >= MaxRules)
            {
                throw new RuleValidationException($"At most {MaxRules} rules.");
            }

            var rule = incoming.Clone();
            rule.Name = rule.Name.Trim();
            if (rule.Id == Guid.Empty)
            {
                rule.Id = Guid.NewGuid();
            }

            foreach (var action in rule.Actions)
            {
                var old = previous?.Definition.Actions.FirstOrDefault(a => a.Key == action.Key);
                ProtectSettings(rule.Id, action, old, passwordFields(action));
            }

            if (row is null)
            {
                row = new EventRuleEntity { Id = rule.Id, CreatedUtc = now };
                db.EventRules.Add(row);
            }

            row.Name = rule.Name;
            row.Enabled = rule.Enabled;
            row.DefinitionJson = rule.ToJson();
            row.UpdatedUtc = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (ToStored(row), previous);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Removes the rule and its states. Returns the removed rule, null when unknown.</summary>
    public async Task<StoredRule?> DeleteAsync(Guid id, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.EventRules.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            db.EventRules.Remove(row);
            await db.EventRuleStates.Where(s => s.RuleId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToStored(row);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Turns a rule on or off. Returns the rule after and before, null when unknown.</summary>
    public async Task<(StoredRule Rule, StoredRule Previous)?> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.EventRules.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            var previous = ToStored(row);
            var rule = previous.Definition.Clone();
            rule.Enabled = enabled;
            row.Enabled = enabled;
            row.DefinitionJson = rule.ToJson();
            row.UpdatedUtc = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (ToStored(row), previous);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>
    /// A copy "&lt;name&gt; (copy)", turned off, with the stored password values (encrypted again for the copy). A value
    /// that cannot be read (older master key) stays empty in the copy.
    /// </summary>
    public async Task<StoredRule?> DuplicateAsync(Guid id, CancellationToken ct)
    {
        var source = await GetAsync(id, ct).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var copy = source.Definition.Clone();
        copy.Id = Guid.Empty;
        copy.Enabled = false;
        copy.Name = CopyName(source.Definition.Name);
        foreach (var action in copy.Actions)
        {
            foreach (var (key, node) in action.Settings.ToList())
            {
                if (IsSecret(node))
                {
                    string plain;
                    try
                    {
                        plain = secrets.Unprotect(node!.GetValue<string>()[SecretPrefix.Length..], Purpose(id, action.Key, key));
                    }
#pragma warning disable CA1031 // A value of an older master key cannot be read; the copy gets an empty field.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                        plain = string.Empty;
                    }

                    action.Settings[key] = plain; // encrypted again for the copy below (an empty one is left out)
                }
            }

            action.StoredSecretFields = [];
        }

        // By action key: a rule can hold two actions of the same type.
        var secretKeys = new Dictionary<Guid, IReadOnlyCollection<string>>();
        foreach (var action in source.Definition.Actions)
        {
            secretKeys[action.Key] = [.. action.Settings.Where(p => IsSecret(p.Value)).Select(p => p.Key)];
        }

        var (rule, _) = await SaveAsync(copy, action => secretKeys.GetValueOrDefault(action.Key) ?? [], ct).ConfigureAwait(false);
        return rule;
    }

    public async Task SetLastFiredAsync(IReadOnlyDictionary<Guid, DateTime> fired, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fired);
        if (fired.Count == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        foreach (var (id, at) in fired)
        {
            await db.EventRules.Where(r => r.Id == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.LastFiredUtc, at), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Subjects every rule is on for (server restart: compared with the fresh evaluation).</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, DateTime>>> LoadStatesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.EventRuleStates.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return rows.GroupBy(r => r.RuleId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyDictionary<Guid, DateTime>)g.ToDictionary(r => r.SubjectId, r => r.SinceUtc));
    }

    /// <summary>
    /// Applies state changes in one transaction: added (rule, subject, since) and removed (rule, subject). Changes of rules
    /// that no longer exist are skipped (serialized with <see cref="DeleteAsync"/>, so a rule cannot vanish meanwhile).
    /// </summary>
    public async Task WriteStatesAsync(
        IReadOnlyCollection<(Guid Rule, Guid Subject, DateTime Since)> added,
        IReadOnlyCollection<(Guid Rule, Guid Subject)> removed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(removed);
        if (added.Count == 0 && removed.Count == 0)
        {
            return;
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var rules = await db.EventRules.Select(r => r.Id).ToListAsync(ct).ConfigureAwait(false);
            var known = rules.ToHashSet();
            foreach (var (rule, subject) in removed)
            {
                if (!known.Contains(rule))
                {
                    continue; // deleted with its states
                }

                var row = await db.EventRuleStates.FindAsync([rule, subject], ct).ConfigureAwait(false);
                if (row is not null)
                {
                    db.EventRuleStates.Remove(row);
                }
            }

            foreach (var (rule, subject, since) in added)
            {
                if (!known.Contains(rule))
                {
                    continue; // deleted meanwhile
                }

                var row = await db.EventRuleStates.FindAsync([rule, subject], ct).ConfigureAwait(false);
                if (row is null)
                {
                    db.EventRuleStates.Add(new EventRuleStateEntity { RuleId = rule, SubjectId = subject, SinceUtc = since });
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>The rule for a client: password values removed, their fields in <see cref="RuleAction.StoredSecretFields"/>.</summary>
    public static RuleDefinition ForClient(RuleDefinition stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var rule = stored.Clone();
        foreach (var action in rule.Actions)
        {
            action.StoredSecretFields = [];
            foreach (var (key, node) in action.Settings.ToList())
            {
                if (IsSecret(node))
                {
                    action.Settings.Remove(key);
                    action.StoredSecretFields.Add(key);
                }
            }
        }

        return rule;
    }

    /// <summary>An action's settings for a run: password values decrypted. Unreadable ones (other master key) become empty.</summary>
    public JsonObject DecryptSettings(Guid ruleId, RuleAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var settings = (JsonObject)action.Settings.DeepClone();
        foreach (var (key, node) in settings.ToList())
        {
            if (!IsSecret(node))
            {
                continue;
            }

            try
            {
                settings[key] = secrets.Unprotect(node!.GetValue<string>()[SecretPrefix.Length..], Purpose(ruleId, action.Key, key));
            }
#pragma warning disable CA1031 // A value of an older master key cannot be read; the action then runs without it.
            catch (Exception)
#pragma warning restore CA1031
            {
                settings[key] = string.Empty;
            }
        }

        return settings;
    }

    private void ProtectSettings(Guid ruleId, RuleAction action, RuleAction? old, IReadOnlyCollection<string> passwordFields)
    {
        foreach (var key in passwordFields)
        {
            var value = action.Settings.TryGetPropertyValue(key, out var node) && node is not null ? node.ToString() : string.Empty;
            if (value.StartsWith(SecretPrefix, StringComparison.Ordinal))
            {
                action.Settings.Remove(key); // a client never sends encrypted values
                value = string.Empty;
            }

            if (value.Length > 0)
            {
                action.Settings[key] = SecretPrefix + secrets.Protect(value, Purpose(ruleId, action.Key, key));
            }
            else if (action.StoredSecretFields.Contains(key)
                && old?.Settings.TryGetPropertyValue(key, out var stored) == true && IsSecret(stored))
            {
                action.Settings[key] = stored!.GetValue<string>();
            }
            else
            {
                action.Settings.Remove(key);
            }
        }

        action.StoredSecretFields = [];
    }

    private static bool IsSecret(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && text.StartsWith(SecretPrefix, StringComparison.Ordinal);

    private static string Purpose(Guid ruleId, Guid actionKey, string field) => $"oadm:event-rule:{ruleId:N}:{actionKey:N}:{field}";

    private static string CopyName(string name)
    {
        var copy = name + " (copy)";
        return copy.Length <= RuleDefinition.MaxNameLength ? copy : name[..(RuleDefinition.MaxNameLength - 7)].TrimEnd() + " (copy)";
    }

    private static StoredRule ToStored(EventRuleEntity row)
    {
        RuleDefinition definition;
        try
        {
            definition = RuleDefinition.FromJson(row.DefinitionJson);
        }
        catch (System.Text.Json.JsonException)
        {
            definition = new RuleDefinition();
        }

        definition.Id = row.Id;
        definition.Name = row.Name;
        definition.Enabled = row.Enabled;
        return new StoredRule(definition, row.CreatedUtc, row.UpdatedUtc, row.LastFiredUtc);
    }

    /// <summary>The password field keys of an action (empty when the action is unknown).</summary>
    public static IReadOnlyCollection<string> PasswordFields(EventCatalog catalog, string actionId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var action = catalog.FindAction(actionId);
        if (action is null)
        {
            return [];
        }

        try
        {
            return [.. action.Plugin.Fields.Where(f => f.Type == ActionFieldType.Password).Select(f => f.Key)];
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception)
#pragma warning restore CA1031
        {
            return [];
        }
    }
}
