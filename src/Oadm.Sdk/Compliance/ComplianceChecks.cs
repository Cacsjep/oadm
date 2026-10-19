using System.Text.Json;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Compliance;

/// <summary>How a failed check counts: red or amber. The user sets it per check; the type gives the default.</summary>
public enum ComplianceSeverity
{
    /// <summary>Red: the device needs attention.</summary>
    Problem = 0,

    /// <summary>Amber: worth a look.</summary>
    Warning = 1,
}

/// <summary>The result of one check on one device.</summary>
public enum ComplianceOutcome
{
    /// <summary>Not checked yet, or the device is not contacted (its status is a problem of its own).</summary>
    NotChecked = 0,

    /// <summary>The device meets the check.</summary>
    Passed = 1,

    /// <summary>The device does not meet the check; the check's severity decides red or amber.</summary>
    Failed = 2,

    /// <summary>The check cannot mean anything for this device ("HTTPS is off").</summary>
    NotApplicable = 3,

    /// <summary>A read failed ("Timeout after 15 s"); counts neither as passed nor as failed.</summary>
    CouldNotCheck = 4,
}

/// <summary>What a check type found on one device, with a short text for the page ("Object Analytics is stopped").</summary>
/// <param name="Outcome">Passed, failed, not applicable or could not check.</param>
/// <param name="Detail">One short line; the tooltip of the result. At most 1,000 characters are kept.</param>
public sealed record ComplianceResult(ComplianceOutcome Outcome, string Detail)
{
    /// <summary>The device meets the check.</summary>
    public static ComplianceResult Passed(string detail = "") => new(ComplianceOutcome.Passed, detail);

    /// <summary>The device does not meet the check.</summary>
    public static ComplianceResult Failed(string detail) => new(ComplianceOutcome.Failed, detail);

    /// <summary>The check does not apply to the device.</summary>
    public static ComplianceResult NotApplicable(string reason) => new(ComplianceOutcome.NotApplicable, reason);

    /// <summary>A read failed; the reason is shown.</summary>
    public static ComplianceResult CouldNotCheck(string reason) => new(ComplianceOutcome.CouldNotCheck, reason);
}

/// <summary>One check run on one device: the check and its settings as the user set them.</summary>
/// <param name="CheckId">The check.</param>
/// <param name="CheckName">Its name ("Object Analytics running").</param>
/// <param name="Settings">The settings of the check (the type's <see cref="IComplianceCheckType.Fields"/>).</param>
public sealed record ComplianceInvocation(Guid CheckId, string CheckName, JsonElement Settings)
{
    /// <summary>A setting as text (null when missing or null).</summary>
    public string? Get(string key) =>
        Settings.ValueKind == JsonValueKind.Object && Settings.TryGetProperty(key, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.GetRawText(),
            }
            : null;

