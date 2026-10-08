using Microsoft.EntityFrameworkCore;

using Oadm.Core.Persistence;

namespace Oadm.Core.Devices;

/// <summary>
/// Tag definitions (table TagDefinitions) and the tags of devices (<see cref="Device.Tags"/>). Every write that touches
/// devices is one transaction for all of them (5,000 devices: one commit) and publishes one Updated change per changed
/// device to <see cref="IDeviceChangeFeed"/>. Writes are serialized (name uniqueness, rewrites). Devices store their
/// tags distinct and sorted (<see cref="TagNames.Canonical"/>), with the spelling of the definition.
/// </summary>
public sealed class DeviceTagStore(IDbContextFactory<OadmDbContext> dbFactory, IDeviceChangeFeed changeFeed, TimeProvider time) : IDisposable
{
    private const int Chunk = 500;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _version;

    /// <summary>Raised after the definitions changed (create, rename, recolor, delete, created by tagging).</summary>
    public event EventHandler? DefinitionsChanged;

    /// <summary>Incremented with every <see cref="DefinitionsChanged"/>.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Every definition sorted by name with its device count, then the undefined names used on devices (sorted).</summary>
    public async Task<IReadOnlyList<TagSummary>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var definitions = await db.TagDefinitions.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var counts = await CountAsync(db, ct).ConfigureAwait(false);
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
                var counts = await CountAsync(db, ct).ConfigureAwait(false);
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
                : await RewriteAsync(db, ids: null, tags => Replace(tags, oldName, target), ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            result = new TagUpdateResult(definition.Clone(), oldName, oldColor, changed.Count);
        }
        finally
        {
            _writeLock.Release();
        }

        Publish(changed);
        RaiseDefinitionsChanged();
        return result;
    }

    /// <summary>Deletes the tag: its definition and the tag on every device, in one transaction. Returns the devices changed.</summary>
    /// <exception cref="KeyNotFoundException">No such tag.</exception>
    public async Task<(string Name, int DevicesChanged)> DeleteAsync(string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        string deletedName;
        List<Device> changed;
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

        Publish(changed);
        RaiseDefinitionsChanged();
        return (deletedName, changed.Count);
    }

    /// <summary>
    /// Adds and removes tag names on many devices in one transaction (one call for 5,000 devices). Names in
    /// <paramref name="add"/> without a definition get one (next palette color). Unknown device ids are ignored.
    /// </summary>
    /// <exception cref="TagNameException">A name to add cannot be used, or a name is both added and removed.</exception>
    /// <exception cref="TagLimitException">A device would get more than <see cref="TagNames.MaxTagsPerDevice"/> tags, or too many definitions.</exception>
    public async Task<SetDeviceTagsResult> SetDeviceTagsAsync(
        IReadOnlyCollection<Guid> deviceIds, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        var toAdd = TagNames.Canonical(add.Select(TagNames.Clean));
        var toRemove = TagNames.Canonical(remove.Where(r => !string.IsNullOrWhiteSpace(r)));
        if (toAdd.FirstOrDefault(a => toRemove.Contains(a, TagNames.Comparer)) is { } both)
        {
            throw new TagNameException($"The tag \"{both}\" cannot be added and removed at the same time.");
        }

        if (deviceIds.Count == 0 || (toAdd.Count == 0 && toRemove.Count == 0))
        {
            return new SetDeviceTagsResult(0, []);
        }

        var created = new List<TagDefinition>();
        List<Device> changed;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var definitions = (await db.TagDefinitions.ToListAsync(ct).ConfigureAwait(false))
                .ToDictionary(d => d.NormalizedName, StringComparer.Ordinal);
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

            changed = await RewriteAsync(db, deviceIds, tags =>
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

        Publish(changed);
        if (created.Count > 0)
        {
            RaiseDefinitionsChanged();
        }

        return new SetDeviceTagsResult(changed.Count, created.Select(c => c.Clone()).ToList());
    }

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

    /// <summary>Device count per tag name used on devices (case-insensitive, first spelling seen). Reads only the Tags column.</summary>
    private static async Task<Dictionary<string, int>> CountAsync(OadmDbContext db, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(TagNames.Comparer);
        await foreach (var tags in db.Devices.AsNoTracking().Select(d => d.Tags).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            foreach (string tag in tags.Distinct(TagNames.Comparer))
            {
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// Applies <paramref name="rewrite"/> (null = unchanged) to the tags of the given devices (null = all) inside the caller's
    /// transaction: candidates are found on the Tags column alone, then only the changed rows are loaded and written (in
    /// chunks: SQLite limits the parameters per statement). Returns detached copies of the changed devices.
    /// </summary>
    private static async Task<List<Device>> RewriteAsync(OadmDbContext db, IReadOnlyCollection<Guid>? ids, Func<List<string>, List<string>?> rewrite, CancellationToken ct)
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

        var changed = new List<Device>(updates.Count);
        foreach (var chunk in updates.Keys.Chunk(Chunk))
        {
            var entities = await db.Devices.Where(d => chunk.Contains(d.Id)).ToListAsync(ct).ConfigureAwait(false);
            foreach (var entity in entities)
            {
                entity.Tags = updates[entity.Id];
                changed.Add(entity);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return changed.Select(d => d.Clone()).ToList();
    }

    private static List<string>? Replace(List<string> tags, string oldName, string newName) =>
        tags.Any(t => TagNames.Comparer.Equals(t, oldName))
            ? TagNames.Canonical(tags.Select(t => TagNames.Comparer.Equals(t, oldName) ? newName : t))
            : null;

    private static List<string>? Remove(List<string> tags, IReadOnlyCollection<string> names) =>
        tags.Any(t => names.Contains(t, TagNames.Comparer))
            ? tags.Where(t => !names.Contains(t, TagNames.Comparer)).ToList()
            : null;

    private void Publish(List<Device> changed)
    {
        foreach (var device in changed)
        {
            changeFeed.Publish(new DeviceChange(DeviceChangeKind.Updated, device.Id, device));
        }
    }

    public void Dispose() => _writeLock.Dispose();

    private void RaiseDefinitionsChanged()
    {
        Interlocked.Increment(ref _version);
        DefinitionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
