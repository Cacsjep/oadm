using System.Globalization;
using System.Text.Json;

using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ActionRules;

/// <summary>
/// The task of the Action Rule Editor, one per device: change, delete, copy or put back rules, recipients and schedules.
/// Started only from the page (<see cref="ShowInMenus"/> false), whose methods carry the rights. Device safety: one task per
/// device at a time; everything is read fresh first; a rule, recipient or schedule is only written while it is exactly as
/// the page read it (its version); everything else stays as each device has it. What the task changes is saved for undo
/// before the first write and after every write (as it was before and as the task left it). Passwords are read over SOAP
/// and stay on the server; an action is only sent when it changes. Rules and recipients change in place where the device
/// offers it (AXIS OS 12), else they are recreated (new first, then the old one removed); schedules keep their id.
/// </summary>
public sealed partial class RuleTaskPlugin : ITaskPlugin
{
    public const string ReadStep = "Read the action rules";
    public const string SaveStep = "Save for undo";

    private readonly ActionRulesPlugin? _owner;

    /// <summary>Created by its core plugin only (no public constructor, so the loader never registers it alone).</summary>
    internal RuleTaskPlugin(ActionRulesPlugin? owner)
    {
        _owner = owner;
    }

    public string Id => ActionRulesInfo.TaskPluginId;

    public string DisplayName => "Action rules";

    public string? IconKey => ActionRulesInfo.IconKey;

