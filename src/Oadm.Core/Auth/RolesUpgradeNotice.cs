using Oadm.Core.Settings;

namespace Oadm.Core.Auth;

/// <summary>
/// The one-time notice for administrators after the upgrade to roles: before roles every operator could run every task
/// and open Web View; the built-in Operator role no longer has the risky ones (CLAUDE.md "Users, roles and sessions").
/// Stored as the setting <see cref="Setting"/> on the first start with roles of an installation that had users; the
/// client shows it to administrators until they dismiss it (its id is remembered per client).
/// </summary>
/// <param name="OperatorLost">The names of what the Operator role no longer has, as the matrix shows them.</param>
public sealed record RolesUpgradeNotice(string Id, DateTime CreatedUtc, IReadOnlyList<string> OperatorLost)
{
    public const string Setting = "Security.RolesIntroduced";

    /// <summary>Web View's permission (the relay acts with the stored device login).</summary>
    private const string WebViewOpen = "oadm.web-view:open";

    /// <summary>
    /// Stores the notice when <paramref name="sync"/> was the Operator role's first synchronization on an installation that
    /// already had users, and it withheld something operators could do before roles existed (tasks, Web View).
    /// </summary>
    public static async Task<RolesUpgradeNotice?> RecordAsync(ServerSettingsStore settings, CatalogSyncResult sync, bool hadUsers, PermissionCatalog catalog, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(time);
        if (!sync.OperatorFirstSync || !hadUsers)
        {
            return null;
        }

        var lost = sync.OperatorWithheld
            .Where(k => k.StartsWith("task:", StringComparison.Ordinal) || k == WebViewOpen)
            .Select(k => catalog.Find(k))
            .Where(e => e is not null)
            .Select(e => e!.Group is HostPermissions.TasksGroup ? e.Name : e.Group + ": " + e.Name)
            .ToList();
        if (lost.Count == 0)
        {
            return null;
        }

        var notice = new RolesUpgradeNotice(Guid.NewGuid().ToString("N"), time.GetUtcNow().UtcDateTime, lost);
        await settings.SetAsync(Setting, notice, ct).ConfigureAwait(false);
        return notice;
    }

    /// <summary>The stored notice, or null.</summary>
    public static Task<RolesUpgradeNotice?> ReadAsync(ServerSettingsStore settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.GetAsync<RolesUpgradeNotice>(Setting, ct);
    }
}
