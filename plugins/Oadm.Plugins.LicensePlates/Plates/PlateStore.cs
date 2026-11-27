using Oadm.Plugins.LicensePlates.Matching;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LicensePlates.Plates;

/// <summary>A camera of the plugin: the plate tags allowed there.</summary>
public sealed record CameraEntry
{
    public Guid DeviceId { get; init; }

    public IReadOnlyList<Guid> AllowedTags { get; init; } = [];

    public DateTimeOffset Added { get; init; }

    /// <summary>The camera's own name in the plugin; null: the device's name in OADM.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// The plugin's records (part of the backup): the plates (one record per plate, key = its text), the plate tags and the
/// cameras of the plugin with their allowed tags. Everything is in memory for the matcher; writes go to the records first
/// (one transaction per call) and then into memory, one at a time. One list for the server (decision 2026-11-27).
/// </summary>
internal sealed class PlateStore(IPluginRecords records) : IPlateSource, IDisposable
{
    public const string PlatesCollection = "plates";
    public const string TagsCollection = "tags";
    public const string CamerasCollection = "cameras";

    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Plate> _plates = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, PlateTag> _tags = [];
    private readonly Dictionary<Guid, CameraEntry> _cameras = [];
    private readonly SortedSet<(DateTime End, string Text)> _ending = [];
    private readonly WildcardIndex _wildcards = new();
    private string[]? _sorted;

    /// <summary>Plates with these tags changed (the matcher's indexes follow).</summary>
    public event Action<IReadOnlyCollection<Guid>>? PlatesChanged;

    public async Task LoadAsync(CancellationToken ct)
    {
        var options = LicensePlatesJson.Options;
        var plates = await records.ListAsync(PlatesCollection, ct).ConfigureAwait(false);
        var tags = await records.ListAsync(TagsCollection, ct).ConfigureAwait(false);
        var cameras = await records.ListAsync(CamerasCollection, ct).ConfigureAwait(false);
        lock (_sync)
        {
            _plates.Clear();
            _tags.Clear();
            _cameras.Clear();
            _ending.Clear();
            _wildcards.Clear();
            foreach (var json in tags.Values)
            {
                if (PluginJson.TryParse<PlateTag>(json, options) is { Id: var id } tag && id != Guid.Empty)
                {
                    _tags[id] = tag;
                }
            }

            foreach (var (key, json) in plates)
            {
                if (PluginJson.TryParse<Plate>(json, options) is { } plate && plate.Text == key)
                {
                    PutLocked(plate with { Tags = [.. plate.Tags.Where(_tags.ContainsKey)] });
                }
            }

            foreach (var json in cameras.Values)
            {
                if (PluginJson.TryParse<CameraEntry>(json, options) is { DeviceId: var id } camera && id != Guid.Empty)
                {
                    _cameras[id] = camera with { AllowedTags = [.. camera.AllowedTags.Where(_tags.ContainsKey)] };
                }
            }

            _sorted = null;
        }
    }

    // ---- plates ----

    public int PlateCount
    {
        get
        {
            lock (_sync)
            {
                return _plates.Count;
            }
        }
    }

    public Plate? Find(string text)
    {
        lock (_sync)
        {
            return _plates.GetValueOrDefault(text);
        }
    }

    /// <summary>How many wildcard plates the list has.</summary>
    public int WildcardCount
    {
        get
        {
            lock (_sync)
            {
                return _wildcards.Count;
            }
        }
    }

    public IReadOnlyList<Plate> WildcardsFitting(string read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_sync)
        {
            return [.. _wildcards.Fitting(read).Select(p => _plates[p])];
        }
    }

    public IReadOnlyCollection<string> TextsWithAnyTag(IReadOnlySet<Guid> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var texts = new List<string>();
        lock (_sync)
        {
            foreach (var plate in _plates.Values)
            {
                if (PlateText.IsWildcard(plate.Text))
                {
                    continue; // never a near match to a wildcard plate
                }

                foreach (var tag in plate.Tags)
                {
                    if (tags.Contains(tag))
                    {
                        texts.Add(plate.Text);
                        break;
                    }
                }
            }
        }

        return texts;
    }

