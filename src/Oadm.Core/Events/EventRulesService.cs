using Oadm.Core.Devices;
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

/// <summary>
/// What the gRPC EventService does with rules: list them with their live state, check and save them (the engine applies
/// the change before the call returns), turn them on or off, copy and delete them.
/// </summary>
public sealed class EventRulesService(EventCatalog catalog, RuleStore store, RuleEngine engine, DeviceRepository devices)
{
    public EventCatalog Catalog { get; } = catalog;

    public RuleEngine Engine { get; } = engine;

    /// <summary>A rule was saved, copied, turned on or off, or deleted (the engine raises state changes itself).</summary>
    public event EventHandler? Changed;

    public async Task<IReadOnlyList<RuleSummaryModel>> ListAsync(CancellationToken ct)
    {
        var rules = await store.ListAsync(ct).ConfigureAwait(false);
        var names = await DeviceNamesAsync(rules.Select(r => r.Definition), ct).ConfigureAwait(false);
        return [.. rules.Select(r => Summary(r, names))];
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
        return (RuleStore.ForClient(rule.Definition), Summary(rule, names));
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

        RuleValidator.Validate(rule, Catalog, key => StoredSecretsOf(existing, rule, key));
        var (saved, previous) = await store.SaveAsync(rule, action => RuleStore.PasswordFields(Catalog, action.ActionId), ct).ConfigureAwait(false);
        await Engine.ApplyRuleChangeAsync(previous, saved, ct).ConfigureAwait(false);
        var result = await GetAsync(saved.Definition.Id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("The rule no longer exists.");
        Changed?.Invoke(this, EventArgs.Empty);
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
        Changed?.Invoke(this, EventArgs.Empty);
        return (await GetAsync(id, ct).ConfigureAwait(false))?.Summary;
    }

    public async Task<StoredRule?> DeleteAsync(Guid id, CancellationToken ct)
    {
        var removed = await store.DeleteAsync(id, ct).ConfigureAwait(false);
        if (removed is not null)
        {
            await Engine.ApplyRuleChangeAsync(removed, null, ct).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public async Task<(RuleDefinition Definition, RuleSummaryModel Summary)?> DuplicateAsync(Guid id, CancellationToken ct)
    {
        var copy = await store.DuplicateAsync(id, ct).ConfigureAwait(false);
        if (copy is not null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return copy is null ? null : await GetAsync(copy.Definition.Id, ct).ConfigureAwait(false);
    }

    private RuleSummaryModel Summary(StoredRule rule, IReadOnlyDictionary<Guid, string> names)
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
            RuleTexts.Actions(definition, Catalog, Name),
            stateful,
            definition.Enabled ? status?.ActiveCount ?? 0 : 0,
            RuleTexts.State(definition, stateful, status),
            status?.LastFiredUtc ?? rule.LastFiredUtc,
            RuleTexts.Problem(definition, Catalog));
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
