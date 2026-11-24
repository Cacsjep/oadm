using System.Text.RegularExpressions;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Schedules.Engine;

/// <summary>The devices of a schedule's choice within one view, worked out in one pass (O(n)).</summary>
/// <param name="Devices">The devices of the choice in the view, in the view's order.</param>
/// <param name="ChosenOutside">Chosen device ids that are not in the view (removed from OADM, or outside the user's devices).</param>
public sealed record DeviceChoice(IReadOnlyList<IDeviceInfo> Devices, IReadOnlyList<Guid> ChosenOutside)
{
    /// <summary>The devices of <paramref name="choice"/> among <paramref name="view"/>.</summary>
    public static DeviceChoice Resolve(ScheduleDevices choice, IReadOnlyList<IDeviceInfo> view)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(view);
        if (choice.Kind == ScheduleDevicesKind.All)
        {
            return new DeviceChoice(view, []);
        }

        var ids = (choice.DeviceIds ?? []).ToHashSet();
        var tags = new HashSet<string>(choice.Tags ?? [], StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<Guid>();
        var devices = new List<IDeviceInfo>();
        foreach (var device in view)
        {
            var byId = ids.Contains(device.Id);
            if (byId)
            {
                found.Add(device.Id);
            }

            if (byId || (tags.Count > 0 && device.Tags.Any(tags.Contains)))
            {
                devices.Add(device);
            }
        }

        return new DeviceChoice(devices, [.. ids.Where(id => !found.Contains(id))]);
    }
}

/// <summary>
/// The check of a schedule (the wizard's steps, the same on save, never writes): the name, the devices within the caller's view,
/// the times, the actions with their rights and settings (Provisioning's node checks), the estimate and its warning.
/// </summary>
public static partial class ScheduleChecks
{
    public const string EnterName = "Enter a name.";
    public const string ChooseDevices = "Choose at least one device.";
    public const string AddAction = "Add at least one action.";

    [GeneratedRegex("^[A-Za-z0-9-]{1,32}$")]
    private static partial Regex ActionId();

    /// <summary>What the check needs besides the schedule.</summary>
    /// <param name="Others">The other stored schedules (the name must be unique).</param>
    /// <param name="Catalog">The actions.</param>
    /// <param name="Context">The kept files and PKI's policies for the actions' settings.</param>
    /// <param name="Now">Now.</param>
    /// <param name="Zone">The server's time zone.</param>
    /// <param name="Parallel">The parallel limit of the estimate.</param>
    /// <param name="LearnedDeviceTime">The time per device of the schedule's last ended run; null: the starting estimates.</param>
    /// <param name="CheckPast">Refuse a Once whose time has passed (true for the wizard and a save).</param>
    /// <param name="StrictDevices">A chosen device outside the caller's view throws <see cref="DevicesNotAvailableException"/> like an unknown id (the wizard, a save); false: an error below the devices (a stored schedule shown).</param>
    /// <param name="KnownDeviceCount">The wizard's devices did not change: their count from before, no resolve and no device check (never on save).</param>
    public sealed record Inputs(
        IReadOnlyCollection<StoredSchedule> Others,
        ActionCatalog Catalog,
        ProfileCheckContext Context,
        DateTimeOffset Now,
        TimeZoneInfo Zone,
        int Parallel,
        TimeSpan? LearnedDeviceTime,
        bool CheckPast = true,
        bool StrictDevices = true,
        int? KnownDeviceCount = null);

