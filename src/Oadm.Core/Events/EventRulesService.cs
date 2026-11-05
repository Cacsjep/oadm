using System.Globalization;
using System.Text.Json.Nodes;

using Oadm.Core.Devices;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;

namespace Oadm.Core.Events;

/// <summary>A rule in the Rules tab.</summary>
public sealed record RuleSummaryModel(
    Guid Id,
    string Name,
    bool Enabled,
    string ConditionsText,
    string ActionsText,
    bool Stateful,
    int ActiveCount,
    string StateText,
    DateTime? LastFiredUtc,
    string? Problem);

/// <summary>A recipient for a client: no password values, its one-line target and the rules that use it.</summary>
public sealed record RecipientModel(
    Guid Id,
    string Name,
    string TypeId,
    JsonObject Values,
    IReadOnlyList<string> StoredSecretFields,
    string Target,
    IReadOnlyList<string> UsedBy);

/// <summary>
/// What the gRPC EventService does with rules: list them with their live state, check and save them (the engine applies
/// the change before the call returns), turn them on or off, copy and delete them; and with recipients: list, save, delete.
/// </summary>
public sealed class EventRulesService(EventCatalog catalog, RuleStore store, RecipientStore recipients, RuleEngine engine, DeviceRepository devices) : IDisposable
{
    /// <summary>The rule names a refused delete lists; more are counted.</summary>
    private const int NamedRules = 5;

    // A rule that is being saved and a recipient that is being deleted wait for each other: the rule's check holds.
    private readonly SemaphoreSlim _links = new(1, 1);

    public EventCatalog Catalog { get; } = catalog;

    public RuleEngine Engine { get; } = engine;

    /// <summary>A rule was saved, copied, turned on or off, or deleted (the engine raises state changes itself).</summary>
    public event EventHandler? Changed;

    public void Dispose() => _links.Dispose();

    public async Task<IReadOnlyList<RuleSummaryModel>> ListAsync(CancellationToken ct)
    {
        var rules = await store.ListAsync(ct).ConfigureAwait(false);
        var names = await DeviceNamesAsync(rules.Select(r => r.Definition), ct).ConfigureAwait(false);
        var linked = await RecipientsByIdAsync(ct).ConfigureAwait(false);
        return [.. rules.Select(r => Summary(r, names, linked))];
    }

    /// <summary>The rule for a client (password values removed) and its summary; null when unknown.</summary>
    public async Task<(RuleDefinition Definition, RuleSummaryModel Summary)?> GetAsync(Guid id, CancellationToken ct)
    {
        var rule = await store.GetAsync(id, ct).ConfigureAwait(false);
        if (rule is null)
        {
            return null;
        }

        var names = await DeviceNamesAsync([rule.Definition], ct).ConfigureAwait(false);
        return (RuleStore.ForClient(rule.Definition), Summary(rule, names, await RecipientsByIdAsync(ct).ConfigureAwait(false)));
    }

    /// <summary>Checks and saves a rule. Throws <see cref="RuleValidationException"/> or <see cref="KeyNotFoundException"/>.</summary>
    public async Task<(RuleDefinition Definition, RuleSummaryModel Summary, bool Created)> SaveAsync(RuleDefinition rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        StoredRule? existing = null;
        if (rule.Id != Guid.Empty)
        {
            existing = await store.GetAsync(rule.Id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("The rule no longer exists.");
        }

        StoredRule saved;
        StoredRule? previous;
        await _links.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var clean = WithoutOldTargets(rule); // first: a value that goes at this save must not refuse it
            RuleValidator.Validate(clean, Catalog, key => StoredSecretsOf(existing, clean, key), await RecipientsByIdAsync(ct).ConfigureAwait(false));
            (saved, previous) = await store.SaveAsync(clean, action => RuleStore.PasswordFields(Catalog, action.ActionId), ct).ConfigureAwait(false);
        }
        finally
        {
            _links.Release();
        }

        await Engine.ApplyRuleChangeAsync(previous, saved, ct).ConfigureAwait(false);
        var result = await GetAsync(saved.Definition.Id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("The rule no longer exists.");
        Oadm.Core.Auth.CallerContext.Raise(Changed, this, EventArgs.Empty);
        return (result.Definition, result.Summary, previous is null);
    }

    public async Task<RuleSummaryModel?> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        var change = await store.SetEnabledAsync(id, enabled, ct).ConfigureAwait(false);
        if (change is not { } done)
        {
            return null;
        }

        await Engine.ApplyRuleChangeAsync(done.Previous, done.Rule, ct).ConfigureAwait(false);
        Oadm.Core.Auth.CallerContext.Raise(Changed, this, EventArgs.Empty);
        return (await GetAsync(id, ct).ConfigureAwait(false))?.Summary;
    }

