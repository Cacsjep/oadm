using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.ActionRules.Device;

/// <summary>
/// Writes rules on one device: in place over REST (<c>action-rules</c> v2, the rule id stays) when the device has it, else
/// over SOAP by recreating: the new action and rule first, then the old rule and its action, so a rule the device refuses
/// never leaves it without the old one; when the device refuses to remove the old one, the new one is taken out again, so
/// the device never runs both. When the removal gets no answer (a timeout), the device is read again and decides: the old
/// one still there = the new one is taken out; the old one gone = the change is done; no answer = both may be there and the
/// new one is reported through <c>added</c> so undo can remove it. The only remaining copy is never removed. Recipients follow the rules (REST in place, else recreated with a new id); schedules change in place
/// over REST <c>event-schedules</c> v2, else are removed and added again with the same id, so the rules that use them keep
/// working. The writer keeps count of the rules that use each action configuration while it writes, so a configuration
/// shared by rules is removed only with its last rule.
/// </summary>
public sealed class RuleWriter
{
    private readonly IVapixClient _vapix;
    private readonly DeviceRuleSet _device;
    private readonly bool _inPlace;
    private readonly bool _schedulesInPlace;
    private readonly Dictionary<string, int> _configurationUse;

    public RuleWriter(IVapixClient vapix, DeviceRuleSet device, bool inPlace, bool schedulesInPlace = false)
    {
        _vapix = vapix ?? throw new ArgumentNullException(nameof(vapix));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _inPlace = inPlace;
        _schedulesInPlace = schedulesInPlace;
        _configurationUse = device.Rules.Select(r => r.Action.ConfigurationId).OfType<string>()
            .GroupBy(c => c, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Writes <paramref name="after"/> over <paramref name="before"/>; returns the rule as it is now (its id may be new).
    /// <paramref name="added"/> hears about a rule created by a recreate before the old one is removed (for undo).
    /// </summary>
    public async Task<DeviceRule> ReplaceAsync(DeviceRule before, DeviceRule after, CancellationToken ct, Func<DeviceRule, Task>? added = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var actionChanged = RulePatchApply.ActionChanged(before.Action, after.Action);
        var triggerChanged = RulePatchApply.TriggerChanged(before, after);
        if (_inPlace)
        {
            await RestActionRules.PatchRuleAsync(
                _vapix,
                before.Id,
                after,
                name: before.Name != after.Name,
                enabled: before.Enabled != after.Enabled,
                trigger: triggerChanged,
                action: actionChanged ? _device : null,
                ct).ConfigureAwait(false);
            return after with { Id = before.Id, Action = after.Action with { ConfigurationId = before.Action.ConfigurationId } };
        }

        if (!actionChanged && !triggerChanged && before.Name == after.Name && before.Enabled == after.Enabled)
        {
            return before;
        }

        var created = await AddAsync(after, ct).ConfigureAwait(false);
        if (added is not null)
        {
            await added(created).ConfigureAwait(false);
        }

        try
        {
            await RemoveSoapAsync(before, CancellationToken.None).ConfigureAwait(false);
            return created;
        }
        catch (ActionEngineException)
        {
            await TryAsync(() => RemoveSoapAsync(created, CancellationToken.None)).ConfigureAwait(false); // refused: the old one stays
            throw;
        }
        catch (Exception ex) when (ex is not ActionEngineException)
        {
            var now = await TryReadAsync().ConfigureAwait(false);
            if (now is null)
            {
                throw new ActionEngineException(NoAnswerBoth, ex);
            }

            if (now.Rules.Any(r => r.Id == before.Id))
            {
                await TryAsync(() => RemoveSoapAsync(created, CancellationToken.None)).ConfigureAwait(false);
                throw new ActionEngineException(NoAnswerNothing, ex);
            }

            return created; // the old one is gone: the change is done
        }
    }

    /// <summary>The device did not answer and could not be read: both items may be on it.</summary>
    public const string NoAnswerBoth = "The device did not answer in time. It may have the old and the new one now: read the rules again.";

    /// <summary>The device did not answer, and reading it again showed the old item unchanged.</summary>
    public const string NoAnswerNothing = "The device did not answer in time. Nothing was changed.";

    private async Task<DeviceRuleSet?> TryReadAsync()
    {
        try
        {
            return await SoapActionEngine.ReadAsync(_vapix, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any failure means: the device state is unknown.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>Creates the rule with its action; returns it with its new ids.</summary>
    public async Task<DeviceRule> AddAsync(DeviceRule rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (_inPlace)
        {
            var id = await RestActionRules.AddRuleAsync(_vapix, rule, _device, ct).ConfigureAwait(false);
            return rule with { Id = id, Action = rule.Action with { ConfigurationId = null } };
        }

        var configuration = await SoapActionEngine.AddConfigurationAsync(_vapix, rule.Action, rule.Action.Name ?? rule.Name, ct).ConfigureAwait(false);
        try
        {
            var id = await SoapActionEngine.AddRuleAsync(_vapix, rule, configuration, ct).ConfigureAwait(false);
            _configurationUse[configuration] = 1;
            return rule with { Id = id, Action = rule.Action with { ConfigurationId = configuration } };
        }
        catch
        {
            await TryAsync(() => SoapActionEngine.RemoveConfigurationAsync(_vapix, configuration, CancellationToken.None)).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Removes the rule and its action (over SOAP the action only when no other rule uses it).</summary>
    public async Task DeleteAsync(DeviceRule rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (_inPlace)
        {
            await RestActionRules.DeleteRuleAsync(_vapix, rule.Id, ct).ConfigureAwait(false);
            return;
        }

        await RemoveSoapAsync(rule, ct).ConfigureAwait(false);
    }

    /// <summary>Writes <paramref name="after"/> over <paramref name="before"/>; returns the recipient as it is now (its id may be new).</summary>
    public async Task<DeviceRecipient> ReplaceRecipientAsync(DeviceRecipient before, DeviceRecipient after, CancellationToken ct, Func<DeviceRecipient, Task>? added = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (_inPlace)
        {
            await RestActionRules.PatchRecipientAsync(_vapix, after with { Id = before.Id }, ct).ConfigureAwait(false);
            return after with { Id = before.Id };
        }

        var created = after with { Id = await SoapActionEngine.AddRecipientAsync(_vapix, after, ct).ConfigureAwait(false) };
        if (added is not null)
        {
            await added(created).ConfigureAwait(false);
        }

        try
        {
            await SoapActionEngine.RemoveRecipientAsync(_vapix, before.Id, CancellationToken.None).ConfigureAwait(false);
            return created;
        }
        catch (ActionEngineException)
        {
            await TryAsync(() => SoapActionEngine.RemoveRecipientAsync(_vapix, created.Id, CancellationToken.None)).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not ActionEngineException)
        {
            var now = await TryReadAsync().ConfigureAwait(false);
            if (now is null)
            {
                throw new ActionEngineException(NoAnswerBoth, ex);
            }

            if (now.Recipients.Any(r => r.Id == before.Id))
            {
                await TryAsync(() => SoapActionEngine.RemoveRecipientAsync(_vapix, created.Id, CancellationToken.None)).ConfigureAwait(false);
                throw new ActionEngineException(NoAnswerNothing, ex);
            }

            return created;
        }
    }

    public async Task<DeviceRecipient> AddRecipientAsync(DeviceRecipient recipient, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        return recipient with { Id = await SoapActionEngine.AddRecipientAsync(_vapix, recipient, ct).ConfigureAwait(false) };
    }

    public Task DeleteRecipientAsync(DeviceRecipient recipient, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        return SoapActionEngine.RemoveRecipientAsync(_vapix, recipient.Id, ct);
    }

    /// <summary>
    /// Writes <paramref name="after"/> over <paramref name="before"/> keeping the id: in place over REST, else removed and
    /// added again (when the add fails, the old one is added back and the error stays).
    /// </summary>
    public async Task ReplaceScheduleAsync(DeviceSchedule before, DeviceSchedule after, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (_schedulesInPlace)
        {
            await RestActionRules.PatchScheduleAsync(_vapix, after with { Id = before.Id }, ct).ConfigureAwait(false);
            return;
        }

        // Removed and added again with the same id: rules keep naming it. Without an answer, the device is read again.
        try
        {
            await SoapActionEngine.RemoveScheduleAsync(_vapix, before.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ActionEngineException)
        {
            var now = await TryReadAsync().ConfigureAwait(false);
            if (now is null || now.Schedules.Any(x => x.Id == before.Id))
            {
                throw new ActionEngineException(now is null ? NoAnswerBoth : NoAnswerNothing, ex);
            }
        }

        try
        {
            await SoapActionEngine.AddScheduleAsync(_vapix, after with { Id = before.Id }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var now = ex is ActionEngineException ? null : await TryReadAsync().ConfigureAwait(false);
            if (now?.Schedules.FirstOrDefault(x => x.Id == before.Id) is { } there
                && Rules.ScheduleText.Normalize(there.Definition) == Rules.ScheduleText.Normalize(after.Definition) && there.Name == after.Name)
            {
                return; // added, only the answer was lost
            }

            if (now is null || !now.Schedules.Any(x => x.Id == before.Id))
            {
                await TryAsync(() => SoapActionEngine.AddScheduleAsync(_vapix, before, CancellationToken.None)).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Creates a schedule with its id (the device keeps the id given).</summary>
    public Task<string> AddScheduleAsync(DeviceSchedule schedule, CancellationToken ct) =>
        SoapActionEngine.AddScheduleAsync(_vapix, schedule, ct);

    public Task DeleteScheduleAsync(DeviceSchedule schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return SoapActionEngine.RemoveScheduleAsync(_vapix, schedule.Id, ct);
    }

    private async Task RemoveSoapAsync(DeviceRule rule, CancellationToken ct)
    {
        await SoapActionEngine.RemoveRuleAsync(_vapix, rule.Id, ct).ConfigureAwait(false);
        if (rule.Action.ConfigurationId is not { } configuration)
        {
            return;
        }

        var left = _configurationUse.TryGetValue(configuration, out var used) ? used - 1 : 0;
        _configurationUse[configuration] = left;
        if (left <= 0)
        {
            // The rule is gone; a configuration left behind does nothing, so its removal must not fail the change.
            await TryAsync(() => SoapActionEngine.RemoveConfigurationAsync(_vapix, configuration, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort cleanup: the first error stays the one reported.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