    /// <summary>A check box setting.</summary>
    public bool GetBool(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>A number setting; null when missing or not a number.</summary>
    public int? GetInt(string key) =>
        int.TryParse(Get(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>The content of a <see cref="ActionFieldType.File"/> setting; null when none was chosen or it cannot be read.</summary>
    public ActionFile? GetFile(string key) =>
        Settings.ValueKind == JsonValueKind.Object && Settings.TryGetProperty(key, out var value) ? ActionFile.From(value) : null;
}

/// <summary>
/// Reads one device needs for several checks of a run, made once: the first check that asks for a key reads, the others
/// get the same answer (one application list for every "Application running" check). Kept for one run of one device.
/// </summary>
public interface IComplianceReads
{
    /// <summary>The value of <paramref name="key"/> ("oadm.acap:list"); <paramref name="read"/> runs at most once per run.</summary>
    Task<T> GetOrAddAsync<T>(string key, Func<CancellationToken, Task<T>> read, CancellationToken ct);
}

/// <summary>Server services for one check run on one device.</summary>
public interface IComplianceContext
{
    /// <summary>The device, as the server knows it now.</summary>
    IDeviceInfo Device { get; }

    /// <summary>
    /// Pre-authenticated for <see cref="Device"/> with the stored login; null for types with
    /// <see cref="IComplianceCheckType.UsesCachedDataOnly"/>. Read only: a check never changes a device.
    /// </summary>
    IVapixClient? Vapix { get; }

    /// <summary>Reads shared by every check of this run on this device.</summary>
    IComplianceReads Reads { get; }

    /// <summary>The server time of the run (certificate days left).</summary>
    DateTimeOffset Now { get; }

    /// <summary>The server log. Never log secrets.</summary>
    ILogger Logger { get; }

    /// <summary>The core plugin that contributed the type; null for the host's own types.</summary>
    ICorePlugin? Owner { get; }
}

/// <summary>Services for <see cref="IComplianceCheckType.ListChoicesAsync"/> (read only, while a check is edited).</summary>
public interface IComplianceQueryContext
{
    /// <summary>The server log.</summary>
    ILogger Logger { get; }

    /// <summary>The core plugin that contributed the type; null for the host's own types.</summary>
    ICorePlugin? Owner { get; }

    /// <summary>Every managed device.</summary>
    IDeviceRepository Devices { get; }

    /// <summary>The device the user chose to read the choices from (<see cref="ActionField.ChoicesFromDevice"/>); else null.</summary>
    IDeviceInfo? Device { get; }

    /// <summary>Pre-authenticated for <see cref="Device"/>; null without a device.</summary>
    IVapixClient? Vapix { get; }
}

/// <summary>
/// A kind of compliance check ("Application running", "Matches configuration file"). The user adds checks of a type on
/// the Compliance page: the type's settings, the devices, Problem or Warning, and how often it runs. The host runs it on
/// every chosen device, stores the results, shows them and publishes an event per check for the event rules. Contributed
/// by a core plugin through <see cref="IComplianceSource"/>; the host's own types use the same contract. Read only: a
/// check never changes a device.
/// </summary>
public interface IComplianceCheckType : IPlugin
{
    /// <summary>Group of the type select ("Devices", "Applications", "Configuration", "Storage"). Default "General".</summary>
    string Group => "General";

    /// <summary>One short sentence for the type select.</summary>
    string? Description => null;

    /// <summary>The settings form, drawn by the check dialog and checked on the server.</summary>
    IReadOnlyList<ActionField> Fields { get; }

    /// <summary>The severity of a new check of this type.</summary>
    ComplianceSeverity DefaultSeverity => ComplianceSeverity.Problem;

    /// <summary>
    /// True: the type decides from what the server already knows about the device (<see cref="IComplianceContext.Device"/>)
    /// and never contacts it. The host checks it whenever the device changes, without an interval.
    /// </summary>
    bool UsesCachedDataOnly => false;

    /// <summary>
    /// Checks the settings before a check is saved; returns errors per field key ("" for the whole check). Required
    /// fields, numbers and choices are already checked. Default: no further errors.
    /// </summary>
    IReadOnlyDictionary<string, string> Validate(JsonElement settings) => new Dictionary<string, string>();

    /// <summary>One line for the page: "Object Analytics running, at least 1.4". Default: the display name.</summary>
    string Describe(JsonElement settings) => DisplayName;

    /// <summary>The choices of a <see cref="ActionFieldType.DynamicChoice"/> field (read only). Default: none.</summary>
    Task<IReadOnlyList<ActionChoice>> ListChoicesAsync(IComplianceQueryContext ctx, string fieldKey, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ActionChoice>>([]);

    /// <summary>
    /// Checks one device. Return the result; an exception counts as <see cref="ComplianceOutcome.CouldNotCheck"/> with its
    /// message. The host cancels <paramref name="ct"/> after the device timeout (15 s for all checks of a run) or when the
    /// server stops. Devices the server must not contact (status not Ok) never reach a type that reads the device.
    /// </summary>
    Task<ComplianceResult> EvaluateAsync(IComplianceContext ctx, ComplianceInvocation invocation, CancellationToken ct);
}

/// <summary>A core plugin that contributes compliance check types implements this next to <c>ICorePlugin</c>.</summary>
public interface IComplianceSource
{
    /// <summary>The types this core plugin contributes; available while it is on.</summary>
    IReadOnlyList<IComplianceCheckType> ComplianceCheckTypes { get; }
}
