using System.Globalization;

using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ActionRules;

/// <summary>Copying rules, recipients and schedules to devices; changing and deleting recipients and schedules.</summary>
public sealed partial class RuleTaskPlugin
{
    /// <summary>
    /// Copies a rule: the schedules it uses first (a device refuses a rule naming a schedule it lacks: "could not match any
    /// property events"), then the rule fitted to this device's action (settings the device lacks are dropped, settings it
    /// needs and the rule lacks come from the patch or fail). An identical rule on the device is left as it is.
    /// </summary>
    private static async Task DeployRuleAsync(Run run)
    {
        var source = run.Payload.Rule!;
        var patch = run.Payload.Patch ?? new RulePatch();
        var ruleStep = "Create rule " + Short(patch.Name ?? source.Name);
        DeviceRule rule;
        Dictionary<string, DeviceSchedule> create;
        try
        {
            // The copy's name, event, schedule and trigger first, then the schedules it names are fitted to this device.
            var shaped = RulePatchApply.Apply(source, patch with { Template = null, Parameters = null }, run.Set);
            (rule, create) = FitSchedules(shaped, [.. run.Payload.Schedules ?? [], .. NewSchedules(patch)], run.Set);
            if (patch.RecipientName is { } recipient)
            {
                patch = WithRecipient(patch, patch.Template ?? rule.Action.Template, recipient, run.Set);
            }

            rule = rule with { Id = string.Empty, Action = RulePatchApply.ApplyAction(rule.Action, patch, run.Set) with { ConfigurationId = null } };
            var leftOut = RulePatchApply.LeftOut(rule.Action.Template, patch, run.Set);
            if (leftOut.Count > 0)
            {
                run.Ctx.ReportWarning("This device's action has no " + string.Join(", ", leftOut) + "; left out.");
            }
        }
        catch (ActionEngineException ex)
        {
            run.Ctx.SkipStep(SaveStep, "Nothing to change.");
            using var step = run.Ctx.BeginStep(ruleStep);
            step.Fail(ex.Message);
            throw;
        }

        if (run.Set.Rules.Any(r => RuleContent.Same(r, rule)))
        {
            run.Ctx.SkipStep(SaveStep, "Nothing to change.");
            run.Ctx.SkipStep(ruleStep, RuleTexts.AlreadyThere);
            return;
        }

        await run.SaveFirstAsync().ConfigureAwait(false);
        foreach (var schedule in create.Values)
        {
            await run.StepAsync("Create schedule " + Short(schedule.Name), async _ =>
            {
                await run.Writer.AddScheduleAsync(schedule, run.Ct).ConfigureAwait(false);
                run.Schedules.Add(new UndoSchedule(null, schedule));
                await run.WroteAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        if (run.Failed == 0)
        {
            await run.StepAsync(ruleStep, async step =>
            {
                var added = await run.Writer.AddAsync(rule, run.Ct).ConfigureAwait(false);
                run.Rules.Add(new UndoRule(null, added));
                await run.WroteAsync().ConfigureAwait(false);
                step.Complete("Rule " + added.Id);
            }).ConfigureAwait(false);
        }
        else
        {
            run.Ctx.SkipStep(ruleStep, "Not run: a schedule could not be created.");
        }

        run.ThrowIfFailed("parts");
    }

    /// <summary>The schedule a change adds (OADM's own id), as a source to create where a device lacks it.</summary>
    internal static IEnumerable<DeviceSchedule> NewSchedules(RulePatch patch) =>
        patch is { ScheduleId: { Length: > 0 } id, NewSchedule: { Name: { } name, Definition: { } definition } }
            ? [new DeviceSchedule(id, name.Trim(), ScheduleText.Normalize(definition))]
            : [];

    /// <summary>
    /// A new rule's action takes the settings of this device's recipient of that name (its passwords too); settings the
    /// rule builder set win.
    /// </summary>
    internal static RulePatch WithRecipient(RulePatch patch, string template, string recipientName, DeviceRuleSet device)
    {
        var kind = device.Template(template)?.RecipientTemplate
            ?? throw new ActionEngineException("This device does not offer this action. Nothing was changed.");
        var found = device.Recipients.FirstOrDefault(r => r.Template == kind && r.Name == recipientName)
            ?? throw new ActionEngineException($"This device has no recipient \"{recipientName}\" of this kind. Nothing was changed.");
        var values = new Dictionary<string, string>(patch.Parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var p in found.Parameters)
        {
            values.TryAdd(p.Name, p.Value);
        }

        return patch with { Parameters = values };
    }

    /// <summary>
    /// The rule with its schedule ids as this device has them, and the schedules to create first. A schedule is matched by id
    /// with the same times, else by name and times; built-in ones must exist; others get OADM's own id (the same on every
    /// device), changed only when that id is taken by another schedule.
    /// </summary>
    internal static (DeviceRule Rule, Dictionary<string, DeviceSchedule> Create) FitSchedules(DeviceRule rule, IReadOnlyList<DeviceSchedule> sources, DeviceRuleSet device)
    {
        var create = new Dictionary<string, DeviceSchedule>(StringComparer.Ordinal);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in rule.Conditions.Prepend(rule.StartEvent).OfType<EventFilter>().Select(RuleText.ScheduleId).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var source = sources.FirstOrDefault(s => s.Id == id);
            var same = device.Schedules.FirstOrDefault(s => s.Id == id);
            if (same is not null && (source is null || same.IsBuiltIn || SameTimes(same, source)))
            {
                map[id] = id; // the device has it (built-in schedules are each device's own)
                continue;
            }

            if (source is null)
            {
                throw new ActionEngineException($"The schedule {id} of the rule is unknown. Nothing was changed.");
            }

            if (device.Schedules.FirstOrDefault(s => s.Name == source.Name && SameTimes(s, source)) is { } byName)
            {
                map[id] = byName.Id;
                continue;
            }

            if (source.IsBuiltIn)
            {
                throw new ActionEngineException($"This device has no schedule \"{source.Name}\". Nothing was changed.");
            }

            var newId = source.Id.StartsWith(ScheduleText.OwnIdPrefix, StringComparison.Ordinal) ? source.Id : ScheduleText.OwnId(source.Name);
            var candidate = newId;
            for (var n = 2; device.Schedules.Any(s => s.Id == candidate) || create.ContainsKey(candidate); n++)
            {
                candidate = newId + "-" + n.ToString(CultureInfo.InvariantCulture);
            }

            create[candidate] = source with { Id = candidate };
            map[id] = candidate;
        }

        EventFilter Map(EventFilter f) => RuleText.ScheduleId(f) is { } old && map.TryGetValue(old, out var now) && now != old
            ? RuleText.WithScheduleId(f, now)
            : f;
        return (rule with { StartEvent = rule.StartEvent is null ? null : Map(rule.StartEvent), Conditions = [.. rule.Conditions.Select(Map)] }, create);
    }

    /// <summary>
    /// Changes or deletes recipients; a change can also update the rules that send to the old settings (each rule follows one
    /// recipient of the run, and only when that recipient's change was written).
    /// </summary>
    private static async Task ChangeRecipientsAsync(Run run)
    {
        var delete = run.Payload.Action == RuleTaskActions.RecipientDelete;
        var work = new List<DeviceRecipient>();
        foreach (var target in run.Targets)
        {
            var recipient = run.Set.Recipients.FirstOrDefault(r => r.Id == target.Id);
            var step = (delete ? "Delete recipient " : "Change recipient ") + Short(recipient?.Name ?? target.Id);
            if (recipient is null)
            {
                run.Warn(step, "The recipient is not on the device any more.");
            }
            else if (RecipientView.VersionOf(recipient) != target.Version)
            {
                run.Warn(step, RuleTexts.RecipientChangedSinceRead);
            }
            else
            {
                work.Add(recipient);
            }
        }

        if (work.Count == 0)
        {
            run.Ctx.SkipStep(SaveStep, "Nothing to change.");
            return;
        }

        // The new recipients and the rules that follow them, worked out before anything is written.
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var plans = new List<(DeviceRecipient Before, DeviceRecipient After, List<(DeviceRule Before, DeviceRule After)> Rules)>();
        foreach (var recipient in work)
        {
            var after = delete ? recipient : ApplyRecipient(recipient, run.Payload.RecipientPatch!, run.Set);
            var rules = new List<(DeviceRule, DeviceRule)>();
            if (!delete && run.Payload.RecipientPatch!.UpdateRules)
            {
                var changed = after.Parameters.Where(p => recipient.Parameters.FirstOrDefault(o => o.Name == p.Name)?.Value != p.Value).ToList();
                foreach (var rule in changed.Count == 0 ? [] : RulesSendingTo(recipient, run.Set).Where(r => claimed.Add(r.Id)))
                {
                    var parameters = rule.Action.Parameters.Select(p => changed.FirstOrDefault(c => c.Name == p.Name) is { } c ? p with { Value = c.Value } : p).ToList();
                    rules.Add((rule, rule with { Action = rule.Action with { Parameters = parameters } }));
                }
            }

            plans.Add((recipient, after, rules));
        }

        run.Recipients.AddRange(plans.Select(p => new UndoRecipient(p.Before, p.Before)));
        run.Rules.AddRange(plans.SelectMany(p => p.Rules).Select(r => new UndoRule(r.Before, r.Before)));
        await run.SaveFirstAsync().ConfigureAwait(false);
        foreach (var (before, after, rules) in plans)
        {
            var index = run.Recipients.FindIndex(u => u.Before?.Id == before.Id);
            var written = await run.StepAsync((delete ? "Delete recipient " : "Change recipient ") + Short(before.Name), async step =>
            {
                if (delete)
                {
                    await run.Writer.DeleteRecipientAsync(before, run.Ct).ConfigureAwait(false);
                    run.Recipients[index] = run.Recipients[index] with { After = null };
                    await run.WroteAsync().ConfigureAwait(false);
                    return;
                }

                if (RuleContent.Same(after, before))
                {
                    step.Skip("Already as chosen.");
                    return;
                }

                var now = await run.Writer.ReplaceRecipientAsync(before, after, run.Ct, run.RecreatedAsync).ConfigureAwait(false);
                run.Recipients[index] = run.Recipients[index] with { After = now };
                run.Settled(now);
                await run.WroteAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);

            foreach (var (ruleBefore, ruleAfter) in rules)
            {
                var step = "Change rule " + Short(ruleBefore.Name);
                if (!written)
                {
                    run.Ctx.SkipStep(step, "Not run: the recipient could not be changed.");
                    continue;
                }

                await run.StepAsync(step, async s =>
                {
                    var now = await run.Writer.ReplaceAsync(ruleBefore, ruleAfter, run.Ct, run.RecreatedAsync).ConfigureAwait(false);
                    var at = run.Rules.FindIndex(r => r.Before?.Id == ruleBefore.Id);
                    run.Rules[at] = run.Rules[at] with { After = now };
                    run.Settled(now);
                    await run.WroteAsync().ConfigureAwait(false);
                    s.Complete(now.Id == ruleBefore.Id ? null : "New rule id " + now.Id);
                }).ConfigureAwait(false);
            }
        }

        run.ThrowIfFailed("changes");
    }

    /// <summary>
    /// The rules that send to a recipient: an action of the recipient's kind whose server settings (every recipient setting but
    /// passwords) are the recipient's, like the camera copied them when the rule was made.
    /// </summary>
    internal static IEnumerable<DeviceRule> RulesSendingTo(DeviceRecipient recipient, DeviceRuleSet device)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(device);
        var keys = recipient.Parameters.Where(p => !RuleText.IsSecret(p.Name)).ToList();
        return device.Rules.Where(r =>
            device.Template(r.Action.Template)?.RecipientTemplate == recipient.Template
            && keys.All(k => r.Action.Get(k.Name) == k.Value));
    }

    private static DeviceRecipient ApplyRecipient(DeviceRecipient recipient, RecipientPatch patch, DeviceRuleSet device)
    {
        var names = device.RecipientTemplates.FirstOrDefault(t => t.Token == recipient.Template)?.Parameters ?? [.. recipient.Parameters.Select(p => p.Name)];
        var values = (patch.Parameters ?? new Dictionary<string, string>()).Where(v => names.Contains(v.Key, StringComparer.Ordinal))
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal); // a setting this device's kind lacks is left out
        return recipient with
        {
            Name = patch.Name?.Trim() ?? recipient.Name,
            Parameters = [.. recipient.Parameters.Select(p => values.TryGetValue(p.Name, out var v) ? p with { Value = RuleText.KeepLogin(v, p.Value) } : p)],
        };
    }

    /// <summary>Copies a recipient; settings this device's recipient kind lacks are left out, ones it needs must be given.</summary>
    private static async Task DeployRecipientAsync(Run run)
    {
        var source = run.Payload.Recipient!;
        var patch = run.Payload.RecipientPatch ?? new RecipientPatch(null, null, UpdateRules: false);
        var step = "Create recipient " + Short(patch.Name ?? source.Name);
        string? problem = null;
        DeviceRecipient? recipient = null;
        if (run.Set.RecipientTemplates.FirstOrDefault(t => t.Token == source.Template) is not { } template)
        {
            problem = "This device does not offer this kind of recipient. Nothing was changed.";
        }
        else
        {
            var values = patch.Parameters ?? new Dictionary<string, string>();
            var parameters = new List<NameValue>();
            foreach (var n in template.Parameters)
            {
                var own = source.Parameters.FirstOrDefault(p => p.Name == n)?.Value;
                var value = values.TryGetValue(n, out var v) ? v : own;
                if (RuleText.OmitWhenEmpty(n) && string.IsNullOrEmpty(value))
                {
                    continue; // given as the other fingerprint
                }

                if (value is null)
                {
                    problem = $"Enter a value for \"{n}\". Nothing was changed.";
                    break;
                }

                try
                {
                    value = RuleText.KeepLogin(value, own);
                }
                catch (ActionEngineException ex)
                {
                    problem = ex.Message;
                    break;
                }

                parameters.Add(new NameValue(n, value));
            }

            recipient = source with { Id = string.Empty, Name = patch.Name?.Trim() ?? source.Name, Parameters = parameters };
        }

        if (problem is not null)
        {
            run.Ctx.SkipStep(SaveStep, "Nothing to change.");
            using var s = run.Ctx.BeginStep(step);
            s.Fail(problem);
            throw new ActionEngineException(problem);
        }

        if (run.Set.Recipients.Any(r => RuleContent.Same(r, recipient!)))
        {
            run.Ctx.SkipStep(SaveStep, "Nothing to change.");
            run.Ctx.SkipStep(step, RuleTexts.AlreadyThere);
            return;
        }

        await run.SaveFirstAsync().ConfigureAwait(false);
        await run.StepAsync(step, async _ =>
        {
            var added = await run.Writer.AddRecipientAsync(recipient!, run.Ct).ConfigureAwait(false);
            run.Recipients.Add(new UndoRecipient(null, added));
            await run.WroteAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        run.ThrowIfFailed("changes");
    }

    /// <summary>
    /// Changes or deletes schedules. Built-in ones are never changed or deleted. A schedule that rules use is deleted only
    /// together with them, and only with the rules the page showed, each still at the version it read; a schedule that other
    /// rules use stays.
    /// </summary>
    private static async Task ChangeSchedulesAsync(Run run)
    {
        var delete = run.Payload.Action == RuleTaskActions.ScheduleDelete;
        var chosenRules = run.Payload.Rules?.FirstOrDefault(t => t.DeviceId == run.Device.Id)?.Rules ?? [];
        var work = new List<(DeviceSchedule Schedule, List<DeviceRule> Rules)>();
        foreach (var target in run.Targets)
        {
            var schedule = run.Set.Schedules.FirstOrDefault(s => s.Id == target.Id);
            var step = (delete ? "Delete schedule " : "Change schedule ") + Short(schedule?.Name ?? target.Id);
            var users = schedule is null ? [] : run.Set.Rules.Where(r => ScheduleView.UsesSchedule(r, schedule.Id)).ToList();
            var chosen = users.Where(u => chosenRules.Any(c => c.Id == u.Id && c.Version == RuleView.VersionOf(u))).ToList();
            var stale = users.Count(u => chosenRules.Any(c => c.Id == u.Id) && !chosen.Contains(u));
            if (schedule is null)
            {
                run.Warn(step, "The schedule is not on the device any more.");
            }
            else if (ScheduleView.VersionOf(schedule) != target.Version)
            {
                run.Warn(step, RuleTexts.ScheduleChangedSinceRead);
            }
            else if (schedule.IsBuiltIn)
            {
                run.Warn(step, "Built-in schedules are not changed.");
            }
            else if (delete && stale > 0)
            {
                run.Warn(step, "A rule that uses it changed since it was read. Read the rules again. Nothing was changed.");
            }
            else if (delete && users.Count > chosen.Count)
            {
                var others = users.Count - chosen.Count;
                run.Warn(step, (others == 1 ? "Used by 1 rule." : string.Create(CultureInfo.InvariantCulture, $"Used by {others} rules.")) + " Nothing was changed.");
            }
            else
            {
                work.Add((schedule, delete ? chosen : []));
            }
        }

        if (work.Count == 0)
        {
            run.Ctx.SkipStep(SaveStep, "Nothing to change.");
            return;
        }

        run.Schedules.AddRange(work.Select(w => new UndoSchedule(w.Schedule, w.Schedule)));
        run.Rules.AddRange(work.SelectMany(w => w.Rules).DistinctBy(r => r.Id).Select(r => new UndoRule(r, r)));
        await run.SaveFirstAsync().ConfigureAwait(false);
        foreach (var (schedule, rules) in work)
        {
            var index = run.Schedules.FindIndex(u => u.Before?.Id == schedule.Id);
            var rulesGone = true;
            foreach (var rule in rules)
            {
                var at = run.Rules.FindIndex(r => r.Before?.Id == rule.Id);
                if (run.Rules[at].After is null)
                {
                    continue; // deleted with another schedule of this run
                }

                rulesGone &= await run.StepAsync(StepName(RuleTaskActions.Delete, rule.Name), async _ =>
                {
                    await run.Writer.DeleteAsync(rule, run.Ct).ConfigureAwait(false);
                    run.Rules[at] = run.Rules[at] with { After = null };
                    await run.WroteAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
            }

            var step = (delete ? "Delete schedule " : "Change schedule ") + Short(schedule.Name);
            if (!rulesGone)
            {
                run.Ctx.SkipStep(step, "Not run: a rule that uses it could not be deleted.");
                continue;
            }

            await run.StepAsync(step, async s =>
            {
                if (delete)
                {
                    await run.Writer.DeleteScheduleAsync(schedule, run.Ct).ConfigureAwait(false);
                    run.Schedules[index] = run.Schedules[index] with { After = null };
                    await run.WroteAsync().ConfigureAwait(false);
                    return;
                }

                var patch = run.Payload.SchedulePatch!;
                var after = schedule with { Name = patch.Name?.Trim() ?? schedule.Name, Definition = patch.Definition is null ? schedule.Definition : ScheduleText.Normalize(patch.Definition) };
                if (RuleContent.Same(after, schedule))
                {
                    s.Skip("Already as chosen.");
                    return;
                }

                await run.Writer.ReplaceScheduleAsync(schedule, after, run.Ct).ConfigureAwait(false);
                run.Schedules[index] = run.Schedules[index] with { After = after };
                await run.WroteAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        run.ThrowIfFailed("changes");
    }

    /// <summary>Puts a schedule on the device with its id; one with the same name and times is left as it is.</summary>
    private static async Task DeployScheduleAsync(Run run)
    {
        var schedule = run.Payload.Schedules![0];
        var step = "Create schedule " + Short(schedule.Name);
        if (run.Set.Schedules.FirstOrDefault(s => s.Id == schedule.Id || (s.Name == schedule.Name && SameTimes(s, schedule))) is { } there)
        {
            run.Ctx.SkipStep(SaveStep, "Nothing to change.");
            if (SameTimes(there, schedule))
            {
                run.Ctx.SkipStep(step, RuleTexts.AlreadyThere);
                return;
            }

            using var s = run.Ctx.BeginStep(step);
            var message = there.Id == schedule.Id && there.Name != schedule.Name
                ? $"Another schedule (\"{there.Name}\") has this schedule's id on the device. Nothing was changed."
                : "A schedule with this name and other times is on the device. Change that one instead. Nothing was changed.";
            s.Fail(message);
            throw new ActionEngineException(message);
        }

        await run.SaveFirstAsync().ConfigureAwait(false);
        await run.StepAsync(step, async _ =>
        {
            await run.Writer.AddScheduleAsync(schedule, run.Ct).ConfigureAwait(false);
            run.Schedules.Add(new UndoSchedule(null, schedule));
            await run.WroteAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        run.ThrowIfFailed("changes");
    }

    private static bool SameTimes(DeviceSchedule a, DeviceSchedule b) =>
        ScheduleText.Normalize(a.Definition) == ScheduleText.Normalize(b.Definition);
}
