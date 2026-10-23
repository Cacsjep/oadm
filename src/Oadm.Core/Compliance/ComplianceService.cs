using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Compliance;

/// <summary>
/// Saving, deleting and turning checks on or off with every rule checked, plus the choices of a type's settings. Device
/// groups (docs/spec/device-scope.md, Shared objects): every check has an owner (all devices or a group); a user of a group
/// sees and changes only the checks of the group, a new check belongs to the caller's devices, only administrators choose
/// another owner, and the chosen devices and tags must be the owner's (NOT_FOUND otherwise). Check now and the results
/// follow the caller's view.
/// </summary>
public sealed class ComplianceService(
    ComplianceStore store,
    ComplianceEngine engine,
    IVapixClientFactory vapix,
    ILoggerFactory loggers,
    DeviceViewFactory views,
    OwnerScopes owners,
    DeviceTagStore tags)
{
    /// <summary>The refusal of a check someone else owns, or one that is gone (the same answer).</summary>
    public const string NoLongerExists = "The check no longer exists.";

    public ComplianceEngine Engine => engine;

    /// <summary>The owners of the checks (group names).</summary>
    public OwnerScopes Owners => owners;

    /// <summary>Turns compliance on or off for the server.</summary>
    public Task SetEnabledAsync(bool enabled, CancellationToken ct) => engine.SetEnabledAsync(enabled, ct);

    /// <summary>The checks the user of <paramref name="view"/> sees (all of them for all devices, else the group's).</summary>
    public IReadOnlyList<ComplianceCheckState> ListChecks(DeviceView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var all = engine.ListChecks();
        return view.HasAllDevices ? all : [.. all.Where(c => OwnerScopes.Sees(view, c.Check.Owner))];
    }

    /// <summary>One check the user sees, else <see cref="KeyNotFoundException"/> (unknown and hidden alike).</summary>
    public ComplianceCheckState Find(DeviceView view, Guid id) =>
        ListChecks(view).FirstOrDefault(c => c.Check.Id == id) ?? throw new KeyNotFoundException(NoLongerExists);

    /// <summary>Whether the user sees the results of the check (its owner is the user's group, or the user has all devices).</summary>
    public bool SeesResultsOf(DeviceView view, Guid checkId)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.HasAllDevices || (engine.OwnerOf(checkId) is { } owner && OwnerScopes.Sees(view, owner));
    }

    /// <summary>
    /// Checks <paramref name="check"/> and stores it; returns the stored check and whether it is new. The owner: a new check
    /// belongs to the caller's devices, a changed one keeps its owner; <paramref name="owner"/> sets another one, allowed
    /// only with <paramref name="mayChooseOwner"/> (administrators). Throws <see cref="ComplianceValidationException"/>
    /// with the first problem, <see cref="KeyNotFoundException"/> (a check, device group or tag that does not exist for the
    /// owner), <see cref="DevicesNotAvailableException"/> (chosen devices outside the owner's devices) and
    /// <see cref="UnauthorizedAccessException"/> (another owner without the right). A caller without devices (no group, or
    /// the group was deleted) saves nothing (<see cref="DeviceAccessRefusedException"/>, <see cref="DeviceGroupTexts.NoDevicesRefusal"/>).
    /// </summary>
    public async Task<(ComplianceCheck Check, bool Created)> SaveAsync(
        ComplianceCheck check, DeviceView caller, DeviceScope? owner, bool mayChooseOwner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(caller);
        if (!caller.HasAllDevices && caller.GroupId is null)
        {
            throw new DeviceAccessRefusedException(DeviceGroupTexts.NoDevicesRefusal);
        }

        var existing = check.Id == Guid.Empty ? null : engine.GetCheck(check.Id);
        if (check.Id != Guid.Empty && (existing is null || !OwnerScopes.Sees(caller, existing.Owner)))
        {
            throw new KeyNotFoundException(NoLongerExists);
        }

        var kept = existing?.Owner ?? OwnerScopes.OwnerOf(caller);
        var target = owner ?? kept;
        if (target != kept && !mayChooseOwner)
        {
            throw new UnauthorizedAccessException(OwnerRules.OnlyAdministrators);
        }

        if (!target.AllDevices && target.GroupId is { } group && !owners.GroupExists(group))
        {
            throw new KeyNotFoundException(DeviceGroupTexts.UnknownGroup);
        }

        if (target.IsNone && check.Enabled)
        {
            throw new ComplianceValidationException(DeviceGroupTexts.ChooseDevices);
        }

        var problem = Problem(check);
        if (problem is not null)
        {
            throw new ComplianceValidationException(problem);
        }

        await RequireOwnersDevicesAsync(check.Devices, target, caller.UserName, ct).ConfigureAwait(false);
        check.Owner = target;
        var stored = await store.SaveCheckAsync(check, ct).ConfigureAwait(false);
        engine.ApplySaved(stored);
        return (stored, existing is null);
    }

    /// <summary>Turns a check the caller sees on or off; a check without an owner stays off.</summary>
    public async Task<ComplianceCheck> SetEnabledAsync(Guid id, bool enabled, DeviceView caller, CancellationToken ct)
    {
        var check = Find(caller, id).Check;
        if (enabled && check.Owner.IsNone)
        {
            throw new ComplianceValidationException(DeviceGroupTexts.ChooseDevices);
        }

        var stored = await store.SetEnabledAsync(id, enabled, ct).ConfigureAwait(false);
        engine.ApplySaved(stored);
        return stored;
    }

    /// <summary>Deletes a check the caller sees; returns its name.</summary>
    public async Task<string> DeleteAsync(Guid id, DeviceView caller, CancellationToken ct)
    {
        Find(caller, id);
        var name = await store.DeleteCheckAsync(id, ct).ConfigureAwait(false);
        engine.ApplyDeleted(id);
        return name;
    }

    /// <summary>
    /// Check now ("empty means all" = the caller's view, docs/spec/device-scope.md): the given checks (each one the caller
    /// sees, else NOT_FOUND; none = every check the caller sees) on the given devices (none = every device of the view).
    /// </summary>
    public async Task CheckNowAsync(IReadOnlyCollection<Guid> checkIds, ViewedDevices devices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkIds);
        ArgumentNullException.ThrowIfNull(devices);
        var view = devices.View;
        var visible = ListChecks(view).Select(c => c.Check.Id).ToHashSet();
        if (checkIds.Any(id => !visible.Contains(id)))
        {
            throw new KeyNotFoundException(NoLongerExists);
        }

        if (view.HasAllDevices)
        {
            engine.CheckNow(checkIds, devices.Ids); // as before: none = every check, every device of each check
            return;
        }

        IReadOnlyCollection<Guid> chosen = checkIds.Count > 0 ? checkIds : visible;
        IReadOnlyCollection<Guid> deviceIds = devices.Count > 0 ? devices.Ids : [.. await view.IdsAsync(ct).ConfigureAwait(false)];
        if (chosen.Count > 0 && deviceIds.Count > 0)
        {
            engine.CheckNow(chosen, deviceIds);
        }
    }

    /// <summary>
    /// The choices of a setting of a type, read from the first of <paramref name="devices"/> when given (read only). The
    /// type lists only the devices of the caller's view.
    /// </summary>
    public async Task<IReadOnlyList<ActionChoice>> ListChoicesAsync(string typeId, string fieldKey, ViewedDevices devices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var type = engine.Catalog.Find(typeId) ?? throw new KeyNotFoundException("This kind of check does not exist.");
        if (!type.Available)
        {
            throw new InvalidOperationException(type.UnavailableText);
        }

        IDeviceInfo? device = null;
        IVapixClient? client = null;
        if (devices.Count > 0)
        {
            var row = devices[0];
            if (DeviceMessages.Refusal(row, refuseUnreachable: true) is { } refusal)
            {
                throw new InvalidOperationException(refusal);
            }

            device = row;
            client = await vapix.CreateAsync(row.Id, ct).ConfigureAwait(false);
        }

        var all = await views.ForScopeAsync(DeviceScope.All, devices.View.UserName, ct).ConfigureAwait(false);
        var repository = devices.View.HasAllDevices ? new DeviceViewRepository(all) : new DeviceViewRepository(devices.View);
        var context = new QueryContext(loggers.CreateLogger("Oadm.Compliance." + type.Id), type.Owner, repository, device, client);
        return await type.Type.ListChoicesAsync(context, fieldKey, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>
    /// The chosen devices and tags of a check owned by a group must be the group's: every chosen device in the owner's view
    /// (<see cref="DevicesNotAvailableException"/>), every tag a defined tag or a name on a device of the view.
    /// </summary>
    private async Task RequireOwnersDevicesAsync(DeviceSelector selector, DeviceScope owner, string? userName, CancellationToken ct)
    {
        var view = await views.ForScopeAsync(owner, userName, ct).ConfigureAwait(false);
        if (view.HasAllDevices)
        {
            return;
        }

        await view.GetAsync(selector.DeviceIds, ct).ConfigureAwait(false);
        await OwnerRules.RequireTagsAsync(selector.Tags, view, tags, ct).ConfigureAwait(false);
    }

    /// <summary>The first problem of a check, or null. Shared rules: name, type, devices, interval, the type's settings.</summary>
    public string? Problem(ComplianceCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        check.Name = check.Name?.Trim() ?? string.Empty;
        if (check.Name.Length == 0)
        {
            return "Enter a name.";
        }

        if (check.Name.Length > ComplianceStore.MaxNameLength || check.Name.Any(char.IsControl))
        {
            return string.Create(CultureInfo.InvariantCulture, $"The name must be 1 to {ComplianceStore.MaxNameLength} characters on one line.");
        }

        if (check.Devices.Mode is DeviceSelectorMode.EventDevice)
        {
            return "Choose the devices.";
        }

        if (check.Devices.IsEmptyChoice)
        {
            return "Choose at least one device or tag.";
        }

        if (!Enum.IsDefined(check.Severity))
        {
            return "Choose Problem or Warning.";
        }

        var type = engine.Catalog.Find(check.TypeId);
        if (type is null)
        {
            return "Choose the kind of check.";
        }

        if (!type.Available)
        {
            return type.UnavailableText + ".";
        }

        if (type.Type.UsesCachedDataOnly)
        {
            check.IntervalMinutes = 0;
        }
        else if (check.IntervalMinutes is < ComplianceStore.MinInterval or > ComplianceStore.MaxInterval)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Check every {ComplianceStore.MinInterval} to {ComplianceStore.MaxInterval} minutes.");
        }

        IReadOnlyList<ActionField> fields;
        try
        {
            fields = type.Type.Fields ?? [];
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return "The check cannot be set up: " + ex.Message;
        }

        var errors = ActionSettings.Check(fields, check.Settings, statefulRule: true);
        if (errors.Count > 0)
        {
            var (key, message) = errors.First();
            return (fields.FirstOrDefault(f => f.Key == key)?.Label ?? key) + ": " + message;
        }

        try
        {
            var more = type.Type.Validate(JsonSerializer.SerializeToElement(check.Settings));
            if (more.Count > 0)
            {
                var (key, message) = more.First();
                var label = fields.FirstOrDefault(f => f.Key == key)?.Label;
                return label is null ? message : label + ": " + message;
            }
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return "The settings cannot be checked: " + ex.Message;
        }

        return null;
    }

    /// <summary>A check with every setting of its type at its default (a new check of the dialog).</summary>
    public static JsonObject Defaults(CatalogComplianceType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ActionSettings.Defaults(type.Type.Fields);
    }

    private sealed record QueryContext(ILogger Logger, ICorePlugin? Owner, IDeviceRepository Devices, IDeviceInfo? Device, IVapixClient? Vapix)
        : IComplianceQueryContext;
}
