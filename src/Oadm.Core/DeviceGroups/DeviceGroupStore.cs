using Microsoft.EntityFrameworkCore;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;

namespace Oadm.Core.DeviceGroups;

/// <summary>
/// Something a device group can own (event rules, compliance checks, plugin objects): deleting the group turns it off in the
/// same transaction (fail closed) until an administrator gives it another owner (docs/spec/device-scope.md, Model).
/// Implementations are registered in the container; the store calls every one of them.
/// </summary>
public interface IDeviceGroupOwnedObjects
{
    /// <summary>
    /// Clears the owner of what <paramref name="groupId"/> owns and turns it off, inside the delete transaction
    /// (<paramref name="db"/>; never save or commit). Returns what it turned off for the audit entry ("3 event rules"), null
    /// for nothing. Engines that keep the objects in memory reload them on <see cref="DeviceGroupStore.Deleted"/>.
    /// </summary>
    Task<string?> DisableOwnedAsync(OadmDbContext db, Guid groupId, CancellationToken ct);
}

/// <summary>A saved group: the stored group, whether it is new, and what changed for the audit entry.</summary>
public sealed record DeviceGroupSaveResult(DeviceGroup Group, bool Created, string Changes);

/// <summary>A deleted group: its name, the users who have no devices now, and what it owned that is off now.</summary>
public sealed record DeviceGroupDeleteResult(Guid Id, string Name, IReadOnlyList<string> UserNames, IReadOnlyList<string> DisabledObjects);

