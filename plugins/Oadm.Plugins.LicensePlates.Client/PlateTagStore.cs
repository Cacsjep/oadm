using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>
/// The plate tags of the server as the page knows them (<see cref="LicensePlatesMethods.Tags"/>), by id, in name order: the
/// chips of the plates and cameras, the tag filter and the tag dialogs. At most 200; <see cref="Changed"/> after each read.
/// </summary>
public sealed class PlateTagStore
{
    private Dictionary<Guid, TagInfo> _byId = [];
    private Dictionary<string, TagInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tags in name order.</summary>
    public IReadOnlyList<TagInfo> All { get; private set; } = [];

    /// <summary>The tags were read again.</summary>
    public event EventHandler? Changed;

    public void Set(IReadOnlyList<TagInfo> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        All = [.. tags.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
        _byId = All.ToDictionary(t => t.Id);
        _byName = All.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public TagInfo? Find(Guid id) => _byId.GetValueOrDefault(id);

    /// <summary>The chips of tag ids in name order; ids of deleted tags are left out.</summary>
    public IReadOnlyList<TagChip> Chips(IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var chips = new List<TagChip>(ids.Count);
        foreach (var id in ids)
        {
            if (_byId.TryGetValue(id, out var tag))
            {
                chips.Add(new TagChip(tag.Name, (int)tag.Color));
            }
        }

        chips.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return chips;
    }

    /// <summary>The chips of tag names (a read keeps the names of its time); a name without a tag now is grey.</summary>
    public IReadOnlyList<TagChip> ChipsOfNames(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count == 0
            ? []
            : [.. names.Select(n => new TagChip(n, _byName.TryGetValue(n, out var tag) ? (int)tag.Color : 0))];
    }

    /// <summary>"Garage A, Garage B" for a tooltip or a cell's sort key.</summary>
    public string NamesOf(IReadOnlyList<Guid> ids) => string.Join(", ", Chips(ids).Select(c => c.Name));
}
