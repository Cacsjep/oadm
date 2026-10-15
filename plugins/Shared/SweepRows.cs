using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.Shared;

/// <summary>
/// The rows of a table page fed by a <c>DeviceSweep</c> (client side): one view model per device id, updated in place; a
/// full state, new or removed rows and a search rebuild the shown list with one reset (O(n)). Shared by the table pages
/// (Image Health Dashboard, Edge storage) as linked source.
/// </summary>
internal sealed class SweepRows<TRow, TVm>(
    Func<TRow, Guid> id,
    Func<TRow, TVm> create,
    Action<TVm, TRow> apply,
    Func<TVm, string> searchText,
    Func<TVm, string> sortKey)
    where TVm : class
{
    private readonly Dictionary<Guid, TVm> _byId = [];
    private string _search = string.Empty;
    private IReadOnlySet<Guid>? _scope;

    /// <summary>
    /// The devices of the page's choice (null = every row the server sends): the sweep is shared by every page, so a page
    /// keeps only the rows of its own devices. Setting it drops the other rows and rebuilds once.
    /// </summary>
    public IReadOnlySet<Guid>? Scope
    {
        get => _scope;
        set
        {
            _scope = value;
            if (value is not null)
            {
                foreach (var key in _byId.Keys.Where(k => !value.Contains(k)).ToList())
                {
                    _byId.Remove(key);
                }
            }

            Rebuild();
        }
    }

    /// <summary>The rows the search shows, sorted; bound to the grid.</summary>
    public RangeObservableCollection<TVm> Shown { get; } = [];

    /// <summary>Every row, also those the search hides.</summary>
    public IReadOnlyCollection<TVm> All => _byId.Values;

    public int Count => _byId.Count;

    public TVm? Find(Guid deviceId) => _byId.GetValueOrDefault(deviceId);

    /// <summary>A full state: these rows only (view models of known devices are kept).</summary>
    public void Replace(IEnumerable<TRow> rows)
    {
        var keep = new Dictionary<Guid, TVm>(_byId);
        _byId.Clear();
        foreach (var row in rows)
        {
            var key = id(row);
            if (_scope is not null && !_scope.Contains(key))
            {
                continue;
            }

            if (keep.TryGetValue(key, out var existing))
            {
                apply(existing, row);
                _byId[key] = existing;
            }
            else
            {
                _byId[key] = create(row);
            }
        }

        Rebuild();
    }

    /// <summary>Changed rows in place; new and removed rows rebuild the shown list once. True when the list changed.</summary>
    public bool Apply(IReadOnlyList<TRow> rows, IReadOnlyList<Guid>? removed)
    {
        var structural = false;
        foreach (var gone in removed ?? [])
        {
            structural |= _byId.Remove(gone);
        }

        foreach (var row in rows)
        {
            var key = id(row);
            if (_scope is not null && !_scope.Contains(key))
            {
                continue;
            }

            if (_byId.TryGetValue(key, out var existing))
            {
                apply(existing, row);
            }
            else
            {
                _byId[key] = create(row);
                structural = true;
            }
        }

        if (structural)
        {
            Rebuild();
        }

        return structural;
    }

    public void Filter(string search)
    {
        _search = search.Trim();
        Rebuild();
    }

    private void Rebuild()
    {
        IEnumerable<TVm> shown = _byId.Values;
        if (_search.Length > 0)
        {
            shown = shown.Where(r => searchText(r).Contains(_search, StringComparison.OrdinalIgnoreCase));
        }

        Shown.ReplaceAll(shown.OrderBy(sortKey, StringComparer.Ordinal));
    }
}

/// <summary>The sort key of an address column: IPv4 addresses numerically ("010.000.000.048"), host names after them.</summary>
internal static class AddressSortKeys
{
    public static string Of(string address)
    {
        var host = address.Split(':')[0];
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(p => byte.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            ? string.Join('.', parts.Select(p => p.PadLeft(3, '0')))
            : "~" + address;
    }
}