    public string Group => TaskGroups.Maintenance;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool ShowInMenus => false;

    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device.Status) is null;
    }

    public string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device.Status);
    }

    public string GetTaskName(string? payloadJson)
    {
        try
        {
            return ActionRulesJson.Deserialize<RuleTaskPayload>(payloadJson).Description;
        }
        catch (JsonException)
        {
            return ActionRulesInfo.DisplayName;
        }
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = Validate(ActionRulesJson.Deserialize<RuleTaskPayload>(payloadJson));
        var gate = _owner?.DeviceGate(device.Id);
        var held = false;
        Run? run = null;
        try
        {
            ctx.PlanSteps(ReadStep, SaveStep);
            if (gate is not null)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false); // one change per device at a time; read after waiting
                held = true;
            }

            var (set, caps) = await ctx.StepAsync(ReadStep, async step =>
            {
                var read = await SoapActionEngine.ReadAsync(ctx.Vapix, ct).ConfigureAwait(false);
                var rest = await RestActionRules.ReadCapabilitiesAsync(ctx.Vapix, ct).ConfigureAwait(false);
                step.Complete(Count(read.Rules.Count, "rule") + (rest.Rules ? ", changed in place" : ", changed by recreating"));
                return (read, rest);
            }).ConfigureAwait(false);

            run = new Run(ctx, device, payload, set, new RuleWriter(ctx.Vapix, set, caps.Rules, caps.Schedules), _owner?.Undo, ct);
            switch (payload.Action)
            {
                case RuleTaskActions.Undo:
                    ctx.SkipStep(SaveStep, "Not needed to undo.");
                    await UndoAsync(run).ConfigureAwait(false);
                    break;
                case RuleTaskActions.Edit or RuleTaskActions.Delete:
                    await ChangeRulesAsync(run).ConfigureAwait(false);
                    break;
                case RuleTaskActions.Deploy:
                    await DeployRuleAsync(run).ConfigureAwait(false);
                    break;
                case RuleTaskActions.RecipientEdit or RuleTaskActions.RecipientDelete:
                    await ChangeRecipientsAsync(run).ConfigureAwait(false);
                    break;
                case RuleTaskActions.RecipientDeploy:
                    await DeployRecipientAsync(run).ConfigureAwait(false);
                    break;
                case RuleTaskActions.ScheduleEdit or RuleTaskActions.ScheduleDelete:
                    await ChangeSchedulesAsync(run).ConfigureAwait(false);
                    break;
                case RuleTaskActions.ScheduleDeploy:
                    await DeployScheduleAsync(run).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            if (run is { Dirty: true })
            {
                await run.SaveQuietlyAsync().ConfigureAwait(false); // also after a failure or a cancel: undo knows every write
            }

            if (held)
            {
                gate!.Release();
            }

            if (_owner is not null)
            {
                _owner.TaskEnded(payload.RunId);
                await _owner.RefreshRowAsync(device.Id).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Everything one task works with; the lists collect what it changed for undo.</summary>
    private sealed class Run(ITaskExecutionContext ctx, IDeviceInfo device, RuleTaskPayload payload, DeviceRuleSet set, RuleWriter writer, UndoStore? store, CancellationToken ct)
    {
        public ITaskExecutionContext Ctx => ctx;

        public IDeviceInfo Device => device;

        public RuleTaskPayload Payload => payload;

        public DeviceRuleSet Set => set;

        public RuleWriter Writer => writer;

        public CancellationToken Ct => ct;

        public List<UndoRule> Rules { get; } = [];

        public List<UndoRecipient> Recipients { get; } = [];

        public List<UndoSchedule> Schedules { get; } = [];

        /// <summary>Something was written since the last save.</summary>
        public bool Dirty { get; private set; }

        public int Failed { get; private set; }

        public int Done { get; private set; }

        public string? LastError { get; private set; }

        /// <summary>The ids this device has in the run (rules, recipients or schedules).</summary>
        public IReadOnlyList<RuleRef> Targets => payload.Targets.FirstOrDefault(t => t.DeviceId == device.Id)?.Rules ?? [];

        /// <summary>Saves what was collected before the first write (the save step; a failure stops the task).</summary>
        public async Task SaveFirstAsync()
        {
            if (store is not { IsAvailable: true })
            {
                ctx.SkipStep(SaveStep, "Undo is not available on this server.");
                return;
            }

            await ctx.StepAsync(SaveStep, async _ => await store.SaveDeviceAsync(payload.RunId, Snapshot(), ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        /// <summary>
        /// A recreate made a new rule before removing the old one: recorded as created until <see cref="Settled"/> says the
        /// replace finished, so undo can remove it when the device kept both.
        /// </summary>
        public Task RecreatedAsync(DeviceRule created)
        {
            Rules.Add(new UndoRule(null, created));
            return WroteAsync();
        }

        /// <summary>The replace finished: the rule it created is the changed rule, no longer a rule of its own.</summary>
        public void Settled(DeviceRule now) => Rules.RemoveAll(u => u.Before is null && u.After?.Id == now.Id && payload.Action != RuleTaskActions.Deploy);

        public Task RecreatedAsync(DeviceRecipient created)
        {
            Recipients.Add(new UndoRecipient(null, created));
            return WroteAsync();
        }

        public void Settled(DeviceRecipient now) => Recipients.RemoveAll(u => u.Before is null && u.After?.Id == now.Id && payload.Action != RuleTaskActions.RecipientDeploy);

        /// <summary>Records a write: saved for undo right away.</summary>
        public async Task WroteAsync()
        {
            Dirty = true;
            if (store is { IsAvailable: true })
            {
                await store.SaveDeviceAsync(payload.RunId, Snapshot(), CancellationToken.None).ConfigureAwait(false);
                Dirty = false;
            }
        }

        public async Task SaveQuietlyAsync()
        {
            try
            {
                await WroteAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                ctx.Log(TaskLogLevel.Error, "Saving for undo failed: " + ex.Message);
            }
        }

        /// <summary>One named step; a refusal or a device timeout fails the step and the task goes on with the next one.</summary>
        public async Task<bool> StepAsync(string name, Func<ITaskStep, Task> body)
        {
            using var step = ctx.BeginStep(name);
            try
            {
                await body(step).ConfigureAwait(false);
                Done++;
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Failed++;
                LastError = ex is OperationCanceledException ? "The device did not answer in time." : ex.Message;
                step.Fail(LastError);
                ctx.Log(TaskLogLevel.Error, name + ": " + LastError);
                return false;
            }
        }

        /// <summary>A skipped part that the user should see (Done with warnings).</summary>
        public void Warn(string step, string reason)
        {
            ctx.SkipStep(step, reason);
            ctx.ReportWarning(step + ": " + reason);
        }

        /// <summary>Ends the task: fails it when a part failed.</summary>
        public void ThrowIfFailed(string what)
        {
            if (Failed == 0)
            {
                return;
            }

            var total = Failed + Done;
            throw new ActionEngineException(Failed < total
                ? string.Create(CultureInfo.InvariantCulture, $"{Failed} of {total} {what} failed; the others were changed.")
                : Failed == 1 ? LastError! : "Every change failed: " + LastError);
        }

        private DeviceUndo Snapshot() => new(device.Id, [.. Rules]) { Recipients = [.. Recipients], Schedules = [.. Schedules] };
    }

    /// <summary>Changes or deletes the chosen rules.</summary>
    private static async Task ChangeRulesAsync(Run run)
    {
        var work = new List<DeviceRule>();
        foreach (var target in run.Targets)
        {
            var rule = run.Set.Rules.FirstOrDefault(r => r.Id == target.Id);
            var step = StepName(run.Payload.Action, rule?.Name ?? target.Id);
            if (rule is null)
            {
                run.Warn(step, "The rule is not on the device any more.");
            }
            else if (RuleView.VersionOf(rule) != target.Version)
            {
                run.Warn(step, RuleTexts.ChangedSinceRead);
            }
            else
            {
                work.Add(rule);
            }
        }

        if (work.Count == 0)
        {
            run.Ctx.SkipStep(SaveStep, "Nothing to change.");
            return;
        }

        run.Rules.AddRange(work.Select(r => new UndoRule(r, r)));
        var patch = run.Payload.Patch;
        Dictionary<string, DeviceSchedule> create = [];
        if (patch is not null && run.Payload.Action != RuleTaskActions.Delete && NewSchedules(patch).FirstOrDefault() is { } schedule)
        {
            // The schedule of the change: this device's one with the same id or name and times, else created first.
            var probe = new DeviceRule(string.Empty, string.Empty, false, null, [RuleText.ScheduleCondition(schedule.Id)], null, new RuleAction(string.Empty, []));
            var (fitted, missing) = FitSchedules(probe, [schedule], run.Set);
            patch = patch with { ScheduleId = RuleText.ScheduleId(fitted.Conditions[0]) };
            create = missing;
        }

        await run.SaveFirstAsync().ConfigureAwait(false);
        foreach (var s in create.Values)
        {
            await run.StepAsync("Create schedule " + Short(s.Name), async _ =>
            {
                await run.Writer.AddScheduleAsync(s, run.Ct).ConfigureAwait(false);
                run.Schedules.Add(new UndoSchedule(null, s));
                await run.WroteAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        if (run.Failed > 0)
        {
            foreach (var rule in work)
            {
                run.Ctx.SkipStep(StepName(run.Payload.Action, rule.Name), "Not run: a schedule could not be created.");
            }

            run.ThrowIfFailed("parts");
            return;
        }

        for (var i = 0; i < work.Count; i++)
        {
            var rule = work[i];
            var index = i;
            await run.StepAsync(StepName(run.Payload.Action, rule.Name), async step =>
            {
                if (run.Payload.Action == RuleTaskActions.Delete)
                {
                    await run.Writer.DeleteAsync(rule, run.Ct).ConfigureAwait(false);
                    run.Rules[index] = run.Rules[index] with { After = null };
                    await run.WroteAsync().ConfigureAwait(false);
                    return;
                }

                var own = patch!.RecipientName is { } recipient ? WithRecipient(patch, patch.Template ?? rule.Action.Template, recipient, run.Set) : patch;
                var after = RulePatchApply.Apply(rule, own, run.Set);
                var leftOut = RulePatchApply.LeftOut(after.Action.Template, patch, run.Set);
                if (leftOut.Count > 0)
                {
                    run.Ctx.ReportWarning(rule.Name + ": this device's action has no " + string.Join(", ", leftOut) + "; left out.");
                }

                if (RuleContent.Same(after, rule))
                {
                    step.Skip("Already as chosen.");
                    return;
                }

                var now = await run.Writer.ReplaceAsync(rule, after, run.Ct, run.RecreatedAsync).ConfigureAwait(false);
                run.Rules[index] = run.Rules[index] with { After = now };
                run.Settled(now);
                await run.WroteAsync().ConfigureAwait(false);
                step.Complete(now.Id == rule.Id ? null : "New rule id " + now.Id);
            }).ConfigureAwait(false);
        }

        run.ThrowIfFailed("rules");
    }

    /// <summary>
    /// Puts back what a run changed, each item only while it is still as the run left it (a later change stays and is
    /// reported). Order: deleted and changed schedules first (rules need them), recipients, rules, then the schedules the
    /// run created (only when no rule uses them).
    /// </summary>
    private async Task UndoAsync(Run run)
    {
        var undo = _owner?.Undo is { } store ? await store.ReadDeviceAsync(run.Payload.UndoOf!.Value, run.Device.Id, run.Ct).ConfigureAwait(false) : null;
        if (undo is null || undo.IsEmpty)
        {
            run.Ctx.ReportWarning("Nothing was saved for this device.");
            return;
        }

        var set = run.Set;
        const string later = "Changed after this change. Left as it is.";
        foreach (var s in undo.Schedules.Where(s => s.Before is not null))
        {
            var before = s.Before!;
            await run.StepAsync("Put back schedule " + Short(before.Name), async step =>
            {
                var now = set.Schedules.FirstOrDefault(x => x.Id == before.Id);
                if (now is not null && RuleContent.Same(now, before))
                {
                    step.Skip("Already as before.");
                }
                else if (now is null && s.After is null)
                {
                    await run.Writer.AddScheduleAsync(before, run.Ct).ConfigureAwait(false);
                }
                else if (now is not null && s.After is not null && RuleContent.Same(now, s.After))
                {
                    await run.Writer.ReplaceScheduleAsync(now, before, run.Ct).ConfigureAwait(false);
                }
                else
                {
                    step.Skip(later);
                    run.Ctx.ReportWarning("Schedule " + before.Name + ": " + later);
                }
            }).ConfigureAwait(false);
        }

        foreach (var r in undo.Recipients)
        {
            var name = (r.Before ?? r.After)!.Name;
            await run.StepAsync((r.Before is null ? "Delete recipient " : "Put back recipient ") + Short(name), async step =>
            {
                var now = Own(set.Recipients, r.After, x => x.Id, RuleContent.Same);
                var original = r.Before is null ? null : set.Recipients.FirstOrDefault(x => x.Id == r.Before.Id && RuleContent.Same(x, r.Before));
                switch (Plan(r.Before is null, r.After is null, now is not null, original is not null, r.Before is not null && r.After is not null && RuleContent.Same(r.Before, r.After)))
                {
                    case UndoStep.Remove:
                        await run.Writer.DeleteRecipientAsync(now!, run.Ct).ConfigureAwait(false);
                        break;
                    case UndoStep.Add:
                        await run.Writer.AddRecipientAsync(r.Before!, run.Ct).ConfigureAwait(false);
                        break;
                    case UndoStep.Replace:
                        await run.Writer.ReplaceRecipientAsync(now!, r.Before!, run.Ct).ConfigureAwait(false);
                        break;
                    case UndoStep.Already:
                        step.Skip("Already as before.");
                        break;
                    case UndoStep.Gone:
                        step.Skip("Not on the device as this change left it.");
                        break;
                    default:
                        step.Skip(later);
                        run.Ctx.ReportWarning("Recipient " + name + ": " + later);
                        break;
                }
            }).ConfigureAwait(false);
        }

        foreach (var saved in undo.Rules)
        {
            var name = (saved.Before ?? saved.After)!.Name;
            await run.StepAsync((saved.Before is null ? "Delete rule " : "Put back rule ") + Short(name), async step =>
            {
                var current = Own(set.Rules, saved.After, x => x.Id, RuleContent.Same);
                var original = saved.Before is null ? null : set.Rules.FirstOrDefault(x => x.Id == saved.Before.Id && RuleContent.Same(x, saved.Before));
                switch (Plan(saved.Before is null, saved.After is null, current is not null, original is not null, saved.Before is not null && saved.After is not null && RuleContent.Same(saved.Before, saved.After)))
                {
                    case UndoStep.Remove:
                        await run.Writer.DeleteAsync(current!, run.Ct).ConfigureAwait(false);
                        break;
                    case UndoStep.Add:
                        var added = await run.Writer.AddAsync(saved.Before! with { Id = string.Empty }, run.Ct).ConfigureAwait(false);
                        step.Complete("Created again as rule " + added.Id);
                        break;
                    case UndoStep.Replace:
                        await run.Writer.ReplaceAsync(current!, saved.Before! with { Id = current!.Id, Action = saved.Before.Action with { ConfigurationId = current.Action.ConfigurationId } }, run.Ct).ConfigureAwait(false);
                        break;
                    case UndoStep.Already:
                        step.Skip("Already as before.");
                        break;
                    case UndoStep.Gone:
                        step.Skip("Not on the device as this change left it.");
                        break;
                    default:
                        step.Skip(later);
                        run.Ctx.ReportWarning("Rule " + name + ": " + later);
                        break;
                }
            }).ConfigureAwait(false);
        }

        foreach (var s in undo.Schedules.Where(s => s.Before is null && s.After is not null))
        {
            var created = s.After!;
            await run.StepAsync("Delete schedule " + Short(created.Name), async step =>
            {
                if (set.Schedules.FirstOrDefault(x => x.Id == created.Id) is not { } now || !RuleContent.Same(now, created))
                {
                    step.Skip("Not on the device as this change left it.");
                    return;
                }

                // Rules that still use it (made after the run, or a rule the undo could not delete) keep it.
                var rules = await SoapActionEngine.ReadRulesAsync(run.Ctx.Vapix, run.Ct).ConfigureAwait(false);
                if (rules.Any(r => ScheduleView.UsesSchedule(r, now.Id)))
                {
                    step.Skip("Rules use it.");
                    return;
                }

                await run.Writer.DeleteScheduleAsync(now, run.Ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        run.ThrowIfFailed("parts");
    }

    private enum UndoStep
    {
        Remove,
        Add,
        Replace,
        Already,
        Gone,
        Later,
    }

    /// <summary>
    /// What undo does with one item: <paramref name="created"/> (no before) = remove the run's item while it is as the run left
    /// it; <paramref name="deleted"/> (no after) = add it again unless the original (same id and content) is there; changed =
    /// put back the run's item, or add it again when the run changed nothing and it is gone (lost on the device), else leave a
    /// later change. A twin with the same content never counts as the run's item.
    /// </summary>
    private static UndoStep Plan(bool created, bool deleted, bool ownThere, bool originalThere, bool unchanged) =>
        created ? (ownThere ? UndoStep.Remove : UndoStep.Gone)
        : originalThere ? UndoStep.Already
        : deleted ? UndoStep.Add
        : ownThere ? (unchanged ? UndoStep.Already : UndoStep.Replace)
        : unchanged ? UndoStep.Add
        : UndoStep.Later;

    /// <summary>The run's item as it left it: by id and content, else by content when exactly one item matches.</summary>
    private static T? Own<T>(IReadOnlyList<T> items, T? after, Func<T, string> id, Func<T, T, bool> same)
        where T : class
    {
        if (after is null)
        {
            return null;
        }

        var byId = items.FirstOrDefault(x => id(x) == id(after) && same(x, after));
        if (byId is not null)
        {
            return byId;
        }

        var matches = items.Where(x => same(x, after)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null; // a later run may have recreated it with another id
    }

    /// <summary>Checks the payload before any device access ("Nothing was changed" on a bad one).</summary>
    internal static RuleTaskPayload Validate(RuleTaskPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        string? problem = payload.Action switch
        {
            _ when !RuleTaskActions.IsKnown(payload.Action) => "Unknown action.",
            RuleTaskActions.Edit => payload.Patch is null ? "Choose what to change." : payload.Patch.Problem(),
            RuleTaskActions.Undo when payload.UndoOf is null => "Choose the change to undo.",
            RuleTaskActions.Deploy when payload.Rule is null => "Choose the rule to copy.",
            RuleTaskActions.Deploy => payload.Patch is { IsEmpty: false } patch ? patch.Problem() : null,
            RuleTaskActions.RecipientEdit when payload.RecipientPatch is null or { IsEmpty: true } => "Choose what to change.",
            RuleTaskActions.RecipientDeploy when payload.Recipient is null => "Choose the recipient to copy.",
            RuleTaskActions.ScheduleEdit when payload.SchedulePatch is null or { Name: null, Definition: null } => "Choose what to change.",
            RuleTaskActions.ScheduleDeploy when payload.Schedules is not { Count: 1 } => "Choose the schedule.",
            _ => null,
        };
        return problem is null ? payload : throw new ArgumentException(problem + " Nothing was changed.");
    }

    /// <summary>"Change rule Door alarm", "Delete rule Door alarm".</summary>
    public static string StepName(string action, string ruleName) =>
        (action == RuleTaskActions.Delete ? "Delete rule " : "Change rule ") + Short(ruleName);

    private static string Short(string name) => name.Length > 60 ? name[..59] + "…" : name;

    private static string Count(int count, string what) =>
        count == 1 ? "1 " + what : string.Create(CultureInfo.InvariantCulture, $"{count} {what}s");
}

/// <summary>Texts shared by the tasks and the page.</summary>
public static class RuleTexts
{
    public const string ChangedSinceRead = "The rule changed since it was read. Read the rules again.";
    public const string RecipientChangedSinceRead = "The recipient changed since it was read. Read the rules again.";
    public const string ScheduleChangedSinceRead = "The schedule changed since it was read. Read the rules again.";
    public const string AlreadyThere = "Already on the device.";
}