/// <summary>
/// The device groups (table DeviceGroups, Administrator only on the API). Names are unique case-insensitively. Every save
/// raises the group's <see cref="DeviceGroup.Revision"/>; a save made on an older revision is refused (two administrators).
/// Deleting a group sets its users to no devices and turns off what it owned, in one transaction. The groups are kept in
/// memory (at most <see cref="DeviceGroup.MaxGroups"/>), so the auth interceptor and the views read them without a query.
/// </summary>
public sealed class DeviceGroupStore(IDbContextFactory<OadmDbContext> dbFactory, UserStore users, TimeProvider time, IEnumerable<IDeviceGroupOwnedObjects>? owned = null) : IDisposable
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly IReadOnlyList<IDeviceGroupOwnedObjects> _owned = owned?.ToList() ?? [];
    private volatile IReadOnlyDictionary<Guid, DeviceGroup>? _groups;
    private long _version;

    /// <summary>
    /// Raised (without a caller) with the group's id after a group was created or its rules changed: the devices of its users
    /// may have changed (their streams end, their plugin jobs are forgotten). A new name or description alone raises only
    /// <see cref="Updated"/>.
    /// </summary>
    public event EventHandler<Guid>? Changed;

    /// <summary>Raised (without a caller) with the group's id after every save that changed anything (name, description or rules).</summary>
    public event EventHandler<Guid>? Updated;

    /// <summary>Raised (without a caller) after a group was deleted (its users have no devices, its objects are off).</summary>
    public event EventHandler<DeviceGroupDeleteResult>? Deleted;

    /// <summary>Moves with every write (any group).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>The groups by id once loaded (<see cref="LoadAsync"/>); null before.</summary>
    public IReadOnlyDictionary<Guid, DeviceGroup>? Loaded => _groups;

    /// <summary>Reads the groups once; later writes keep the memory copy current.</summary>
    public async ValueTask<IReadOnlyDictionary<Guid, DeviceGroup>> LoadAsync(CancellationToken ct)
    {
        if (_groups is { } groups)
        {
            return groups;
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_groups is { } again)
            {
                return again;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            _groups = await ReadAllAsync(db, ct).ConfigureAwait(false);
            return _groups;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Every group, sorted by name (copies).</summary>
    public async Task<IReadOnlyList<DeviceGroup>> ListAsync(CancellationToken ct)
    {
        var groups = await LoadAsync(ct).ConfigureAwait(false);
        return [.. groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Clone())];
    }

    /// <summary>One group (a copy), or null.</summary>
    public async Task<DeviceGroup?> GetAsync(Guid id, CancellationToken ct)
    {
        var groups = await LoadAsync(ct).ConfigureAwait(false);
        return groups.TryGetValue(id, out var group) ? group.Clone() : null;
    }

    /// <summary>The group's revision once loaded; 0 for an unknown group (and before <see cref="LoadAsync"/>).</summary>
    public long RevisionOf(Guid? groupId) =>
        groupId is { } id && _groups is { } groups && groups.TryGetValue(id, out var group) ? group.Revision : 0;

    /// <summary>The names of the groups whose rules name the tag (the note before a tag is deleted). Needs <see cref="LoadAsync"/>.</summary>
    public IReadOnlyList<string> GroupsUsingTag(Guid tagId) =>
        [.. (_groups?.Values ?? []).Where(g => g.TagIds.Contains(tagId)).Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The names of the groups whose rules name the site (the note before a site is removed). Needs <see cref="LoadAsync"/>.</summary>
    public IReadOnlyList<string> GroupsUsingSite(Guid siteId) =>
        [.. (_groups?.Values ?? []).Where(g => g.SiteIds.Contains(siteId)).Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Every tag id any group uses: users with a group never add or remove these tags (that would move devices between
    /// groups; the guard is in <c>DeviceTagStore</c>). Needs <see cref="LoadAsync"/>.
    /// </summary>
    public IReadOnlySet<Guid> TagIdsInUse() => (_groups?.Values ?? []).SelectMany(g => g.TagIds).ToHashSet();

    /// <summary>The names of the users of a group (sorted).</summary>
    public async Task<IReadOnlyList<string>> UserNamesAsync(Guid groupId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var names = await db.Users.AsNoTracking().Where(u => u.DeviceGroupId == groupId).Select(u => u.UserName).ToListAsync(ct).ConfigureAwait(false);
        return [.. names.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The user names of every group in one query (the Users page card).</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> UserNamesByGroupAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Users.AsNoTracking().Where(u => u.DeviceGroupId != null)
            .Select(u => new { u.DeviceGroupId, u.UserName }).ToListAsync(ct).ConfigureAwait(false);
        return rows.GroupBy(r => r.DeviceGroupId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => r.UserName).Order(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// Creates (<paramref name="id"/> null) or changes a group. <paramref name="expectedRevision"/> is the revision the
    /// change was made on (0 = no check). Throws <see cref="DeviceGroupException"/> with the reason.
    /// </summary>
    public async Task<DeviceGroupSaveResult> SaveAsync(Guid? id, string? name, string? description, DeviceGroupRules rules, long expectedRevision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var cleanName = ValidateName(name);
        var cleanDescription = (description ?? string.Empty).Trim();
        if (cleanDescription.Length > DeviceGroup.MaxDescriptionLength || cleanDescription.Any(char.IsControl))
        {
            throw new DeviceGroupException(DeviceGroupError.InvalidArgument, DeviceGroupTexts.DescriptionTooLong);
        }

        var normalizedRules = rules.Normalize();
        DeviceGroupSaveResult result;
        var rulesChanged = false;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var normalizedName = cleanName.ToUpperInvariant();
            if (await db.DeviceGroups.AnyAsync(g => g.NormalizedName == normalizedName && (id == null || g.Id != id), ct).ConfigureAwait(false))
            {
                throw new DeviceGroupException(DeviceGroupError.AlreadyExists, DeviceGroupTexts.NameInUse(cleanName));
            }

            var now = time.GetUtcNow().UtcDateTime;
            DeviceGroup group;
            var created = id is null;
            var changes = new List<string>();
            if (created)
            {
                if (await db.DeviceGroups.CountAsync(ct).ConfigureAwait(false) >= DeviceGroup.MaxGroups)
                {
                    throw new DeviceGroupException(DeviceGroupError.ResourceExhausted, DeviceGroupTexts.TooManyGroups);
                }

                group = new DeviceGroup { Id = Guid.NewGuid(), CreatedUtc = now, Revision = 1 };
                db.DeviceGroups.Add(group);
                changes.Add(Describe(normalizedRules));
                rulesChanged = true;
            }
            else
            {
                group = await db.DeviceGroups.FirstOrDefaultAsync(g => g.Id == id, ct).ConfigureAwait(false)
                    ?? throw new DeviceGroupException(DeviceGroupError.NotFound, DeviceGroupTexts.UnknownGroup);
                if (expectedRevision > 0 && expectedRevision != group.Revision)
                {
                    throw new DeviceGroupException(DeviceGroupError.Precondition, DeviceGroupTexts.ChangedMeanwhile);
                }

                if (!string.Equals(group.Name, cleanName, StringComparison.Ordinal))
                {
                    changes.Add("name " + cleanName);
                }

                if (!string.Equals(group.Description, cleanDescription, StringComparison.Ordinal))
                {
                    changes.Add("description");
                }

                if (!SameRules(group.Rules, normalizedRules))
                {
                    changes.Add("rules: " + Describe(normalizedRules));
                    rulesChanged = true;
                }

                if (changes.Count == 0)
                {
                    return new DeviceGroupSaveResult(group.Clone(), false, string.Empty);
                }

                group.Revision++;
            }

            group.Name = cleanName;
            group.NormalizedName = normalizedName;
            group.Description = cleanDescription;
            group.ThisServer = normalizedRules.ThisServer;
            group.SiteIds = [.. normalizedRules.SiteIds];
            group.TagIds = [.. normalizedRules.TagIds];
            group.Serials = [.. normalizedRules.Serials];
            group.ExceptSerials = [.. normalizedRules.ExceptSerials];
            group.ChangedUtc = now;
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new DeviceGroupException(DeviceGroupError.Precondition, DeviceGroupTexts.ChangedMeanwhile);
            }

            // Stored: from here on the call's cancel must not leave the memory copy, the views and the events behind.
            AfterSave?.Invoke();
            _groups = await ReadAllAsync(db, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref _version);
            result = new DeviceGroupSaveResult(group.Clone(), created, string.Join(", ", changes));
        }
        finally
        {
            _write.Release();
        }

        if (rulesChanged)
        {
            CallerContext.Raise(Changed, this, result.Group.Id);
        }

        CallerContext.Raise(Updated, this, result.Group.Id);
        return result;
    }

    /// <summary>Tests: runs right after a save was stored (a cancel at that moment).</summary>
    internal Action? AfterSave { get; set; }

    /// <summary>
    /// Deletes a group: in one transaction its users get no devices and every <see cref="IDeviceGroupOwnedObjects"/> turns off
    /// what it owned. Their sessions take it over and their streams end (<see cref="UserStore.UserDeviceAccessChanged"/>).
    /// </summary>
    public async Task<DeviceGroupDeleteResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        DeviceGroupDeleteResult result;
        List<Guid> userIds;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var group = await db.DeviceGroups.FirstOrDefaultAsync(g => g.Id == id, ct).ConfigureAwait(false)
                ?? throw new DeviceGroupException(DeviceGroupError.NotFound, DeviceGroupTexts.UnknownGroup);
            var affected = await db.Users.Where(u => u.DeviceGroupId == id).Select(u => new { u.Id, u.UserName }).ToListAsync(ct).ConfigureAwait(false);
            userIds = [.. affected.Select(u => u.Id)];

            // Fail closed: the users keep no flag and no group, so they reach no device until an administrator chooses again.
            await db.Users.Where(u => u.DeviceGroupId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DeviceGroupId, (Guid?)null).SetProperty(u => u.AllDevices, false), ct).ConfigureAwait(false);
            var disabled = new List<string>();
            foreach (var owner in _owned)
            {
                if (await owner.DisableOwnedAsync(db, id, ct).ConfigureAwait(false) is { Length: > 0 } text)
                {
                    disabled.Add(text);
                }
            }

            db.DeviceGroups.Remove(group);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            _groups = await ReadAllAsync(db, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref _version);
            result = new DeviceGroupDeleteResult(id, group.Name, [.. affected.Select(u => u.UserName).Order(StringComparer.OrdinalIgnoreCase)], disabled);
        }
        finally
        {
            _write.Release();
        }

        users.NotifyDeviceAccessChanged([.. userIds.Select(u => (u, DeviceScope.None))]);
        CallerContext.Raise(Deleted, this, result);
        return result;
    }

    /// <summary>
    /// A short summary of rules for the audit log and the group list: "This server, 2 sites, 3 tags, 4 devices, except 1".
    /// The list names sites and tags (<see cref="DeviceGroupSummary"/>); the audit log counts them.
    /// </summary>
    public static string Describe(DeviceGroupRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var parts = new List<string>();
        if (rules.ThisServer)
        {
            parts.Add(DeviceGroupTexts.ThisServer);
        }

        if (rules.SiteIds.Count > 0)
        {
            parts.Add(Count(rules.SiteIds.Count, "site", "sites"));
        }

        if (rules.TagIds.Count > 0)
        {
            parts.Add(Count(rules.TagIds.Count, "tag", "tags"));
        }

        if (rules.Serials.Count > 0)
        {
            parts.Add(Count(rules.Serials.Count, "device", "devices"));
        }

        if (rules.ExceptSerials.Count > 0)
        {
            parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"except {rules.ExceptSerials.Count:N0}"));
        }

        return parts.Count == 0 ? DeviceGroupTexts.NoDevices : string.Join(", ", parts);
    }

    public void Dispose() => _write.Dispose();

    internal static string Count(int count, string one, string many) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{count:N0} {(count == 1 ? one : many)}");

    private static string ValidateName(string? name)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            throw new DeviceGroupException(DeviceGroupError.InvalidArgument, DeviceGroupTexts.EnterName);
        }

        if (clean.Length > DeviceGroup.MaxNameLength || clean.Any(char.IsControl))
        {
            throw new DeviceGroupException(DeviceGroupError.InvalidArgument, DeviceGroupTexts.NameTooLong);
        }

        return clean;
    }

    private static bool SameRules(DeviceGroupRules a, DeviceGroupRules b) =>
        a.ThisServer == b.ThisServer
        && a.SiteIds.Order().SequenceEqual(b.SiteIds.Order())
        && a.TagIds.Order().SequenceEqual(b.TagIds.Order())
        && a.Serials.SequenceEqual(b.Serials, StringComparer.Ordinal)
        && a.ExceptSerials.SequenceEqual(b.ExceptSerials, StringComparer.Ordinal);

    private static async Task<IReadOnlyDictionary<Guid, DeviceGroup>> ReadAllAsync(OadmDbContext db, CancellationToken ct)
    {
        var rows = await db.DeviceGroups.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(g => g.Id);
    }
}