    public async Task<StoredRule?> DeleteAsync(Guid id, CancellationToken ct)
    {
        var removed = await store.DeleteAsync(id, ct).ConfigureAwait(false);
        if (removed is not null)
        {
            await Engine.ApplyRuleChangeAsync(removed, null, ct).ConfigureAwait(false);
            Oadm.Core.Auth.CallerContext.Raise(Changed, this, EventArgs.Empty);
        }

        return removed;
    }

    public async Task<(RuleDefinition Definition, RuleSummaryModel Summary)?> DuplicateAsync(Guid id, CancellationToken ct)
    {
        var copy = await store.DuplicateAsync(id, ct).ConfigureAwait(false);
        if (copy is not null)
        {
            Oadm.Core.Auth.CallerContext.Raise(Changed, this, EventArgs.Empty);
        }

        return copy is null ? null : await GetAsync(copy.Definition.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Every recipient with its target and the rules that use it; the rules are read and walked once per list.</summary>
    public async Task<IReadOnlyList<RecipientModel>> ListRecipientsAsync(CancellationToken ct)
    {
        var rows = await recipients.ListAsync(ct).ConfigureAwait(false);
        var usedBy = RecipientUse.ByRecipient(await store.ListAsync(ct).ConfigureAwait(false), rows.Select(r => r.Id));
        return [.. rows.Select(r => ToModel(r, usedBy.GetValueOrDefault(r.Id) ?? []))];
    }

    /// <summary>
    /// Checks and saves a recipient; its rules follow at their next run. Throws <see cref="RecipientValidationException"/>
    /// or <see cref="KeyNotFoundException"/>.
    /// </summary>
    public async Task<(RecipientModel Recipient, bool Created)> SaveRecipientAsync(Guid? id, string name, string typeId, JsonObject settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var type = Catalog.FindRecipientType(typeId) ?? throw new RecipientValidationException("Choose a type.");
        IReadOnlyList<string> stored = [];
        if (id is { } known)
        {
            var current = await recipients.GetAsync(known, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(RecipientType.NotFoundText);
            stored = RecipientStore.ForClient(current).StoredSecretFields;
        }

        var error = RecipientChecks.Errors(type, settings, stored).FirstOrDefault();
        if (error.Value is not null)
        {
            throw new RecipientValidationException(error.Key, error.Value);
        }

        var (saved, created) = await recipients.SaveAsync(id, name, type.Id, settings, type.Type.Fields, ct).ConfigureAwait(false);
        var rules = await store.ListAsync(ct).ConfigureAwait(false);
        Oadm.Core.Auth.CallerContext.Raise(Changed, this, EventArgs.Empty); // the rules list names what the recipient holds
        return (ToModel(saved, RecipientUse.ByRecipient(rules, [saved.Id]).GetValueOrDefault(saved.Id) ?? []), created);
    }

    /// <summary>
    /// Deletes a recipient no rule uses. Returns it, null when unknown; throws <see cref="InvalidOperationException"/> with the
    /// rule names while a rule uses it.
    /// </summary>
    public async Task<StoredRecipient?> DeleteRecipientAsync(Guid id, CancellationToken ct)
    {
        await _links.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var used = RecipientUse.ByRecipient(await store.ListAsync(ct).ConfigureAwait(false), [id]).GetValueOrDefault(id);
            if (used is { Count: > 0 })
            {
                var more = used.Count - NamedRules;
                throw new InvalidOperationException("Used by " + string.Join(", ", used.Take(NamedRules))
                    + (more > 0 ? string.Create(CultureInfo.InvariantCulture, $" and {more} more") : string.Empty) + ". Change those rules first.");
            }

            var removed = await recipients.DeleteAsync(id, ct).ConfigureAwait(false);
            if (removed is not null)
            {
                Oadm.Core.Auth.CallerContext.Raise(Changed, this, EventArgs.Empty);
            }

            return removed;
        }
        finally
        {
            _links.Release();
        }
    }

    /// <summary>The one line of the Recipients list; a value of its template that is missing stays empty.</summary>
    internal string Target(StoredRecipient recipient)
    {
        var (values, _) = RecipientStore.ForClient(recipient);
        return TargetOf(recipient.TypeId, values);
    }

    /// <summary>The target of <paramref name="values"/> in one line; empty for a type that is gone or has no fields.</summary>
    public string TargetOf(string typeId, JsonObject values)
    {
        if (Catalog.FindRecipientType(typeId)?.Type is not { } type || (type.Target is null && type.Fields.Count == 0))
        {
            return string.Empty;
        }

        return ActionPlaceholders.Fill(type.Target ?? "{" + type.Fields[0].Key + "}", key => ActionSettings.Text(values, key) ?? string.Empty);
    }

    private RecipientModel ToModel(StoredRecipient recipient, IReadOnlyList<string> usedBy)
    {
        var (values, stored) = RecipientStore.ForClient(recipient);
        return new RecipientModel(recipient.Id, recipient.Name, recipient.TypeId, values, stored, Target(recipient), usedBy);
    }

    private async Task<Dictionary<Guid, StoredRecipient>> RecipientsByIdAsync(CancellationToken ct) =>
        (await recipients.ListAsync(ct).ConfigureAwait(false)).ToDictionary(r => r.Id);

    /// <summary>
    /// A rule to store: an action with a recipient keeps only its declared fields, so the old values of its targets (passwords
    /// too) go at this save, also when a client sends them back.
    /// </summary>
    private RuleDefinition WithoutOldTargets(RuleDefinition rule)
    {
        var copy = rule.Clone();
        foreach (var action in copy.Actions)
        {
            if (Catalog.FindAction(action.ActionId)?.Plugin.Fields is { } fields && fields.Any(f => f.Type == ActionFieldType.Recipient))
            {
                foreach (var key in action.Settings.Select(p => p.Key).Where(k => !fields.Any(f => f.Key == k)).ToList())
                {
                    action.Settings.Remove(key);
                }
            }
        }

        return copy;
    }

    private RuleSummaryModel Summary(StoredRule rule, IReadOnlyDictionary<Guid, string> names, IReadOnlyDictionary<Guid, StoredRecipient> linked)
    {
        var definition = rule.Definition;
        string? Name(Guid id) => names.GetValueOrDefault(id);
        var stateful = RuleValidator.IsStateful(definition, Catalog);
        var status = Engine.Status.GetValueOrDefault(definition.Id);
        return new RuleSummaryModel(
            definition.Id,
            definition.Name,
            definition.Enabled,
            RuleTexts.Conditions(definition, Catalog, Name),
            RuleTexts.Actions(definition, Catalog, Name, linked),
            stateful,
            definition.Enabled ? status?.ActiveCount ?? 0 : 0,
            RuleTexts.State(definition, stateful, status),
            status?.LastFiredUtc ?? rule.LastFiredUtc,
            RuleTexts.Problem(definition, Catalog, linked));
    }

    /// <summary>Password fields of an action with a value on the server that an incoming rule keeps.</summary>
    private static IReadOnlyCollection<string> StoredSecretsOf(StoredRule? existing, RuleDefinition incoming, Guid actionKey)
    {
        var old = existing?.Definition.Actions.FirstOrDefault(a => a.Key == actionKey);
        var keep = incoming.Actions.FirstOrDefault(a => a.Key == actionKey)?.StoredSecretFields ?? [];
        return old is null ? [] : [.. RuleStore.ForClient(existing!.Definition).Actions.First(a => a.Key == actionKey).StoredSecretFields.Intersect(keep)];
    }

    /// <summary>Names of the devices a rule names alone ("10.0.0.70 (P3265-V)"), for the summaries.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> DeviceNamesAsync(IEnumerable<RuleDefinition> rules, CancellationToken ct)
    {
        var ids = rules
            .SelectMany(r => r.Conditions.Select(c => c.Devices).Concat(r.Actions.Select(a => a.Targets)))
            .Where(s => s.IsChoice && s.DeviceIds.Count == 1)
            .Select(s => s.DeviceIds[0])
            .ToHashSet();
        var names = new Dictionary<Guid, string>();
        foreach (var id in ids)
        {
            if (await devices.GetAsync(id, ct).ConfigureAwait(false) is { } device)
            {
                names[id] = DeviceRef.Text(device.Address, device.Model);
            }
        }

        return names;
    }
}
