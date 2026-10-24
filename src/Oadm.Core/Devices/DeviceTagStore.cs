using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Oadm.Core.DeviceGroups;
using Oadm.Core.Persistence;

namespace Oadm.Core.Devices;

/// <summary>
/// Tag definitions (table TagDefinitions) and the tags of devices (<see cref="Device.Tags"/>). Every write that touches
/// devices is one transaction for all of them (5,000 devices: one commit) and publishes one Updated change per changed
/// device to <see cref="IDeviceChangeFeed"/>. Writes are serialized (name uniqueness, rewrites). Devices store their
/// tags distinct and sorted (<see cref="TagNames.Canonical"/>), with the spelling of the definition.
/// The store is the only writer of device tags and holds the device-group guard (docs/spec/device-scope.md, Decisions: Tags):
/// a user with a device group tags devices of the group but never adds or removes a tag any device group uses (that would
/// move devices between groups). The guard reads the groups inside the write transaction, which holds SQLite's write lock
/// from its start, so a group saved meanwhile is either seen or commits after the tagging. In the same transaction it refuses
/// the tags core plugins protect (<see cref="ProtectedTagRegistry"/>) for such a user.
/// </summary>
public sealed partial class DeviceTagStore(
    IDbContextFactory<OadmDbContext> dbFactory,
    IDeviceChangeFeed changeFeed,
    TimeProvider time,
    ILogger<DeviceTagStore>? logger = null,
    ProtectedTagRegistry? protectedTags = null) : IDisposable
{
    private const int Chunk = 500;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _version;

    /// <summary>Raised after the definitions changed (create, rename, recolor, delete, created by tagging).</summary>
    public event EventHandler? DefinitionsChanged;

    /// <summary>Incremented with every <see cref="DefinitionsChanged"/>.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Every definition sorted by name with its device count, then the undefined names used on devices (sorted).</summary>
    public Task<IReadOnlyList<TagSummary>> ListAsync(CancellationToken ct) => ListAsync(null, ct);

    /// <summary>
    /// Every definition sorted by name with its device count in <paramref name="view"/> (null: every device), then the
    /// undefined names used on devices of the view (sorted). Definitions are listed for every user (names, no device data).
    /// </summary>
    public async Task<IReadOnlyList<TagSummary>> ListAsync(DeviceView? view, CancellationToken ct)
    {
        IReadOnlySet<Guid>? allowed = view is null || view.HasAllDevices ? null : await view.IdsAsync(ct).ConfigureAwait(false);
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var definitions = await db.TagDefinitions.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var counts = await CountAsync(db, allowed, ct).ConfigureAwait(false);
        var result = new List<TagSummary>(definitions.Count);
        foreach (var definition in definitions.OrderBy(d => d.Name, TagNames.Comparer))
        {
            result.Add(new TagSummary(definition.Id, definition.Name, definition.Color, counts.GetValueOrDefault(definition.Name), Defined: true));
            counts.Remove(definition.Name);
        }

        foreach (var (name, count) in counts.OrderBy(c => c.Key, TagNames.Comparer))
        {
            result.Add(new TagSummary(null, name, TagColor.None, count, Defined: false));
        }

        return result;
    }

    /// <summary>All definitions, sorted by name.</summary>
    public async Task<IReadOnlyList<TagDefinition>> ListDefinitionsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var definitions = await db.TagDefinitions.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        definitions.Sort((a, b) => TagNames.Comparer.Compare(a.Name, b.Name));
        return definitions;
    }

    /// <summary>Creates a definition. <see cref="TagColor.None"/> = the next palette color.</summary>
    /// <exception cref="TagNameException">Bad name.</exception>
    /// <exception cref="TagConflictException">The name is in use.</exception>
    /// <exception cref="TagLimitException">Already <see cref="TagNames.MaxDefinitions"/> definitions.</exception>
    public async Task<TagDefinition> CreateAsync(string name, TagColor color, CancellationToken ct)
    {
        string clean = TagNames.Clean(name);
        TagDefinition created;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            string normalized = TagNames.Normalize(clean);
            if (await db.TagDefinitions.AnyAsync(t => t.NormalizedName == normalized, ct).ConfigureAwait(false))
            {
                throw new TagConflictException(clean);
            }

            int count = await db.TagDefinitions.CountAsync(ct).ConfigureAwait(false);
            created = NewDefinition(clean, color, count);
            db.TagDefinitions.Add(created);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        await ReloadNamesAfterWriteAsync().ConfigureAwait(false);
        RaiseDefinitionsChanged();
        return created.Clone();
    }

    /// <summary>
    /// Renames and / or recolors the tag <paramref name="name"/> (null new name or <see cref="TagColor.None"/> keep). A rename
    /// rewrites every device's tag in the same transaction. A name used only on devices gets a definition.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such tag.</exception>
    public async Task<TagUpdateResult> UpdateAsync(string name, string? newName, TagColor color, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        string? cleanNew = string.IsNullOrWhiteSpace(newName) ? null : TagNames.Clean(newName);
        TagUpdateResult result;
        List<Device> changed;
        // The publish order of the devices is held from before the transaction (StoredDevicePublisher.HoldAsync): the rows
        // written are published as they are, without reading them again.
        using var lease = await StoredDevicePublisher.HoldAsync(changeFeed).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            string normalized = TagNames.Normalize(name);
            var definition = await db.TagDefinitions.FirstOrDefaultAsync(t => t.NormalizedName == normalized, ct).ConfigureAwait(false);
            string oldName;
            TagColor oldColor;
            if (definition is null)
            {
                // A name only on devices (older data): it gets a definition.
                var counts = await CountAsync(db, null, ct).ConfigureAwait(false);
                if (!counts.ContainsKey(name.Trim()))
                {
                    throw new KeyNotFoundException($"There is no tag named \"{name.Trim()}\".");
                }

                int count = await db.TagDefinitions.CountAsync(ct).ConfigureAwait(false);
                oldName = counts.Keys.First(k => TagNames.Comparer.Equals(k, name.Trim()));
                oldColor = TagColor.None;
                definition = NewDefinition(oldName, color, count);
                db.TagDefinitions.Add(definition);
            }
            else
            {
                oldName = definition.Name;
                oldColor = definition.Color;
            }

            string target = cleanNew ?? oldName;
            if (!TagNames.Comparer.Equals(target, oldName)
                && await db.TagDefinitions.AnyAsync(t => t.NormalizedName == TagNames.Normalize(target) && t.Id != definition.Id, ct).ConfigureAwait(false))
            {
                throw new TagConflictException(target);
            }

            definition.Name = target;
            definition.NormalizedName = TagNames.Normalize(target);
            if (color != TagColor.None)
            {
                definition.Color = color;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            changed = string.Equals(target, oldName, StringComparison.Ordinal)
                ? []
                : (await RewriteAsync(db, ids: null, tags => Replace(tags, oldName, target), ct).ConfigureAwait(false)).ConvertAll(c => c.After);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            result = new TagUpdateResult(definition.Clone(), oldName, oldColor, changed.Count);
        }
        finally
        {
            _writeLock.Release();
        }

        await ReloadNamesAfterWriteAsync().ConfigureAwait(false); // before the devices: device groups follow a rename at once
        Publish(lease, changed);
        RaiseDefinitionsChanged();
        return result;
    }

    /// <summary>
    /// Deletes the tag: its definition and the tag on every device, in one transaction. Returns the devices changed with
    /// their tags before and after. Device groups that use the tag lose it (their rules keep the id of a tag that is gone).
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such tag.</exception>
    public async Task<TagDeleteResult> DeleteAsync(string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        string deletedName;
        List<TagRewrite> changed;
        // The publish order of the devices is held from before the transaction (StoredDevicePublisher.HoldAsync): the rows
        // written are published as they are, without reading them again.
        using var lease = await StoredDevicePublisher.HoldAsync(changeFeed).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            string normalized = TagNames.Normalize(name);
            var definition = await db.TagDefinitions.FirstOrDefaultAsync(t => t.NormalizedName == normalized, ct).ConfigureAwait(false);
            deletedName = definition?.Name ?? name.Trim();
            if (definition is not null)
            {
                db.TagDefinitions.Remove(definition);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            changed = await RewriteAsync(db, ids: null, tags => Remove(tags, [deletedName]), ct).ConfigureAwait(false);
            if (definition is null && changed.Count == 0)
            {
                throw new KeyNotFoundException($"There is no tag named \"{deletedName}\".");
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        await ReloadNamesAfterWriteAsync().ConfigureAwait(false); // before the devices: device groups follow the tag at once
        Publish(lease, changed);
        RaiseDefinitionsChanged();
        return new TagDeleteResult(deletedName, changed.Count, Changes(changed));
    }

    /// <summary>
    /// Adds and removes tag names on many devices in one transaction (one call for 5,000 devices). Names in
    /// <paramref name="add"/> without a definition get one (next palette color). The devices come from the caller's view;
    /// for a view of a device group no name in <paramref name="add"/> or <paramref name="remove"/> may be a tag a device
    /// group uses (<see cref="TagInUseByGroupException"/>) or a plugin protects (<see cref="ProtectedTagException"/>, the
    /// 802.1X server's VLAN tags); nothing changed.
    /// </summary>
    /// <exception cref="TagNameException">A name to add cannot be used, or a name is both added and removed.</exception>
    /// <exception cref="TagLimitException">A device would get more than <see cref="TagNames.MaxTagsPerDevice"/> tags, or too many definitions.</exception>
    /// <exception cref="TagInUseByGroupException">A user with a device group added or removed a tag a group uses.</exception>
    /// <exception cref="ProtectedTagException">A user with a device group added or removed a tag a plugin protects.</exception>
    public async Task<SetDeviceTagsResult> SetDeviceTagsAsync(
        ViewedDevices devices, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        var toAdd = TagNames.Canonical(add.Select(TagNames.Clean));
        var toRemove = TagNames.Canonical(remove.Where(r => !string.IsNullOrWhiteSpace(r)));
        if (toAdd.FirstOrDefault(a => toRemove.Contains(a, TagNames.Comparer)) is { } both)
        {
            throw new TagNameException($"The tag \"{both}\" cannot be added and removed at the same time.");
        }

        if (devices.Count == 0 || (toAdd.Count == 0 && toRemove.Count == 0))
        {
            return new SetDeviceTagsResult(0, []);
        }

        var created = new List<TagDefinition>();
        List<TagRewrite> changed;
        // The publish order of the devices is held from before the transaction (StoredDevicePublisher.HoldAsync): the rows
        // written are published as they are, without reading them again.
        using var lease = await StoredDevicePublisher.HoldAsync(changeFeed).ConfigureAwait(false);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            // BEGIN IMMEDIATE (Microsoft.Data.Sqlite's default): the write lock is held from here, so the groups the guard
            // reads are the committed ones and no group save commits before this transaction ends.
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var definitions = (await db.TagDefinitions.ToListAsync(ct).ConfigureAwait(false))
                .ToDictionary(d => d.NormalizedName, StringComparer.Ordinal);
            if (!devices.View.HasAllDevices)
            {
                await GuardAsync(db, definitions, toAdd.Concat(toRemove), ct).ConfigureAwait(false);

                // The tags plugins protect (the 802.1X server's VLAN rules), by name, in the same transaction.
                foreach (var name in toAdd.Concat(toRemove))
                {
                    if (protectedTags?.RefusalFor([name]) is { } refusal)
                    {
                        throw new ProtectedTagException(definitions.TryGetValue(TagNames.Normalize(name), out var named) ? named.Name : name, refusal);
                    }
                }
            }

            for (int i = 0; i < toAdd.Count; i++)
            {
                if (definitions.TryGetValue(TagNames.Normalize(toAdd[i]), out var existing))
                {
                    toAdd[i] = existing.Name; // the definition's spelling
                    continue;
                }

                var definition = NewDefinition(toAdd[i], TagColor.None, definitions.Count);
                db.TagDefinitions.Add(definition);
                definitions[definition.NormalizedName] = definition;
                created.Add(definition);
            }

            if (created.Count > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            changed = await RewriteAsync(db, devices.Ids, tags =>
            {
                var next = Remove(tags, toRemove) ?? tags;
                var result = TagNames.Canonical(next.Where(t => !toAdd.Contains(t, TagNames.Comparer)).Concat(toAdd));
                if (result.Count > TagNames.MaxTagsPerDevice)
                {
                    throw new TagLimitException($"A device can have at most {TagNames.MaxTagsPerDevice} tags. Nothing was changed.");
                }

                return result.SequenceEqual(tags, StringComparer.Ordinal) ? null : result;
            }, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        if (created.Count > 0)
        {
            await ReloadNamesAfterWriteAsync().ConfigureAwait(false); // before the devices: a device group sees the new tag at once
        }

        Publish(lease, changed);
        if (created.Count > 0)
        {
            RaiseDefinitionsChanged();
        }

        return new SetDeviceTagsResult(changed.Count, created.Select(c => c.Clone()).ToList()) { Changes = Changes(changed) };
    }

    /// <summary>
    /// Refuses a name that is the definition of a tag a device group uses (read inside the write transaction). A name
    /// without a definition is no group's tag (groups name tags by id).
    /// </summary>
    private static async Task GuardAsync(OadmDbContext db, Dictionary<string, TagDefinition> definitions, IEnumerable<string> names, CancellationToken ct)
    {
        var used = new HashSet<Guid>();
        foreach (var tagIds in await db.DeviceGroups.AsNoTracking().Select(g => g.TagIds).ToListAsync(ct).ConfigureAwait(false))
        {
            used.UnionWith(tagIds);
        }

        if (used.Count == 0)
        {
            return;
        }

        foreach (var name in names)
        {
            if (definitions.TryGetValue(TagNames.Normalize(name), out var definition) && used.Contains(definition.Id))
            {
                throw new TagInUseByGroupException(definition.Name);
            }
        }
    }

    private static List<(Sdk.Devices.IDeviceInfo? Before, Sdk.Devices.IDeviceInfo? After)> Changes(List<TagRewrite> changed) =>
        changed.ConvertAll(c =>
        {
            var before = c.After.Clone();
            before.Tags = c.OldTags;
            return ((Sdk.Devices.IDeviceInfo?)before, (Sdk.Devices.IDeviceInfo?)c.After);
        });

    private TagDefinition NewDefinition(string name, TagColor color, int existing)
    {
        if (existing >= TagNames.MaxDefinitions)
        {
            throw new TagLimitException($"There are already {TagNames.MaxDefinitions} tags. Delete tags that are no longer used first.");
        }

        return new TagDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = TagNames.Normalize(name),
            Color = color == TagColor.None ? TagNames.DefaultColor(existing) : color,
            CreatedUtc = time.GetUtcNow().UtcDateTime,
        };
    }

    /// <summary>
    /// Device count per tag name used on devices (case-insensitive, first spelling seen), only of the devices in
    /// <paramref name="allowed"/> when given. Reads only the Id and Tags columns.
    /// </summary>
    private static async Task<Dictionary<string, int>> CountAsync(OadmDbContext db, IReadOnlySet<Guid>? allowed, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(TagNames.Comparer);
        await foreach (var row in db.Devices.AsNoTracking().Select(d => new { d.Id, d.Tags }).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            if (allowed is not null && !allowed.Contains(row.Id))
            {
                continue;
            }

            foreach (string tag in row.Tags.Distinct(TagNames.Comparer))
            {
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// Applies <paramref name="rewrite"/> (null = unchanged) to the tags of the given devices (null = all) inside the caller's
    /// transaction: candidates are found on the Tags column alone, then only the changed rows are loaded and written (in
    /// chunks: SQLite limits the parameters per statement). Returns detached copies of the changed devices with their tags before.
    /// </summary>
    private static async Task<List<TagRewrite>> RewriteAsync(OadmDbContext db, IReadOnlyCollection<Guid>? ids, Func<List<string>, List<string>?> rewrite, CancellationToken ct)
    {
        var updates = new Dictionary<Guid, List<string>>();
        if (ids is null)
        {
            await foreach (var row in db.Devices.AsNoTracking().Select(d => new { d.Id, d.Tags }).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
            {
                if (rewrite(row.Tags) is { } next)
                {
                    updates[row.Id] = next;
                }
            }
        }
        else
        {
            foreach (var chunk in ids.Distinct().Chunk(Chunk))
            {
                var rows = await db.Devices.AsNoTracking().Where(d => chunk.Contains(d.Id)).Select(d => new { d.Id, d.Tags }).ToListAsync(ct).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    if (rewrite(row.Tags) is { } next)
                    {
                        updates[row.Id] = next;
                    }
                }
            }
        }

        var changed = new List<TagRewrite>(updates.Count);
        foreach (var chunk in updates.Keys.Chunk(Chunk))
        {
            var entities = await db.Devices.Where(d => chunk.Contains(d.Id)).ToListAsync(ct).ConfigureAwait(false);
            foreach (var entity in entities)
            {
                var before = entity.Tags;
                entity.Tags = updates[entity.Id];
                changed.Add(new TagRewrite(entity, before));
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // The saved rows leave the tracker: otherwise every later SaveChanges checks all earlier chunks again
            // (30,000 devices: 60 chunks over a growing set, the time grew with the square of the count).
            db.ChangeTracker.Clear();
        }

        return changed.ConvertAll(c => c with { After = c.After.Clone() });
    }

    /// <summary>A device whose tags a write changed: the device after the write and its tags before.</summary>
    private sealed record TagRewrite(Device After, List<string> OldTags);

    private static void Publish(StoredDevicePublisher.Lease lease, List<TagRewrite> changed) => Publish(lease, changed.ConvertAll(c => c.After));

    private static List<string>? Replace(List<string> tags, string oldName, string newName) =>
        tags.Any(t => TagNames.Comparer.Equals(t, oldName))
            ? TagNames.Canonical(tags.Select(t => TagNames.Comparer.Equals(t, oldName) ? newName : t))
            : null;

    private static List<string>? Remove(List<string> tags, IReadOnlyCollection<string> names) =>
        tags.Any(t => names.Contains(t, TagNames.Comparer))
            ? tags.Where(t => !names.Contains(t, TagNames.Comparer)).ToList()
            : null;

    /// <summary>
    /// Publishes the changed devices as the transaction wrote them (read inside it, while the lease held every publish of
    /// them: what the table holds) and releases the lease, before the definitions event.
    /// </summary>
    private static void Publish(StoredDevicePublisher.Lease lease, List<Device> changed)
    {
        lease.Publish(changed);
        lease.Dispose();
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _namesLock.Dispose();
    }

    private void RaiseDefinitionsChanged() => Auth.CallerContext.Raise(DefinitionsChanged, this, EventArgs.Empty);

    /// <summary>
    /// The names of the definitions by id, as committed (null until <see cref="DefinitionNamesAsync"/> first read them).
    /// Updated after every write that changed definitions, before its device changes are published and before
    /// <see cref="Version"/> moves, so device groups (tags by id) resolve a renamed or new tag at once.
    /// </summary>
    public IReadOnlyDictionary<Guid, string>? DefinitionNames => Volatile.Read(ref _names);

    /// <summary><see cref="DefinitionNames"/>, read from the database the first time.</summary>
    public async ValueTask<IReadOnlyDictionary<Guid, string>> DefinitionNamesAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _names) is { } names)
        {
            return names;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var loaded = await db.TagDefinitions.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct).ConfigureAwait(false);
        Interlocked.CompareExchange(ref _names, loaded, null);
        return Volatile.Read(ref _names)!;
    }

    private IReadOnlyDictionary<Guid, string>? _names;
    private readonly SemaphoreSlim _namesLock = new(1, 1);

    /// <summary>Reads the committed definitions again (after a write, never cancelled) and moves <see cref="Version"/>.</summary>
    /// <summary>
    /// The names after a committed write: a failed read never keeps the change from being published (the devices and the
    /// definitions changed already). The names stay as they were (fail closed: a group's new or renamed tag covers no
    /// device until the next write reads them) and the catalog version moves, so the views compile again.
    /// </summary>
    private async Task ReloadNamesAfterWriteAsync()
    {
        try
        {
            await ReloadNamesAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The write is committed: publish it whatever the read of the names says.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Interlocked.Increment(ref _version);
            LogNamesReloadFailed(logger ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ex);
        }
    }

    /// <summary>Tests: runs before the names are read again after a write (throw to simulate a failed read).</summary>
    internal Action? BeforeNamesReload { get; set; }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the tag names after a change failed; device groups keep the names they had until the next change")]
    private static partial void LogNamesReloadFailed(ILogger logger, Exception ex);

    private async Task ReloadNamesAsync()
    {
        BeforeNamesReload?.Invoke();
        await _namesLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            var loaded = await db.TagDefinitions.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref _names, loaded);
            Interlocked.Increment(ref _version);
        }
        finally
        {
            _namesLock.Release();
        }
    }
}
