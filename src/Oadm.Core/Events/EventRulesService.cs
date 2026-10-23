using System.Text.Json;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
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
    string? Problem)
{
    /// <summary>The owner (all devices or a group; none when its group was deleted).</summary>
    public DeviceScope Owner { get; init; } = DeviceScope.None;
}

/// <summary>
/// What the gRPC EventService does with rules: list them with their live state, check and save them (the engine applies
/// the change before the call returns), turn them on or off, copy and delete them. Device groups
/// (docs/spec/device-scope.md, Shared objects): every rule has an owner; users of a group see and change only the rules of
/// their group (others answer like unknown ones), a new rule and a copy belong to the caller's devices, an edit keeps the
/// owner, only administrators choose another one. Saving a rule of a group refuses devices, tags and events outside the
/// group and addresses outside the administrator's list.
/// </summary>
public sealed class EventRulesService(
    EventCatalog catalog, RuleStore store, RuleEngine engine, DeviceRepository devices, DeviceViewFactory views, RuleOwners owners, DeviceTagStore tags)
{
    /// <summary>The refusal of a rule someone else owns, or one that is gone (the same answer).</summary>
    public const string NoLongerExists = "The rule no longer exists.";

    /// <summary>The refusal of an event a group may not use (another group's site or check), like an unknown one.</summary>
    public const string EventNotAvailable = "The event no longer exists.";

    public EventCatalog Catalog { get; } = catalog;

    public RuleEngine Engine { get; } = engine;

    /// <summary>The owners of rules (groups, sites, allowed addresses).</summary>
    public RuleOwners Owners => owners;

    /// <summary>A rule was saved, copied, turned on or off, or deleted (the engine raises state changes itself).</summary>
    public event EventHandler? Changed;

    /// <summary>The rules the user of <paramref name="view"/> sees: all of them for all devices, else the group's.</summary>
    public async Task<IReadOnlyList<RuleSummaryModel>> ListAsync(DeviceView view, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        var rules = (await store.ListAsync(ct).ConfigureAwait(false)).Where(r => OwnerScopes.Sees(view, r.Owner)).ToList();
        var names = await DeviceNamesAsync(rules.Select(r => r.Definition), ct).ConfigureAwait(false);
        return [.. rules.Select(r => Summary(r, names))];
    }

    /// <summary>The ids of the rules the user sees (the event log of a group).</summary>
    public async Task<IReadOnlySet<Guid>> VisibleIdsAsync(DeviceView view, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        return (await store.ListAsync(ct).ConfigureAwait(false)).Where(r => OwnerScopes.Sees(view, r.Owner)).Select(r => r.Definition.Id).ToHashSet();
    }

    /// <summary>The rule for a client (password values removed) and its summary; null when unknown or hidden.</summary>
    public async Task<(RuleDefinition Definition, RuleSummaryModel Summary)?> GetAsync(Guid id, DeviceView view, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        var rule = await store.GetAsync(id, ct).ConfigureAwait(false);
        if (rule is null || !OwnerScopes.Sees(view, rule.Owner))
        {
            return null;
        }

        var names = await DeviceNamesAsync([rule.Definition], ct).ConfigureAwait(false);
        return (RuleStore.ForClient(rule.Definition), Summary(rule, names));
    }

    /// <summary>
    /// Checks and saves a rule of the caller (<paramref name="caller"/>; a new rule belongs to the caller's devices, an edit
    /// keeps its owner; <paramref name="owner"/> chooses another one with <paramref name="mayChooseOwner"/>). Throws
    /// <see cref="RuleValidationException"/>, <see cref="KeyNotFoundException"/> (a rule, group, tag or event the owner does
    /// not have), <see cref="Sdk.Devices.DevicesNotAvailableException"/> (devices outside the owner's) and
    /// <see cref="UnauthorizedAccessException"/> (another owner without the right).
    /// </summary>
    public async Task<(RuleDefinition Definition, RuleSummaryModel Summary, bool Created)> SaveAsync(
        RuleDefinition rule, DeviceView caller, DeviceScope? owner, bool mayChooseOwner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(caller);
        StoredRule? existing = null;
        if (rule.Id != Guid.Empty)
        {
            existing = await store.GetAsync(rule.Id, ct).ConfigureAwait(false);
            if (existing is null || !OwnerScopes.Sees(caller, existing.Owner))
            {
                throw new KeyNotFoundException(NoLongerExists);
            }
        }

        var kept = existing?.Owner ?? OwnerScopes.OwnerOf(caller);
        var target = owner ?? kept;
        if (target != kept && !mayChooseOwner)
        {
            throw new UnauthorizedAccessException(OwnerRules.OnlyAdministrators);
        }

        if (!target.AllDevices && target.GroupId is { } group && !owners.Scopes.GroupExists(group))
        {
            throw new KeyNotFoundException(DeviceGroupTexts.UnknownGroup);
        }

        if (target.IsNone && rule.Enabled)
        {
            throw new RuleValidationException(DeviceGroupTexts.ChooseDevices);
        }

        RuleValidator.Validate(rule, Catalog, key => StoredSecretsOf(existing, rule, key));
        await RequireOwnersAsync(rule, target, caller.UserName, ct).ConfigureAwait(false);
        var (saved, previous) = await store.SaveAsync(rule, action => RuleStore.PasswordFields(Catalog, action.ActionId), target, ct).ConfigureAwait(false);
        await Engine.ApplyRuleChangeAsync(previous, saved, ct).ConfigureAwait(false);
        var result = await GetAsync(saved.Definition.Id, caller, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(NoLongerExists);
        CallerContext.Raise(Changed, this, EventArgs.Empty);
        return (result.Definition, result.Summary, previous is null);
    }

    /// <summary>Turns a rule the caller sees on or off (a rule without an owner stays off). Null when unknown or hidden.</summary>
    public async Task<RuleSummaryModel?> SetEnabledAsync(Guid id, bool enabled, DeviceView caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await store.GetAsync(id, ct).ConfigureAwait(false) is not { } current || !OwnerScopes.Sees(caller, current.Owner))
        {
            return null;
        }

        if (enabled && current.Owner.IsNone)
        {
            throw new RuleValidationException(DeviceGroupTexts.ChooseDevices);
        }

        var change = await store.SetEnabledAsync(id, enabled, ct).ConfigureAwait(false);
        if (change is not { } done)
        {
            return null;
        }

        await Engine.ApplyRuleChangeAsync(done.Previous, done.Rule, ct).ConfigureAwait(false);
        CallerContext.Raise(Changed, this, EventArgs.Empty);
        return (await GetAsync(id, caller, ct).ConfigureAwait(false))?.Summary;
    }

    /// <summary>Deletes a rule the caller sees; null when unknown or hidden.</summary>
    public async Task<StoredRule?> DeleteAsync(Guid id, DeviceView caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await store.GetAsync(id, ct).ConfigureAwait(false) is not { } current || !OwnerScopes.Sees(caller, current.Owner))
        {
            return null;
        }

        var removed = await store.DeleteAsync(id, ct).ConfigureAwait(false);
        if (removed is not null)
        {
            await Engine.ApplyRuleChangeAsync(removed, null, ct).ConfigureAwait(false);
            CallerContext.Raise(Changed, this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>
    /// Copies a rule the caller sees; the copy belongs to the caller's devices (a user of a group copies into the group;
    /// a copy of a rule outside the caller's devices is refused like any save outside them). Null when unknown or hidden.
    /// </summary>
    public async Task<(RuleDefinition Definition, RuleSummaryModel Summary)?> DuplicateAsync(Guid id, DeviceView caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (await store.GetAsync(id, ct).ConfigureAwait(false) is not { } source || !OwnerScopes.Sees(caller, source.Owner))
        {
            return null;
        }

        var owner = OwnerScopes.OwnerOf(caller);
        await RequireOwnersAsync(source.Definition, owner, caller.UserName, ct).ConfigureAwait(false);
        var copy = await store.DuplicateAsync(id, owner, ct).ConfigureAwait(false);
        if (copy is not null)
        {
            CallerContext.Raise(Changed, this, EventArgs.Empty);
        }

        return copy is null ? null : await GetAsync(copy.Definition.Id, caller, ct).ConfigureAwait(false);
    }

    /// <summary>The event types the user of <paramref name="view"/> may use (the catalog of the rule dialog).</summary>
    public async Task<IReadOnlyList<CatalogEventType>> EventTypesAsync(DeviceView view, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.HasAllDevices)
        {
            return Catalog.EventTypes;
        }

        var ids = await view.IdsAsync(ct).ConfigureAwait(false);
        var owner = OwnerScopes.OwnerOf(view);
        return [.. Catalog.EventTypes.Where(t => owners.Sees(t, owner, ids.Contains))];
    }

    /// <summary>
    /// A rule of a group uses only the group's devices, tags (defined or on its devices), events (its sites, its checks,
    /// triggers of its devices) and allowed addresses.
    /// </summary>
    private async Task RequireOwnersAsync(RuleDefinition rule, DeviceScope owner, string? userName, CancellationToken ct)
    {
        var view = await views.ForScopeAsync(owner, userName, ct).ConfigureAwait(false);
        if (view.HasAllDevices)
        {
            return;
        }

        var selectors = rule.Conditions.Select(c => c.Devices).Concat(rule.Actions.Select(a => a.Targets)).ToList();
        await view.GetAsync([.. selectors.SelectMany(s => s.DeviceIds).Distinct()], ct).ConfigureAwait(false);
        await OwnerRules.RequireTagsAsync([.. selectors.SelectMany(s => s.Tags).Distinct(StringComparer.OrdinalIgnoreCase)], view, tags, ct).ConfigureAwait(false);

        var ids = await view.IdsAsync(ct).ConfigureAwait(false);
        foreach (var condition in rule.Conditions.Where(c => c.Kind == RuleConditionKind.Event))
        {
            if (Catalog.FindEventType(condition.EventTypeId) is { } type && !owners.Sees(type, owner, ids.Contains))
            {
                throw new KeyNotFoundException(EventNotAvailable);
            }
        }

        await owners.Destinations.LoadAsync(ct).ConfigureAwait(false);
        foreach (var action in rule.Actions)
        {
            if (Catalog.FindAction(action.ActionId) is not { } catalogAction)
            {
                continue;
            }

            IReadOnlyList<ActionDestination> destinations;
            try
            {
                destinations = catalogAction.Plugin.Destinations(JsonSerializer.SerializeToElement(action.Settings));
            }
#pragma warning disable CA1031 // Plugin code is untrusted: an action that cannot say where it sends is refused for a group.
            catch (Exception)
#pragma warning restore CA1031
            {
                throw new RuleValidationException(ActionDestination.NotAllowedText);
            }

            if (!destinations.All(d => owners.Destinations.Allows(d.Host, d.Port)))
            {
                throw new RuleValidationException(ActionDestination.NotAllowedText);
            }
        }
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
            RuleTexts.Problem(definition, Catalog))
        {
            Owner = rule.Owner,
        };
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
