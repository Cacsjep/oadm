using System.Collections.Frozen;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;

namespace Oadm.Core.Auth;

/// <summary>What a role may do: every permission (the Administrator role) or a set of keys; <see cref="Has"/> is O(1).</summary>
public sealed class PermissionSet
{
    private readonly FrozenSet<string> _keys;

    private PermissionSet(bool all, IEnumerable<string> keys)
    {
        IsAll = all;
        _keys = keys.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Every permission, including ones that appear later.</summary>
    public static PermissionSet All { get; } = new(true, []);

    /// <summary>Nothing beyond a login.</summary>
    public static PermissionSet None { get; } = new(false, []);

    public bool IsAll { get; }

    /// <summary>The granted keys (empty for <see cref="All"/>).</summary>
    public IReadOnlyCollection<string> Keys => _keys;

    public static PermissionSet Of(IEnumerable<string> keys) => new(false, keys);

    public bool Has(string key) => IsAll || _keys.Contains(key);

    /// <summary>True when the set has at least one of <paramref name="keys"/>.</summary>
    public bool HasAny(IEnumerable<string> keys) => IsAll || keys.Any(_keys.Contains);
}

/// <summary>A role as listed: its permissions and how many users have it.</summary>
public sealed record RoleRecord(Guid Id, string Name, string Description, RoleKind Kind, PermissionSet Permissions, int UserCount, long Version = 0, string StartPage = "");

/// <summary>A role to create (<see cref="Id"/> null) or to change in <see cref="RoleStore.SaveAsync"/>.</summary>
/// <param name="Permissions">The granted keys; keys the catalog does not know are ignored (a role keeps its own unknown keys).</param>
/// <param name="Version">The <see cref="RoleRecord.Version"/> the change was made on; a newer stored role refuses it (two administrators).</param>
/// <param name="StartPage">The page the client opens after a login (empty = Devices); null keeps it. The caller checks it is a page.</param>
public sealed record RoleChange(Guid? Id, string Name, string Description, IReadOnlyCollection<string> Permissions, long Version = 0, string? StartPage = null);

/// <summary>What a save changed, per role, for the audit log ("+Web View: Open devices -Restart").</summary>
/// <param name="StartPage">The new start page when it changed (empty = Devices), else null.</param>
public sealed record RoleDiff(string RoleName, bool Created, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, bool Renamed, string? StartPage = null);

/// <summary>
/// The roles (table Roles, Users page, Administrator only). The three built-in roles exist from the migration on:
/// Administrator has every permission and cannot change; Operator and Viewer are editable. At most
/// <see cref="MaxCustomRoles"/> custom roles. Every role's permissions live in memory, so a check is one set lookup; a
/// save replaces the snapshot and raises <see cref="Changed"/> (the server then ends streams a role no longer allows).
/// </summary>
public sealed partial class RoleStore(IDbContextFactory<OadmDbContext> dbFactory, TimeProvider time) : IDisposable
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 200;
    public const int MaxCustomRoles = 50;
    public const int MaxStartPageLength = 200;

    private readonly SemaphoreSlim _write = new(1, 1);
    private volatile Snapshot? _snapshot;
    private PermissionCatalog _catalog = PermissionCatalog.HostOnly;

    /// <summary>Raised after roles changed, with the ids of the changed roles.</summary>
    public event EventHandler<IReadOnlyCollection<Guid>>? Changed;

    /// <summary>The catalog the roles were last synchronized with.</summary>
    public PermissionCatalog Catalog => _catalog;

    /// <summary>The trimmed name, or an <see cref="AuthException"/> (InvalidArgument) with the reason.</summary>
    public static string ValidateName(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new AuthException(AuthError.InvalidArgument, "Enter a role name.");
        }

        if (text.Length > MaxNameLength || text.Any(char.IsControl))
        {
            throw new AuthException(AuthError.InvalidArgument, $"The role name must be 1-{MaxNameLength} characters without control characters.");
        }