    /// <summary>
    /// A page of plates in plate order after <paramref name="after"/>, filtered by a search (text prefix or name) and a tag;
    /// with the count of every match.
    /// </summary>
    public (IReadOnlyList<Plate> Page, string? Next, int Total) Page(string? after, int limit, string? search, Guid? tag)
    {
        limit = Math.Clamp(limit, 1, LicensePlateLimits.MaxPlatePage);
        var prefix = PlateText.Normalize(search);
        var term = search?.Trim() ?? string.Empty;
        lock (_sync)
        {
            _sorted ??= [.. _plates.Keys.Order(StringComparer.Ordinal)];
            var start = 0;
            if (after is not null)
            {
                var found = Array.BinarySearch(_sorted, after, StringComparer.Ordinal);
                start = found >= 0 ? found + 1 : ~found;
            }

            var page = new List<Plate>(Math.Min(limit, _sorted.Length));
            var total = 0;
            string? next = null;
            for (var n = 0; n < _sorted.Length; n++)
            {
                var plate = _plates[_sorted[n]];
                if (!Matches(plate, prefix, term, tag))
                {
                    continue;
                }

                total++;
                if (n < start)
                {
                    continue;
                }

                if (page.Count < limit)
                {
                    page.Add(plate);
                }
                else
                {
                    next ??= page[^1].Text;
                }
            }

            return (page, next, total);
        }
    }

