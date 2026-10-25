using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Persistence;

namespace Oadm.Core.Auth;

/// <summary>An audit entry as listed to administrators.</summary>
public sealed record AuditRecord(long Id, DateTime TimeUtc, string UserName, string ClientAddress, string Action, string Target, string Detail);

/// <summary>Plain-language action names of the audit log (Logs page, Audit tab).</summary>
public static class AuditActions
{
    public const string LoginOk = "Logged in";
    public const string LoginFailed = "Login failed";
    public const string LoginLocked = "Login locked";
    public const string Logout = "Logged out";
    public const string FirstAdmin = "Created the first administrator";
    public const string UserAdded = "Added user";
    public const string UserChanged = "Changed user";
    public const string UserDeleted = "Deleted user";
    public const string SettingsChanged = "Changed server settings";
    public const string CredentialAdded = "Added credential";
    public const string CredentialRemoved = "Removed credential";
    public const string CredentialRevealed = "Showed credential password";
    public const string TaskRun = "Started task";
    public const string TasksDeletedAll = "Deleted all tasks";
    public const string DevicesRemoved = "Removed devices";
    public const string PluginCall = "Plugin action";
    public const string DeviceLogin = "Device login";
    public const string FirstPasswordSet = "First password set";
    public const string DeviceAddedAutomatically = "Device added automatically";
    public const string DeviceMoved = "Device moved";
    public const string WaitingAdded = "Kept devices waiting";
    public const string WaitingRemoved = "Removed waiting devices";
    public const string TagCreated = "Created tag";
    public const string TagRenamed = "Renamed tag";
    public const string TagRecolored = "Changed tag color";
    public const string TagDeleted = "Deleted tag";
    public const string DevicesTagged = "Tagged devices";
    public const string PluginTurnedOn = "Turned plugin on";
    public const string PluginTurnedOff = "Turned plugin off";
    public const string PluginLicenseEntered = "Entered plugin license";
    public const string PluginLicenseRemoved = "Removed plugin license";
    public const string ProLicenseImported = "Imported license";
    public const string ProLicenseRemoved = "Removed license";
    public const string ProTrialStarted = "Started trial";
    public const string SupportReportCreated = "Created support report";
    public const string ComplianceTurnedOn = "Turned compliance on";
    public const string ComplianceTurnedOff = "Turned compliance off";
    public const string ComplianceCheckCreated = "Created compliance check";
    public const string ComplianceCheckChanged = "Changed compliance check";
    public const string ComplianceCheckDeleted = "Deleted compliance check";
    public const string ComplianceCheckTurnedOn = "Turned compliance check on";
    public const string ComplianceCheckTurnedOff = "Turned compliance check off";
    public const string RuleCreated = "Created event rule";
    public const string RuleChanged = "Changed event rule";
    public const string RuleDeleted = "Deleted event rule";
    public const string EventLogCleared = "Cleared event log";
    public const string EventActionTested = "Tested event rule action";
    public const string RuleTurnedOn = "Turned event rule on";
    public const string RuleTurnedOff = "Turned event rule off";
    public const string WebInterfaceOpened = "Opened web interface";
    public const string RoleCreated = "Created role";
    public const string RoleChanged = "Changed role";
    public const string RoleDeleted = "Deleted role";
    public const string OpticsChanged = "Changed zoom or focus";
    public const string RtspPortChanged = "Changed RTSP port in OADM";
    public const string BackupCreated = "Created backup";
    public const string BackupDownloaded = "Downloaded backup";
    public const string BackupDeleted = "Deleted backup";
    public const string BackupScheduleChanged = "Changed automatic backups";
    public const string RestoreStarted = "Started restore";
    public const string RestoreApplied = "Restored backup";
    public const string DeviceGroupCreated = "Created device group";
    public const string DeviceGroupChanged = "Changed device group";
    public const string DeviceGroupDeleted = "Deleted device group";
}

/// <summary>
/// The audit log (table AuditEntries): who did what from where. Written for logins, user and settings changes, the
/// credential list, task runs, device removal and the plugin actions that change server configuration or devices
/// (<c>ICorePlugin.IsAudited</c>). Retention: <see cref="RetentionDays"/> days and the newest <see cref="MaxEntries"/>
/// entries (<see cref="ApplyRetentionAsync"/>, hourly). Never holds secrets.
/// </summary>
public sealed partial class AuditLog(IDbContextFactory<OadmDbContext> dbFactory, TimeProvider time, ILogger<AuditLog>? logger = null)
{
    public const int RetentionDays = 365;
    public const int MaxEntries = 200_000;
    public const int MaxTextLength = 500;

    /// <summary>Default and largest page of <see cref="ListAsync"/>.</summary>
    public const int DefaultListLimit = 10_000;
    public const int MaxListLimit = 50_000;

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Writes an entry for the current caller (<see cref="CallerContext.Current"/>; "system" outside calls).</summary>
    public Task WriteAsync(string action, string target, string? detail = null, CancellationToken ct = default)
    {
        var caller = CallerContext.Current;
        return WriteAsync(caller?.UserName ?? "system", caller?.ClientAddress ?? string.Empty, action, target, detail, ct);
    }

    /// <summary>Writes an entry for an explicit user (login attempts). Failures are logged, never thrown.</summary>
    public async Task WriteAsync(string userName, string clientAddress, string action, string target, string? detail, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.AuditEntries.Add(new AuditEntryEntity
            {
                TimeUtc = time.GetUtcNow().UtcDateTime,
                UserName = Cut(userName),
                ClientAddress = Cut(clientAddress),
                Action = Cut(action),
                Target = Cut(target),
                Detail = Cut(detail),
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            LogWritten(userName, action, target);
        }
#pragma warning disable CA1031 // The audit log must never fail the action it records.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogWriteFailed(ex, action);
        }
    }

    /// <summary>Newest first; <paramref name="limit"/> 0 = <see cref="DefaultListLimit"/>.</summary>
    public async Task<(IReadOnlyList<AuditRecord> Entries, int TotalCount)> ListAsync(int limit, int offset, CancellationToken ct)
    {
        limit = limit <= 0 ? DefaultListLimit : Math.Min(limit, MaxListLimit);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var total = await db.AuditEntries.CountAsync(ct).ConfigureAwait(false);
        var rows = await db.AuditEntries.AsNoTracking()
            .OrderByDescending(e => e.Id)
            .Skip(Math.Max(0, offset))
            .Take(limit)
            .Select(e => new AuditRecord(e.Id, e.TimeUtc, e.UserName, e.ClientAddress, e.Action, e.Target, e.Detail))
            .ToListAsync(ct).ConfigureAwait(false);
        return (rows, total);
    }

    /// <summary>Deletes entries older than <see cref="RetentionDays"/> and beyond the newest <see cref="MaxEntries"/>. Returns the number deleted.</summary>
    public async Task<int> ApplyRetentionAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-RetentionDays);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var deleted = await db.AuditEntries.Where(e => e.TimeUtc < cutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var keepFrom = await db.AuditEntries.OrderByDescending(e => e.Id).Skip(MaxEntries - 1).Select(e => (long?)e.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (keepFrom is { } id)
        {
            deleted += await db.AuditEntries.Where(e => e.Id < id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        return deleted;
    }

    private static string Cut(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length <= MaxTextLength ? value : value[..(MaxTextLength - 1)] + "…";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit: {UserName} {Action} {Target}")]
    private partial void LogWritten(string userName, string action, string target);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit entry {Action} could not be written")]
    private partial void LogWriteFailed(Exception ex, string action);
}