        return text;
    }

    /// <summary>The permissions of a role; an unknown role has none.</summary>
    public async Task<PermissionSet> PermissionsOfAsync(Guid roleId, CancellationToken ct)
    {
        if (BuiltInRoles.IsAdministrator(roleId))
        {
            return PermissionSet.All;
        }

        var snapshot = _snapshot ?? await LoadAsync(ct).ConfigureAwait(false);
        return snapshot.Roles.TryGetValue(roleId, out var role) ? role.Permissions : PermissionSet.None;
    }

    /// <summary>The name of a role ("Operator"), or an empty string.</summary>
    public async Task<string> NameOfAsync(Guid roleId, CancellationToken ct)
    {
        var snapshot = _snapshot ?? await LoadAsync(ct).ConfigureAwait(false);
        return snapshot.Roles.TryGetValue(roleId, out var role) ? role.Name : string.Empty;
    }

    /// <summary>The page the client opens after a login for users of a role; empty = Devices (also for an unknown role).</summary>
    public async Task<string> StartPageOfAsync(Guid roleId, CancellationToken ct)
    {
        var snapshot = _snapshot ?? await LoadAsync(ct).ConfigureAwait(false);
        return snapshot.Roles.TryGetValue(roleId, out var role) ? role.StartPage : string.Empty;
    }

    public async Task<bool> ExistsAsync(Guid roleId, CancellationToken ct)
    {
        var snapshot = _snapshot ?? await LoadAsync(ct).ConfigureAwait(false);
        return snapshot.Roles.ContainsKey(roleId);
    }

    /// <summary>Every role in matrix order (Administrator, Operator, Viewer, then custom roles by creation) with its user count.</summary>
    public async Task<IReadOnlyList<RoleRecord>> ListAsync(CancellationToken ct)
    {
        var snapshot = _snapshot ?? await LoadAsync(ct).ConfigureAwait(false);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var counts = await db.Users.AsNoTracking().GroupBy(u => u.RoleId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
        return [.. snapshot.Ordered.Select(r => new RoleRecord(r.Id, r.Name, r.Description, r.Kind, r.Permissions, counts.GetValueOrDefault(r.Id), r.Version, r.StartPage))];
    }

    /// <summary>
    /// Takes the permissions <paramref name="catalog"/> knows: every key a role has not seen yet gets its default once
    /// (Operator and Viewer by the permission's default, custom roles nothing), then counts as seen. Returns the keys the
    /// Operator role was given or not given for the first time ("new" permissions), for the upgrade notice.
    /// </summary>
    public async Task<CatalogSyncResult> SyncCatalogAsync(PermissionCatalog catalog, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _catalog = catalog;
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var roles = await db.Roles.ToListAsync(ct).ConfigureAwait(false);
            var changed = new List<Guid>();
            var operatorFirstSync = false;
            var operatorWithheld = new List<string>();
            var now = time.GetUtcNow().UtcDateTime;
            foreach (var role in roles.Where(r => r.Kind != RoleKind.Administrator))
            {
                var known = ReadKeys(role.KnownJson);
                var granted = ReadKeys(role.PermissionsJson);
                var fresh = catalog.Entries.Where(e => !known.Contains(e.Key)).ToList();
                if (fresh.Count == 0)
                {
                    continue;
                }

                if (role.Kind == RoleKind.Operator && known.Count == 0)
                {
                    operatorFirstSync = true;
                }

                foreach (var entry in fresh)
                {
                    known.Add(entry.Key);
                    var give = role.Kind switch
                    {
                        RoleKind.Operator => entry.OperatorDefault,
                        RoleKind.Viewer => entry.ViewerDefault,
                        _ => false,
                    };
                    if (give)
                    {
                        granted.Add(entry.Key);
                    }
                    else if (role.Kind == RoleKind.Operator)
                    {
                        operatorWithheld.Add(entry.Key);
                    }
                }

                role.KnownJson = WriteKeys(known);
                role.PermissionsJson = WriteKeys(granted);
                role.ChangedUtc = now;
                changed.Add(role.Id);
            }

            if (changed.Count > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            _snapshot = Snapshot.From(roles);
            if (changed.Count > 0)
            {
                Oadm.Core.Auth.CallerContext.Raise(Changed, this, changed);
            }

            return new CatalogSyncResult(operatorFirstSync, operatorWithheld);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>
    /// Creates and changes roles in one transaction (the matrix's Save). The Administrator role keeps its name and
    /// permissions; only its start page can change. Returns what changed per role (empty when nothing did).
    /// </summary>
    public async Task<IReadOnlyList<RoleDiff>> SaveAsync(IReadOnlyList<RoleChange> changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var roles = await db.Roles.ToListAsync(ct).ConfigureAwait(false);
            var byId = roles.ToDictionary(r => r.Id);
            var diffs = new List<RoleDiff>();
            var changedIds = new List<Guid>();
            var now = time.GetUtcNow().UtcDateTime;
            var catalogKeys = _catalog.Entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

            foreach (var change in changes)
            {
                var startPage = change.StartPage?.Trim();
                if (startPage is { Length: > MaxStartPageLength })
                {
                    throw new AuthException(AuthError.InvalidArgument, "Choose a start page from the list.");
                }

                if (change.Id is { } adminId && BuiltInRoles.IsAdministrator(adminId))
                {
                    // The Administrator role: everything is fixed but the start page.
                    var admin = byId.GetValueOrDefault(adminId)
                        ?? throw new AuthException(AuthError.NotFound, "The role does not exist (any more).");
                    if ((change.Name?.Trim() is { Length: > 0 } adminName && adminName != admin.Name)
                        || (change.Description?.Trim() ?? string.Empty) != admin.Description
                        || change.Permissions is { Count: > 0 })
                    {
                        throw new AuthException(AuthError.Precondition, "The Administrator role always has every permission and cannot change.");
                    }

                    if (change.Version != 0 && change.Version != VersionOf(admin))
                    {
                        throw new AuthException(AuthError.Precondition,
                            $"Another administrator changed the role {admin.Name} meanwhile. Click Discard and make your changes again.");
                    }

                    if (startPage is null || startPage == admin.StartPage)
                    {
                        continue;
                    }

                    admin.StartPage = startPage;
                    admin.ChangedUtc = now;
                    diffs.Add(new RoleDiff(admin.Name, false, [], [], false, startPage));
                    changedIds.Add(admin.Id);
                    continue;
                }

                var name = ValidateName(change.Name);
                var description = (change.Description ?? string.Empty).Trim();
                if (description.Length > MaxDescriptionLength)
                {
                    throw new AuthException(AuthError.InvalidArgument, $"The description must have at most {MaxDescriptionLength} characters.");
                }

                var wanted = (change.Permissions ?? []).Where(catalogKeys.Contains).ToHashSet(StringComparer.Ordinal);
                RoleEntity role;
                bool created = change.Id is null;
                HashSet<string> before;
                if (created)
                {
                    if (roles.Count(r => r.Kind == RoleKind.Custom) >= MaxCustomRoles)
                    {
                        throw new AuthException(AuthError.InvalidArgument, $"OADM keeps at most {MaxCustomRoles} roles of your own. Delete one first.");
                    }

                    role = new RoleEntity
                    {
                        Id = Guid.NewGuid(),
                        Kind = RoleKind.Custom,
                        CreatedUtc = now,
                        KnownJson = WriteKeys(catalogKeys),
                    };
                    roles.Add(role);
                    byId[role.Id] = role;
                    db.Roles.Add(role);
                    before = [];
                }
                else
                {
                    role = byId.GetValueOrDefault(change.Id!.Value)
                        ?? throw new AuthException(AuthError.NotFound, "The role does not exist (any more).");
                    if (role.Kind == RoleKind.Administrator)
                    {
                        throw new AuthException(AuthError.Precondition, "The Administrator role always has every permission and cannot change.");
                    }

                    if (change.Version != 0 && change.Version != VersionOf(role))
                    {
                        throw new AuthException(AuthError.Precondition,
                            $"Another administrator changed the role {role.Name} meanwhile. Click Discard and make your changes again.");
                    }

                    before = ReadKeys(role.PermissionsJson);
                }

                var normalized = name.ToUpperInvariant();
                if (roles.Any(r => r.Id != role.Id && r.NormalizedName == normalized))
                {
                    throw new AuthException(AuthError.InvalidArgument, $"A role named {name} exists already.");
                }

                // Keys the catalog does not know (a plugin that is not loaded) stay as they were.
                var keep = before.Where(k => !catalogKeys.Contains(k));
                var after = wanted.Concat(keep).ToHashSet(StringComparer.Ordinal);
                var added = after.Except(before).Order(StringComparer.Ordinal).ToList();
                var removed = before.Except(after).Order(StringComparer.Ordinal).ToList();
                var renamed = !created && (role.Name != name || role.Description != description);
                var newStart = startPage is not null && startPage != role.StartPage ? startPage : null;
                if (!created && added.Count == 0 && removed.Count == 0 && !renamed && newStart is null)
                {
                    continue;
                }

                role.Name = name;
                role.NormalizedName = normalized;
                role.Description = description;
                role.PermissionsJson = WriteKeys(after);
                role.KnownJson = WriteKeys(ReadKeys(role.KnownJson).Concat(catalogKeys));
                role.StartPage = newStart ?? role.StartPage;
                role.ChangedUtc = now;
                diffs.Add(new RoleDiff(name, created, added, removed, renamed, created && role.StartPage.Length == 0 ? null : newStart));
                changedIds.Add(role.Id);
            }

            if (changedIds.Count == 0)
            {
                return [];
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _snapshot = Snapshot.From(roles);
            Oadm.Core.Auth.CallerContext.Raise(Changed, this, changedIds);
            return diffs;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Deletes a custom role that no user has. Returns its name.</summary>
    public async Task<string> DeleteAsync(Guid id, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false)
                ?? throw new AuthException(AuthError.NotFound, "The role does not exist (any more).");
            if (role.Kind != RoleKind.Custom)
            {
                throw new AuthException(AuthError.Precondition, $"{role.Name} is built in and cannot be deleted.");
            }

            var users = await db.Users.CountAsync(u => u.RoleId == id, ct).ConfigureAwait(false);
            if (users > 0)
            {
                throw new AuthException(AuthError.Precondition, users == 1
                    ? $"1 user has the role {role.Name}. Give that user another role first."
                    : $"{users} users have the role {role.Name}. Give them another role first.");
            }

            db.Roles.Remove(role);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _snapshot = Snapshot.From(await db.Roles.AsNoTracking().ToListAsync(ct).ConfigureAwait(false));
            Oadm.Core.Auth.CallerContext.Raise(Changed, this, [id]);
            return role.Name;
        }
        finally
        {
            _write.Release();
        }
    }

    public void Dispose() => _write.Dispose();

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var roles = await db.Roles.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var snapshot = Snapshot.From(roles);
        _snapshot ??= snapshot;
        return _snapshot;
    }

    private static HashSet<string> ReadKeys(string? json)
    {
        try
        {
            return string.IsNullOrEmpty(json) ? new(StringComparer.Ordinal) : new(JsonSerializer.Deserialize<string[]>(json) ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static string WriteKeys(IEnumerable<string> keys) => JsonSerializer.Serialize(keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    private sealed record CachedRole(Guid Id, string Name, string Description, RoleKind Kind, DateTime CreatedUtc, PermissionSet Permissions, long Version, string StartPage);

    private sealed class Snapshot
    {
        private Snapshot(Dictionary<Guid, CachedRole> roles)
        {
            Roles = roles;
            Ordered = [.. roles.Values.OrderBy(r => r.Kind == RoleKind.Custom ? 1 : 0).ThenBy(r => r.Kind).ThenBy(r => r.CreatedUtc).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
        }

        public Dictionary<Guid, CachedRole> Roles { get; }

        public IReadOnlyList<CachedRole> Ordered { get; }

        public static Snapshot From(IEnumerable<RoleEntity> roles) => new(roles.ToDictionary(r => r.Id, r => new CachedRole(
            r.Id, r.Name, r.Description, r.Kind, r.CreatedUtc,
            r.Kind == RoleKind.Administrator ? PermissionSet.All : PermissionSet.Of(ReadKeys(r.PermissionsJson)), VersionOf(r), r.StartPage ?? string.Empty)));
    }
}

public sealed partial class RoleStore
{
    /// <summary>The version of a stored role: the time of its last change.</summary>
    internal static long VersionOf(RoleEntity role) => role.ChangedUtc.Ticks;
}

/// <summary>What <see cref="RoleStore.SyncCatalogAsync"/> did to the Operator role.</summary>
/// <param name="OperatorFirstSync">The Operator role had seen no permission before: the first start after the upgrade (or a new installation).</param>
/// <param name="OperatorWithheld">New permissions the Operator role did not get (their default is off).</param>
public sealed record CatalogSyncResult(bool OperatorFirstSync, IReadOnlyList<string> OperatorWithheld);