    private static bool Matches(Plate plate, string prefix, string term, Guid? tag)
    {
        if (tag is { } t && !plate.Tags.Contains(t))
        {
            return false;
        }

        return term.Length == 0
            || (prefix.Length > 0 && PlateText.Starts(plate.Text, prefix))
            || plate.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Every plate (export, the import's comparison).</summary>
    public IReadOnlyList<Plate> AllPlates()
    {
        lock (_sync)
        {
            _sorted ??= [.. _plates.Keys.Order(StringComparer.Ordinal)];
            return [.. _sorted.Select(t => _plates[t])];
        }
    }

    /// <summary>Plates that end before <paramref name="now"/> and are deleted when they end.</summary>
    public IReadOnlyList<Plate> EndedBy(DateTime now)
    {
        lock (_sync)
        {
            return [.. _ending.TakeWhile(e => e.End <= now).Select(e => _plates[e.Text])];
        }
    }

    /// <summary>Writes plates (new or changed) and deletes others in one transaction.</summary>
    public async Task WritePlatesAsync(IReadOnlyCollection<Plate> put, IReadOnlyCollection<string> delete, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(put);
        ArgumentNullException.ThrowIfNull(delete);
        if (put.Count == 0 && delete.Count == 0)
        {
            return;
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        HashSet<Guid> touched = [];
        try
        {
            var batch = new List<KeyValuePair<string, string?>>(put.Count + delete.Count);
            batch.AddRange(delete.Select(d => new KeyValuePair<string, string?>(d, null)));
            batch.AddRange(put.Select(p => new KeyValuePair<string, string?>(p.Text, PluginJson.Serialize(p, LicensePlatesJson.Options))));
            await records.SetManyAsync(PlatesCollection, batch, ct).ConfigureAwait(false);
            lock (_sync)
            {
                foreach (var text in delete)
                {
                    if (RemoveLocked(text) is { } gone)
                    {
                        touched.UnionWith(gone.Tags);
                    }
                }

                foreach (var plate in put)
                {
                    if (RemoveLocked(plate.Text) is { } old)
                    {
                        touched.UnionWith(old.Tags);
                    }

                    PutLocked(plate);
                    touched.UnionWith(plate.Tags);
                }

                _sorted = null;
            }
        }
        finally
        {
            _write.Release();
        }

        PlatesChanged?.Invoke(touched);
    }

    private void PutLocked(Plate plate)
    {
        _plates[plate.Text] = plate;
        if (PlateText.IsWildcard(plate.Text))
        {
            _wildcards.Add(plate.Text);
        }
        if (plate.DeleteWhenEnds && plate.AllowedTo is { } end)
        {
            _ending.Add((end, plate.Text));
        }
    }

    private Plate? RemoveLocked(string text)
    {
        if (!_plates.Remove(text, out var old))
        {
            return null;
        }

        _wildcards.Remove(old.Text);

        if (old.DeleteWhenEnds && old.AllowedTo is { } end)
        {
            _ending.Remove((end, old.Text));
        }

        return old;
    }

    // ---- tags ----

    public IReadOnlyList<PlateTag> Tags
    {
        get
        {
            lock (_sync)
            {
                return [.. _tags.Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    public PlateTag? Tag(Guid id)
    {
        lock (_sync)
        {
            return _tags.GetValueOrDefault(id);
        }
    }

    /// <summary>The names of <paramref name="ids"/> (tags that no longer exist are left out), in name order.</summary>
    public IReadOnlyList<string> NamesOf(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_sync)
        {
            return [.. ids.Select(id => _tags.GetValueOrDefault(id)?.Name).OfType<string>().Order(StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    /// <summary>How many plates and cameras use each tag.</summary>
    public IReadOnlyList<TagInfo> TagInfos()
    {
        lock (_sync)
        {
            var plates = new Dictionary<Guid, int>();
            foreach (var plate in _plates.Values)
            {
                foreach (var tag in plate.Tags)
                {
                    plates[tag] = plates.GetValueOrDefault(tag) + 1;
                }
            }

            var cameras = new Dictionary<Guid, int>();
            foreach (var camera in _cameras.Values)
            {
                foreach (var tag in camera.AllowedTags)
                {
                    cameras[tag] = cameras.GetValueOrDefault(tag) + 1;
                }
            }

            return [.. _tags.Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(t => new TagInfo(t.Id, t.Name, t.Color, plates.GetValueOrDefault(t.Id), cameras.GetValueOrDefault(t.Id)))];
        }
    }

    public async Task SaveTagAsync(PlateTag tag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tag);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await records.SetManyAsync(TagsCollection, [new(tag.Id.ToString("N"), PluginJson.Serialize(tag, LicensePlatesJson.Options))], ct).ConfigureAwait(false);
            lock (_sync)
            {
                _tags[tag.Id] = tag;
            }
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Deletes a tag and takes it off every plate and camera.</summary>
    public async Task DeleteTagAsync(Guid id, CancellationToken ct)
    {
        List<Plate> plates;
        List<CameraEntry> cameras;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                plates = [.. _plates.Values.Where(p => p.Tags.Contains(id)).Select(p => p with { Tags = [.. p.Tags.Where(t => t != id)] })];
                cameras = [.. _cameras.Values.Where(c => c.AllowedTags.Contains(id)).Select(c => c with { AllowedTags = [.. c.AllowedTags.Where(t => t != id)] })];
            }

            if (plates.Count > 0)
            {
                await records.SetManyAsync(PlatesCollection, [.. plates.Select(p => new KeyValuePair<string, string?>(p.Text, PluginJson.Serialize(p, LicensePlatesJson.Options)))], ct).ConfigureAwait(false);
            }

            if (cameras.Count > 0)
            {
                await records.SetManyAsync(CamerasCollection, [.. cameras.Select(c => new KeyValuePair<string, string?>(c.DeviceId.ToString("N"), PluginJson.Serialize(c, LicensePlatesJson.Options)))], ct).ConfigureAwait(false);
            }

            await records.SetManyAsync(TagsCollection, [new(id.ToString("N"), null)], ct).ConfigureAwait(false);
            lock (_sync)
            {
                foreach (var plate in plates)
                {
                    _plates[plate.Text] = plate;
                }

                foreach (var camera in cameras)
                {
                    _cameras[camera.DeviceId] = camera;
                }

                _tags.Remove(id);
            }
        }
        finally
        {
            _write.Release();
        }

        PlatesChanged?.Invoke([id]);
    }

    // ---- cameras ----

    public IReadOnlyList<CameraEntry> Cameras
    {
        get
        {
            lock (_sync)
            {
                return [.. _cameras.Values];
            }
        }
    }

    public CameraEntry? Camera(Guid deviceId)
    {
        lock (_sync)
        {
            return _cameras.GetValueOrDefault(deviceId);
        }
    }

    /// <summary>Writes and deletes cameras in one transaction.</summary>
    public async Task WriteCamerasAsync(IReadOnlyCollection<CameraEntry> put, IReadOnlyCollection<Guid> delete, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(put);
        ArgumentNullException.ThrowIfNull(delete);
        if (put.Count == 0 && delete.Count == 0)
        {
            return;
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var batch = new List<KeyValuePair<string, string?>>(put.Count + delete.Count);
            batch.AddRange(delete.Select(d => new KeyValuePair<string, string?>(d.ToString("N"), null)));
            batch.AddRange(put.Select(c => new KeyValuePair<string, string?>(c.DeviceId.ToString("N"), PluginJson.Serialize(c, LicensePlatesJson.Options))));
            await records.SetManyAsync(CamerasCollection, batch, ct).ConfigureAwait(false);
            lock (_sync)
            {
                foreach (var id in delete)
                {
                    _cameras.Remove(id);
                }

                foreach (var camera in put)
                {
                    _cameras[camera.DeviceId] = camera;
                }
            }
        }
        finally
        {
            _write.Release();
        }
    }

    public void Dispose() => _write.Dispose();
}
