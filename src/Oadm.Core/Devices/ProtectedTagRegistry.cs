using Oadm.Sdk.Devices;

namespace Oadm.Core.Devices;

/// <summary>
/// The tag names core plugins protect (<see cref="IProtectedTags"/>, docs/spec/tags.md "Protected tags"): only users with
/// all devices add them to or remove them from devices. <see cref="Plugins.CorePluginHost"/> hands each plugin its view
/// (<see cref="For"/>) and forgets a plugin's names when it stops (<see cref="RemoveOwner"/>); <see cref="DeviceTagStore"/>
/// asks <see cref="RefusalFor"/> inside its write transaction, next to the device-group guard. Thread-safe; a lookup is a
/// hash lookup per name.
/// </summary>
public sealed class ProtectedTagRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Owner> _owners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <see cref="IProtectedTags"/> of one core plugin.</summary>
    public IProtectedTags For(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var view = new OwnerTags(this, ownerId);
        lock (_gate)
        {
            if (_owners.TryGetValue(ownerId, out var owner))
            {
                owner.Views.Add(view);
            }
            else
            {
                _owners[ownerId] = new Owner([view], null);
            }
        }

        return view;
    }

    /// <summary>Forgets the names of <paramref name="ownerId"/> (the plugin stopped); its views set nothing any more.</summary>
    public void RemoveOwner(string ownerId)
    {
        lock (_gate)
        {
            if (_owners.Remove(ownerId, out var owner))
            {
                foreach (var view in owner.Views)
                {
                    view.Closed = true;
                }
            }
        }
    }

    /// <summary>Every protected name now (case-insensitive).</summary>
    public IReadOnlySet<string> Names
    {
        get
        {
            lock (_gate)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var owner in _owners.Values)
                {
                    if (owner.Set is { } set)
                    {
                        names.UnionWith(set.Names);
                    }
                }

                return names;
            }
        }
    }

    /// <summary>
    /// The refusal for the first of <paramref name="names"/> a plugin protects, null when none is protected. A plugin's text
    /// that fails or is empty becomes <see cref="DefaultRefusal"/> (the refusal never depends on the plugin's code).
    /// </summary>
    public string? RefusalFor(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        List<ProtectedSet> sets;
        lock (_gate)
        {
            sets = [.. _owners.Values.Select(o => o.Set).OfType<ProtectedSet>().Where(s => s.Names.Count > 0)];
        }

        if (sets.Count == 0)
        {
            return null;
        }

        foreach (var name in names)
        {
            var trimmed = name.Trim();
            foreach (var set in sets)
            {
                if (set.Names.Contains(trimmed))
                {
                    string? text = null;
                    try
                    {
                        text = set.Refusal(trimmed);
                    }
#pragma warning disable CA1031 // A failing plugin text still refuses, with the host's own text.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                    }

                    return string.IsNullOrWhiteSpace(text) ? DefaultRefusal(trimmed) : text;
                }
            }
        }

        return null;
    }

    /// <summary>"Only users with all devices can add or remove the tag X. Nothing was changed."</summary>
    public static string DefaultRefusal(string name) => $"Only users with all devices can add or remove the tag {name}. Nothing was changed.";

    private void Set(OwnerTags view, IReadOnlyCollection<string> names, Func<string, string> refusal)
    {
        var set = new ProtectedSet(
            new HashSet<string>(names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase),
            refusal);
        lock (_gate)
        {
            if (!view.Closed && _owners.TryGetValue(view.OwnerId, out var owner))
            {
                _owners[view.OwnerId] = owner with { Set = set };
            }
        }
    }

    private sealed record ProtectedSet(HashSet<string> Names, Func<string, string> Refusal);

    private sealed record Owner(List<OwnerTags> Views, ProtectedSet? Set);

    private sealed class OwnerTags(ProtectedTagRegistry registry, string ownerId) : IProtectedTags
    {
        public string OwnerId => ownerId;

        /// <summary>The plugin stopped (set under the registry's lock).</summary>
        public bool Closed { get; set; }

        public void Protect(IReadOnlyCollection<string> names, Func<string, string> refusal)
        {
            ArgumentNullException.ThrowIfNull(names);
            ArgumentNullException.ThrowIfNull(refusal);
            registry.Set(this, names, refusal);
        }
    }
}
