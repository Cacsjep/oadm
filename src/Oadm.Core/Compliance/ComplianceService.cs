using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Oadm.Core.Devices;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Compliance;

/// <summary>Saving, deleting and turning checks on or off with every rule checked, plus the choices of a type's settings.</summary>
public sealed class ComplianceService(
    ComplianceStore store, ComplianceEngine engine, DeviceRepository devices, IVapixClientFactory vapix, ILoggerFactory loggers)
{
    public ComplianceEngine Engine => engine;

    /// <summary>Turns compliance on or off for the server.</summary>
    public Task SetEnabledAsync(bool enabled, CancellationToken ct) => engine.SetEnabledAsync(enabled, ct);

    /// <summary>
    /// Checks <paramref name="check"/> and stores it; returns the stored check and whether it is new. Throws
    /// <see cref="ComplianceValidationException"/> with the first problem and <see cref="KeyNotFoundException"/>.
    /// </summary>
    public async Task<(ComplianceCheck Check, bool Created)> SaveAsync(ComplianceCheck check, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        var existing = check.Id == Guid.Empty ? null : engine.GetCheck(check.Id) ?? throw new KeyNotFoundException("The check no longer exists.");
        if (existing?.BuiltIn == true)
        {
            check.Name = existing.Name;
            check.TypeId = existing.TypeId;
            check.Severity = existing.Severity;
            check.Enabled = true;
            check.Settings = [];
        }

        var problem = Problem(check);
        if (problem is not null)
        {
            throw new ComplianceValidationException(problem);
        }

        var stored = await store.SaveCheckAsync(check, ct).ConfigureAwait(false);
        engine.ApplySaved(stored);
        return (stored, existing is null);
    }

    public async Task<ComplianceCheck> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        var stored = await store.SetEnabledAsync(id, enabled, ct).ConfigureAwait(false);
        engine.ApplySaved(stored);
        return stored;
    }

    /// <summary>Deletes a check; returns its name.</summary>
    public async Task<string> DeleteAsync(Guid id, CancellationToken ct)
    {
        var name = await store.DeleteCheckAsync(id, ct).ConfigureAwait(false);
        engine.ApplyDeleted(id);
        return name;
    }

    /// <summary>The choices of a setting of a type, read from <paramref name="deviceId"/> when given (read only).</summary>
    public async Task<IReadOnlyList<ActionChoice>> ListChoicesAsync(string typeId, string fieldKey, Guid? deviceId, CancellationToken ct)
    {
        var type = engine.Catalog.Find(typeId) ?? throw new KeyNotFoundException("This kind of check does not exist.");
        if (!type.Available)
        {
            throw new InvalidOperationException(type.UnavailableText);
        }

        IDeviceInfo? device = null;
        IVapixClient? client = null;
        if (deviceId is { } id)
        {
            var row = await devices.GetAsync(id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(DeviceMessages.Removed);
            if (DeviceMessages.Refusal(row.Status, refuseUnreachable: true) is { } refusal)
            {
                throw new InvalidOperationException(refusal);
            }

            device = row;
            client = await vapix.CreateAsync(id, ct).ConfigureAwait(false);
        }

        var context = new QueryContext(loggers.CreateLogger("Oadm.Compliance." + type.Id), type.Owner, devices, device, client);
        return await type.Type.ListChoicesAsync(context, fieldKey, ct).ConfigureAwait(false) ?? [];
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
