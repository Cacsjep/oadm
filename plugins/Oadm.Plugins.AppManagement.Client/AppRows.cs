using System.Globalization;

using System.Text.RegularExpressions;

using Oadm.Plugins.Acap;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Client;

namespace Oadm.Plugins.AppManagement.Client;

/// <summary>
/// One row of the grid: a device and one of its applications, or the device's one row without any ("No applications", a
/// read error, Not checked). Made once per device read; the grid only filters them.
/// </summary>
public sealed partial class AppRow
{
    public const string NoApplications = "No applications";

    private AppRow(DeviceApps device, InstalledApplication? app, DateOnly today)
    {
        Device = device;
        App = app;
        DeviceId = device.DeviceId;
        Mac = device.Mac ?? string.Empty;
        Address = device.Address;
        AddressSortKey = AddressSortKeys.Of(device.Address);
        Model = device.Model ?? string.Empty;
        Firmware = device.Firmware ?? string.Empty;
        // A task of the page on some applications shows only in their rows; one on the whole device (Install) in all.
        Activity = app is null || device.ActivityApplications.Count == 0 || device.ActivityApplications.Contains(app.Name, StringComparer.Ordinal)
            ? device.Activity
            : null;
        Error = device.Error;

        // The failed or warning task of the page on this application (or the device's own, e.g. Install), failures first.
        var result = device.TaskResults
            .Where(r => app is null || r.Application is null || string.Equals(r.Application, app.Name, StringComparison.Ordinal))
            .OrderByDescending(r => r.Failed)
            .FirstOrDefault();
        TaskMessage = result?.Message;
        TaskFailed = result?.Failed == true;
        if (app is not null)
        {
            AppKey = app.Name;
            AppName = app.DisplayName;
            Version = app.Version ?? string.Empty;
            Vendor = app.Vendor ?? string.Empty;
            IsAxisApp = IsAxis(app);
            IsRunning = app.IsRunning;
            IsMissing = AppLicenses.IsMissing(app);
            IsInvalid = AppLicenses.IsInvalid(app);
            ExpiresSoon = AppLicenses.ExpiresSoon(app, today);
            // A valid license with an end shows only its date; the others the device's words ("Valid", "Not required", "Missing").
            LicenseText = AppLicenses.ExpiresOn(app) is { } end ? end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : app.LicenseText;
            AppStatus = string.IsNullOrWhiteSpace(app.Status) ? "Unknown" : app.Status;
        }
        else
        {
            AppName = Error is null ? NoApplications : string.Empty;
        }

        AppHaystack = string.Join('\n', Address, AppName, AppKey, Vendor, Version);
        Haystack = string.Join('\n', Address, Mac, Model, Firmware, DeviceTexts.Soc(device.Soc), device.Architecture, AppName, AppKey, Vendor, Version);
    }