    public static async Task<ScheduleCheck> CheckAsync(Schedule schedule, ICallContext caller, Inputs inputs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(inputs);

        var name = schedule.Name?.Trim() ?? string.Empty;
        string? nameError = null;
        if (name.Length == 0)
        {
            nameError = EnterName;
        }
        else if (name.Length > SchedulesInfo.MaxNameLength)
        {
            nameError = $"The name has at most {SchedulesInfo.MaxNameLength} characters.";
        }
        else if (inputs.Others.Any(o => o.Schedule.Id != schedule.Id && string.Equals(o.Schedule.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            nameError = $"A schedule named {name} exists already.";
        }

        var choice = schedule.Devices ?? new ScheduleDevices();
        var resolved = inputs.KnownDeviceCount is null
            ? DeviceChoice.Resolve(choice, await caller.Devices.ListAsync(ct).ConfigureAwait(false))
            : new DeviceChoice([], []);
        string? devicesError = null;
        if (choice.Kind == ScheduleDevicesKind.Chosen && (choice.DeviceIds ?? []).Count == 0 && (choice.Tags ?? []).Count == 0)
        {
            devicesError = ChooseDevices;
        }
        else if (choice.Kind == ScheduleDevicesKind.Chosen && resolved.ChosenOutside.Count > 0)
        {
            // A device outside the caller's view answers like an unknown id (docs/spec/device-scope.md).
            if (inputs.StrictDevices)
            {
                throw new DevicesNotAvailableException(resolved.ChosenOutside.Count);
            }

            devicesError = resolved.ChosenOutside.Count == 1
                ? "A chosen device is no longer in your devices. Choose the devices again."
                : $"{resolved.ChosenOutside.Count} chosen devices are no longer in your devices. Choose the devices again.";
        }
        else if (!Enum.IsDefined(choice.Kind))
        {
            devicesError = ChooseDevices;
        }

        var when = schedule.When ?? new ScheduleWhen();
        var whenError = ScheduleTimes.Problem(when, inputs.Now, inputs.Zone, inputs.CheckPast);

        var actions = schedule.Actions ?? [];
        string? actionsError = null;
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (actions.Count == 0)
        {
            actionsError = AddAction;
        }
        else if (actions.Count > SchedulesInfo.MaxActions)
        {
            actionsError = $"Add at most {SchedulesInfo.MaxActions} actions.";
        }
        else
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action is null || !ActionId().IsMatch(action.Id ?? string.Empty) || !ids.Add(action.Id!))
                {
                    actionsError ??= $"Action {i + 1} needs an id of its own.";
                    continue;
                }

                if (inputs.Catalog.Find(action.Kind) is not { } node)
                {
                    errors[action.Id!] = ActionCatalog.NotAnAction;
                    continue;
                }

                if (node.Check(action.Settings, inputs.Context) is { } error)
                {
                    errors[action.Id!] = error;
                }
                else if (node.Warning(action.Settings, inputs.Context) is { } warning)
                {
                    warnings[action.Id!] = warning;
                }
            }

            if (actionsError is null && errors.Count == 0 && ActionCatalog.Missing(caller, actions) is { } missing)
            {
                actionsError = $"Your role does not allow: {missing.Name}.";
            }
        }

        var count = inputs.KnownDeviceCount is { } known ? Math.Max(0, known) : resolved.Devices.Count;
        var stageSizes = schedule.Stages ? Rollout.StageSizes(count) : [];
        var perDevice = inputs.LearnedDeviceTime ?? StartingDeviceTime(actions);
        var estimate = actions.Count == 0 ? null : Rollout.Estimate(count, schedule.Stages, inputs.Parallel, perDevice);
        var next = whenError is null && schedule.On ? ScheduleTimes.NextAfter(when, inputs.Now, inputs.Zone) : null;
        return new ScheduleCheck
        {
            NameError = nameError,
            DevicesError = devicesError,
            WhenError = whenError,
            ActionsError = actionsError,
            ActionErrors = errors,
            ActionWarnings = warnings,
            WhenWarning = whenError is null ? Rollout.WhenWarning(estimate, ScheduleTimes.ShortestGap(when)) : null,
            DeviceCount = count,
            StageSizes = stageSizes,
            Estimate = estimate,
            NextRun = next,
        };
    }

    /// <summary>
    /// The schedule without the chosen devices it had stored that are no longer in the caller's view (removed from OADM, or the
    /// view changed): they are simply no longer in it, so a schedule whose camera was deleted can still be changed. A device id
    /// the stored schedule did not have is checked as usual (outside the view: like an unknown id), so nothing tells an unknown id
    /// from one outside the view.
    /// </summary>
    public static Schedule DropGone(Schedule schedule, Schedule? stored, IReadOnlyList<IDeviceInfo> view)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(view);
        if (stored?.Devices?.DeviceIds is not { Count: > 0 } before || schedule.Devices?.DeviceIds is not { Count: > 0 } ids)
        {
            return schedule;
        }

        var had = before.ToHashSet();
        var inView = new HashSet<Guid>(view.Count);
        foreach (var device in view)
        {
            inView.Add(device.Id);
        }

        var kept = ids.Where(id => inView.Contains(id) || !had.Contains(id)).ToList();
        return kept.Count == ids.Count ? schedule : schedule with { Devices = schedule.Devices with { DeviceIds = kept } };
    }

    /// <summary>The time per device of the starting estimates (the sum of the actions).</summary>
    public static TimeSpan StartingDeviceTime(IEnumerable<FlowNode> actions) =>
        (actions ?? []).Where(a => a is not null).Aggregate(TimeSpan.Zero, (sum, a) => sum + ActionCatalog.StartingEstimate(a.Kind));

    /// <summary>The first error of a check for a refusal ("Name: Enter a name."); null when it is valid.</summary>
    public static string? FirstError(ScheduleCheck check, Schedule schedule, ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(catalog);
        if ((check.NameError ?? check.DevicesError ?? check.WhenError ?? check.ActionsError) is { } error)
        {
            return error;
        }

        foreach (var action in schedule.Actions ?? [])
        {
            if (action?.Id is { } id && check.ActionErrors.TryGetValue(id, out var actionError))
            {
                return $"{catalog.Find(action.Kind)?.Name ?? "An action"}: {actionError}";
            }
        }

        return null;
    }
}