    /// <summary>The device's rows: one per application (sorted by name), and the row without one.</summary>
    public static (AppRow[] Apps, AppRow Empty) For(DeviceApps device, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(device);
        var apps = device.Error is null
            ? device.Applications.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).Select(a => new AppRow(device, a, today)).ToArray()
            : [];
        return (apps, new AppRow(device, null, today));
    }

    public DeviceApps Device { get; }

    public InstalledApplication? App { get; }

    public Guid DeviceId { get; }

    /// <summary>The MAC address (serial number), like the Devices grid.</summary>
    public string Mac { get; }

    public string Address { get; }

    /// <summary>The group of the lower grid while several devices are selected: one per device, the header shows the address.</summary>
    public AppDeviceGroup Group => new(DeviceId, Address);

    public string AddressSortKey { get; }

    public string Model { get; }

    public string Firmware { get; }

    /// <summary>The package name (control.cgi's "package"); null for a row without an application.</summary>
    public string? AppKey { get; }

    public string AppName { get; }

    public string Version { get; } = string.Empty;

    public string Vendor { get; } = string.Empty;

    /// <summary>Hidden by Hide AXIS apps: preinstalled (Bundled=Yes) or "AXIS" in the name.</summary>
    public bool IsAxisApp { get; }

    /// <summary>Preinstalled with AXIS OS, or an application named "AXIS ..." (the word, any case).</summary>
    public static bool IsAxis(InstalledApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Bundled || AxisWord().IsMatch(app.DisplayName) || AxisWord().IsMatch(app.Name);
    }

    [GeneratedRegex(@"\bAXIS\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AxisWord();

    public bool IsRunning { get; }

    public bool IsMissing { get; }

    public bool IsInvalid { get; }

    public bool ExpiresSoon { get; }

    public string LicenseText { get; } = string.Empty;

    public string? Error { get; }

    public string? Activity { get; }

    /// <summary>The message of a page task that failed or ended with warnings on this row's application (until the next read the user starts).</summary>
    public string? TaskMessage { get; }

    /// <summary>The task of <see cref="TaskMessage"/> failed; false: it ended with warnings.</summary>
    public bool TaskFailed { get; }

    private string AppStatus { get; } = string.Empty;

    /// <summary>What the search looks in: device, address, model, application, vendor, version.</summary>
    public string Haystack { get; }

    /// <summary>What the lower grid's search looks in: address, application, vendor, version.</summary>
    public string AppHaystack { get; }

    /// <summary>
    /// The status cell: a task of the page, the read error, Running / Stopped / Idle; after a failed or warning task its
    /// message follows the fresh state ("Stopped. The application did not start.").
    /// </summary>
    public string StatusText => Activity ?? Error
        ?? (TaskMessage is null ? AppStatus : App is null ? TaskMessage : AppStatus + ". " + TaskMessage);

    private bool HasTaskProblem => Activity is null && Error is null && TaskMessage is not null;

    public bool IsStatusOk => Activity is null && Error is null && TaskMessage is null && IsRunning;

    public bool IsStatusError => Activity is null && ((Error is not null && !Device.NotChecked) || (HasTaskProblem && TaskFailed));

    public bool IsStatusWarning => Activity is null && (Device.NotChecked || (HasTaskProblem && !TaskFailed));

    public bool IsStatusAccent => Activity is not null;

    public bool HasLicense => App is not null;

    public bool IsLicenseError => IsMissing || IsInvalid;

    public bool IsLicenseWarning => ExpiresSoon && !IsLicenseError;

    /// <summary>A license problem shows as a chip; a valid one or "Not required" as plain text.</summary>
    public bool HasLicenseProblem => IsLicenseError || IsLicenseWarning;

    public bool ShowPlainLicense => App is not null && !HasLicenseProblem;

    /// <summary>Sorts the status cell: problems first.</summary>
    public string StatusSortKey => (Error is not null || TaskMessage is not null ? "0" : IsRunning ? "2" : "1") + StatusText;

    public string LicenseSortKey => (IsLicenseError ? "0" : IsLicenseWarning ? "1" : "2") + LicenseText;
}

/// <summary>One row of the device grid: the device columns and a summary of its shown applications (its rows).</summary>
public sealed class AppDeviceRow
{
    public AppDeviceRow(DeviceApps device, IReadOnlyList<AppRow> rows, string addressSortKey)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(rows);
        Device = device;
        Rows = rows;
        Address = device.Address;
        AddressSortKey = addressSortKey;
        Model = device.Model ?? string.Empty;
        Mac = device.Mac ?? string.Empty;
        Firmware = device.Firmware ?? string.Empty;
        Soc = DeviceTexts.Soc(device.Soc) ?? string.Empty;
        Architecture = device.Architecture ?? string.Empty;

        var notRunning = rows.Count(r => !r.IsRunning);
        var licenseProblems = rows.Count(r => r.IsLicenseError);
        var expiring = rows.Count(r => r.IsLicenseWarning);
        var taskProblems = rows.Count(r => r.TaskMessage is not null);
        if (device.Activity is { } activity)
        {
            SummaryText = activity;
            IsAccent = true;
        }
        else if (device.Error is { } error)
        {
            SummaryText = error;
            IsError = !device.NotChecked;
            IsWarning = device.NotChecked;
        }
        else
        {
            var parts = new List<string>();
            if (taskProblems > 0)
            {
                parts.Add(Count(taskProblems, "task problem", "task problems"));
            }

            if (licenseProblems > 0)
            {
                parts.Add(Count(licenseProblems, "license problem", "license problems"));
            }

            if (expiring > 0)
            {
                parts.Add(Count(expiring, "license ends soon", "licenses end soon"));
            }

            if (notRunning > 0)
            {
                parts.Add(Count(notRunning, "not running", "not running"));
            }

            SummaryText = rows.Count == 0 ? AppRow.NoApplications : parts.Count == 0 ? "All running" : string.Join(", ", parts);
            IsError = taskProblems > 0 && rows.Any(r => r.TaskFailed) || licenseProblems > 0;
            IsWarning = !IsError && (expiring > 0 || taskProblems > 0);
            IsOk = rows.Count > 0 && parts.Count == 0;
        }
    }

    public DeviceApps Device { get; }

    public Guid DeviceId => Device.DeviceId;

    /// <summary>The device's shown application rows (after the filters).</summary>
    public IReadOnlyList<AppRow> Rows { get; }

    public string Address { get; }

    public string AddressSortKey { get; }

    public string Model { get; }

    public string Mac { get; }

    public string Firmware { get; }

    public string Soc { get; }

    public string Architecture { get; }

    public int AppCount => Rows.Count;

    public string SummaryText { get; }

    public bool IsOk { get; }

    public bool IsWarning { get; }

    public bool IsError { get; }

    public bool IsAccent { get; }

    /// <summary>Problems first.</summary>
    public string SummarySortKey => (IsError ? "0" : IsWarning ? "1" : IsOk ? "3" : "2") + SummaryText;

    private static string Count(int n, string one, string many) =>
        string.Create(CultureInfo.CurrentCulture, $"{n:N0} {(n == 1 ? one : many)}");
}

/// <summary>The group key of a device in the lower grid: equal per device (two sites may use the same address), shown as its address.</summary>
public sealed record AppDeviceGroup(Guid DeviceId, string Address)
{
    public bool Equals(AppDeviceGroup? other) => other is not null && other.DeviceId == DeviceId;

    public override int GetHashCode() => DeviceId.GetHashCode();

    public override string ToString() => Address;
}
