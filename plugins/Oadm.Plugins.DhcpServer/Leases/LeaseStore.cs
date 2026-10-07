using Oadm.Plugins.DhcpServer.Protocol;

namespace Oadm.Plugins.DhcpServer.Leases;

public enum LeaseState
{
    /// <summary>Static lease not handed out yet.</summary>
    Reserved = 0,

    /// <summary>Offered, waiting for the client's request (dynamic leases only; hidden in the list).</summary>
    Offered = 1,

    /// <summary>Handed out and not expired.</summary>
    Bound = 2,

    Expired = 3,

    /// <summary>The client gave the address back.</summary>
    Released = 4,
}

/// <summary>One lease (static reservation or dynamic lease). Mutated only under the store lock.</summary>
public sealed class Lease
{
    public ulong Mac { get; internal set; }

    public uint Address { get; internal set; }

    public bool IsStatic { get; internal set; }

    public LeaseState State { get; internal set; }

    /// <summary>End of a bound lease.</summary>
    public DateTime? ExpiresUtc { get; internal set; }

    /// <summary>A pending offer ends at this time (null = none).</summary>
    public DateTime? OfferExpiresUtc { get; internal set; }

    /// <summary>State before the pending offer (null = the lease exists only because of the offer).</summary>
    internal LeaseState? StateBeforeOffer { get; set; }

    public string? HostName { get; internal set; }

    /// <summary>Name of a static lease.</summary>
    public string? Name { get; internal set; }

    public bool IsVisible => IsStatic || State != LeaseState.Offered;

    public LeaseInfo ToInfo() => new(
        MacAddress.Format(Mac),
        Ip4.Format(Address),
        HostName,
        Name,
        IsStatic,
        State switch
        {
            LeaseState.Bound => LeaseInfo.Active,
            LeaseState.Expired => LeaseInfo.Expired,
            LeaseState.Released => LeaseInfo.Released,
            LeaseState.Offered => LeaseInfo.Reserved,
            _ => LeaseInfo.Reserved,
        },
        State == LeaseState.Bound ? ExpiresUtc : null);
}

/// <summary>The dynamic range and the addresses inside it that are never handed out.</summary>
/// <param name="Start">First address.</param>
/// <param name="End">Last address.</param>
/// <param name="Excluded">Server, router and DNS addresses.</param>
public sealed record AddressPool(uint Start, uint End, IReadOnlySet<uint> Excluded)
{
    public long Size => (long)End - Start + 1;

    public bool Contains(uint address) => address >= Start && address <= End;
}

/// <summary>Stored form of a lease (plugin setting <c>leases</c>).</summary>
public sealed record StoredLease(string Mac, string Address, bool IsStatic, string State, DateTime? ExpiresUtc, string? HostName, string? Name);

/// <summary>
/// All leases in memory, indexed by MAC and by address, plus addresses marked as conflicts (declined by a client or
/// answering the probe). Thread safe (one lock, no I/O inside). Static leases always win: a static address is never
/// handed to another device, and a device with a static lease always gets that address. Changes are counted
/// (<see cref="Version"/>) and collected for the page events and the persistence.
/// </summary>
public sealed class LeaseStore
{
    private readonly Lock _sync = new();
    private readonly Dictionary<ulong, Lease> _byMac = [];
    private readonly Dictionary<uint, Lease> _byAddress = [];
    private readonly Dictionary<uint, DateTime> _conflicts = [];
    private readonly HashSet<ulong> _pendingOffers = [];
    private readonly HashSet<ulong> _changed = [];
    private readonly HashSet<ulong> _removed = [];
    private long _version;
    private bool _dirty;
    private uint _cursor;
    private (AddressPool? Pool, long Version) _noFreeAddress;

    public long Version
    {
        get
        {
            lock (_sync)
            {
                return _version;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _byMac.Count;
            }
        }
    }

    public int PendingOffers
    {
        get
        {
            lock (_sync)
            {
                return _pendingOffers.Count;
            }
        }
    }

    /// <summary>A copy of the lease of a MAC (null when none).</summary>
    public LeaseInfo? Find(ulong mac)
    {
        lock (_sync)
        {
            return _byMac.TryGetValue(mac, out var lease) ? lease.ToInfo() : null;
        }
    }

    public bool IsConflict(uint address, DateTime now)
    {
        lock (_sync)
        {
            return _conflicts.TryGetValue(address, out var until) && until > now;
        }
    }

    /// <summary>Visible leases (static and dynamic, no pending offers).</summary>
    public IReadOnlyList<LeaseInfo> Snapshot(out long version)
    {
        lock (_sync)
        {
            version = _version;
            return [.. _byMac.Values.Where(l => l.IsVisible).Select(l => l.ToInfo())];
        }
    }

    /// <summary>Changes since the last call, for the page event (null when nothing changed).</summary>
    public LeasesEvent? TakeChanges()
    {
        lock (_sync)
        {
            if (_changed.Count == 0 && _removed.Count == 0)
            {
                return null;
            }

            var changed = new List<LeaseInfo>(_changed.Count);
            var removed = new List<string>(_removed.Count);
            foreach (var mac in _changed)
            {
                if (_byMac.TryGetValue(mac, out var lease) && lease.IsVisible)
                {
                    changed.Add(lease.ToInfo());
                }
                else
                {
                    removed.Add(MacAddress.Format(mac));
                }
            }

            removed.AddRange(_removed.Where(m => !_changed.Contains(m)).Select(MacAddress.Format));
            _changed.Clear();
            _removed.Clear();
            return new LeasesEvent(_version, changed, removed);
        }
    }

    /// <summary>The leases to store when something changed since the last call (offers are not stored), else null.</summary>
    public IReadOnlyList<StoredLease>? TakeDirty()
    {
        lock (_sync)
        {
            if (!_dirty)
            {
                return null;
            }

            _dirty = false;
            return [.. _byMac.Values.Where(l => l.IsStatic || l.State is LeaseState.Bound or LeaseState.Expired or LeaseState.Released)
                .Select(l => new StoredLease(
                    MacAddress.Format(l.Mac),
                    Ip4.Format(l.Address),
                    l.IsStatic,
                    (l.State == LeaseState.Offered ? l.StateBeforeOffer ?? LeaseState.Reserved : l.State).ToString(),
                    l.ExpiresUtc,
                    l.HostName,
                    l.Name))];
        }
    }

    /// <summary>Loads stored leases (server start); unreadable entries are skipped.</summary>
    public int Load(IEnumerable<StoredLease> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var count = 0;
        lock (_sync)
        {
            foreach (var s in stored)
            {
                if (!MacAddress.TryParse(s.Mac, out var mac) || !Ip4.TryParse(s.Address, out var address)
                    || _byMac.ContainsKey(mac) || _byAddress.ContainsKey(address) || !Enum.TryParse<LeaseState>(s.State, out var state)
                    || state == LeaseState.Offered)
                {
                    continue;
                }

                Put(new Lease { Mac = mac, Address = address, IsStatic = s.IsStatic, State = state, ExpiresUtc = s.ExpiresUtc, HostName = s.HostName, Name = s.Name });
                count++;
            }

            _changed.Clear();
            _dirty = false;
        }

        return count;
    }

    /// <summary>
    /// Chooses the address to offer a client: its static lease, its previous lease, the address it asks for, then the next
    /// free one, then the oldest expired one. The choice is held as a pending offer until <paramref name="offerExpires"/>.
    /// <paramref name="needsProbe"/> is true for an address the client did not have before (check it is not in use).
    /// Null when the pool is exhausted or too many offers are pending (<paramref name="maxPendingOffers"/>).
    /// </summary>
    public uint? ReserveOffer(ulong mac, uint? requested, AddressPool pool, DateTime now, DateTime offerExpires, int maxPendingOffers, string? hostName, out bool needsProbe)
    {
        ArgumentNullException.ThrowIfNull(pool);
        needsProbe = false;
        lock (_sync)
        {
            if (_byMac.TryGetValue(mac, out var existing))
            {
                if (existing.IsStatic)
                {
                    MarkOffer(existing, offerExpires, hostName);
                    return existing.Address;
                }

                if (existing.OfferExpiresUtc > now)
                {
                    return existing.Address; // one pending offer per client: the same one again
                }

                if (pool.Contains(existing.Address) && !pool.Excluded.Contains(existing.Address) && !IsConflictLocked(existing.Address, now))
                {
                    MarkOffer(existing, offerExpires, hostName);
                    return existing.Address;
                }

                Delete(existing); // its address left the range or is a conflict: start over
            }

            if (_pendingOffers.Count >= maxPendingOffers)
            {
                return null;
            }

            uint? chosen = null;
            if (requested is { } r && IsFree(r, pool, now))
            {
                chosen = r;
            }

            chosen ??= NextFree(pool, now);
            if (chosen is null && OldestReclaimable(pool, now) is { } old)
            {
                Delete(old);
                chosen = old.Address;
            }

            if (chosen is not { } address)
            {
                return null;
            }

            var lease = new Lease { Mac = mac, Address = address, State = LeaseState.Offered, HostName = hostName };
            Put(lease);
            lease.OfferExpiresUtc = offerExpires;
            lease.StateBeforeOffer = null;
            _pendingOffers.Add(mac);
            needsProbe = true;
            return address;
        }
    }

    /// <summary>The probe found the address in use: mark it as a conflict and drop the offer.</summary>
    public void RejectOffer(ulong mac, uint address, DateTime conflictUntil)
    {
        lock (_sync)
        {
            _conflicts[address] = conflictUntil;
            if (_byMac.TryGetValue(mac, out var lease) && lease.Address == address && !lease.IsStatic)
            {
                Delete(lease);
            }
        }
    }

    /// <summary>The client took another server's offer: forget ours.</summary>
    public void WithdrawOffer(ulong mac)
    {
        lock (_sync)
        {
            if (_byMac.TryGetValue(mac, out var lease) && lease.OfferExpiresUtc is not null)
            {
                EndOffer(lease);
            }
        }
    }

    /// <summary>
    /// A request for <paramref name="address"/>: true (and the lease is bound until <paramref name="expires"/>) when the
    /// client may have it: its static address, or its own lease or offer of that address, or (<paramref name="allowNew"/>)
    /// a free address of the pool.
    /// </summary>
    public bool TryBind(ulong mac, uint address, AddressPool pool, DateTime now, DateTime expires, string? hostName, bool allowNew)
    {
        ArgumentNullException.ThrowIfNull(pool);
        lock (_sync)
        {
            if (_byMac.TryGetValue(mac, out var lease))
            {
                if (lease.Address != address)
                {
                    return false;
                }

                if (!lease.IsStatic && (!pool.Contains(address) || IsConflictLocked(address, now)))
                {
                    return false;
                }

                Bind(lease, expires, hostName);
                return true;
            }

            if (!allowNew || !IsFree(address, pool, now))
            {
                return false;
            }

            var created = new Lease { Mac = mac, Address = address };
            Put(created);
            Bind(created, expires, hostName);
            return true;
        }
    }

    /// <summary>The client knows another address than we have for it, or the address belongs to someone else.</summary>
    public bool ConflictsWith(ulong mac, uint address)
    {
        lock (_sync)
        {
            return (_byMac.TryGetValue(mac, out var mine) && mine.Address != address)
                || (_byAddress.TryGetValue(address, out var other) && other.Mac != mac);
        }
    }

    /// <summary>True when the server has any lease for this MAC.</summary>
    public bool Knows(ulong mac)
    {
        lock (_sync)
        {
            return _byMac.ContainsKey(mac);
        }
    }

    /// <summary>The client found the address in use (DECLINE): conflict, lease dropped (a static lease stays).</summary>
    public void Decline(ulong mac, uint address, DateTime conflictUntil)
    {
        lock (_sync)
        {
            if (_byMac.TryGetValue(mac, out var lease) && lease.Address == address)
            {
                if (lease.IsStatic)
                {
                    lease.State = LeaseState.Reserved;
                    lease.ExpiresUtc = null;
                    Changed(lease);
                    return; // the user decided: never take the reservation away
                }

                Delete(lease);
            }

            _conflicts[address] = conflictUntil;
        }
    }

    /// <summary>The client gave the address back (RELEASE). The record is kept so the client gets it again later.</summary>
    public bool Release(ulong mac, uint address)
    {
        lock (_sync)
        {
            if (!_byMac.TryGetValue(mac, out var lease) || lease.Address != address || lease.State != LeaseState.Bound)
            {
                return false;
            }

            lease.State = lease.IsStatic ? LeaseState.Reserved : LeaseState.Released;
            lease.ExpiresUtc = null;
            Changed(lease);
            return true;
        }
    }

    /// <summary>Page "Release": forgets a dynamic lease (the address is free; the device asks again at its next renewal).</summary>
    public bool Forget(ulong mac)
    {
        lock (_sync)
        {
            if (!_byMac.TryGetValue(mac, out var lease) || lease.IsStatic)
            {
                return false;
            }

            Delete(lease);
            return true;
        }
    }

    /// <summary>
    /// Adds or edits a static lease. Errors per field ("Mac", "Address") when the address is reserved for another device
    /// or used by another device's active lease, or the device already has a static lease. A dynamic lease of the same
    /// device is replaced; an expired or released lease of another device at that address is removed.
    /// </summary>
    public Dictionary<string, string> SetStatic(ulong mac, uint address, string? name, ulong? originalMac, DateTime now)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_sync)
        {
            if (originalMac is { } original && (!_byMac.TryGetValue(original, out var edited) || !edited.IsStatic))
            {
                errors["Mac"] = "This static lease no longer exists.";
                return errors;
            }

            if (_byMac.TryGetValue(mac, out var own) && own.IsStatic && mac != originalMac)
            {
                errors["Mac"] = $"This device already has a static lease ({Ip4.Format(own.Address)}).";
            }

            if (_byAddress.TryGetValue(address, out var holder) && holder.Mac != mac && holder.Mac != originalMac)
            {
                if (holder.IsStatic)
                {
                    errors["Address"] = $"Already reserved for {MacAddress.Format(holder.Mac)}.";
                }
                else if (holder.State == LeaseState.Bound || holder.OfferExpiresUtc > now)
                {
                    errors["Address"] = $"In use by {MacAddress.Format(holder.Mac)}.";
                }
            }

            if (errors.Count > 0)
            {
                return errors;
            }

            if (originalMac is { } o && _byMac.TryGetValue(o, out var old))
            {
                Delete(old);
            }

            if (_byAddress.TryGetValue(address, out var stale) && stale.Mac != mac)
            {
                Delete(stale);
            }

            if (_byMac.TryGetValue(mac, out var current))
            {
                var keepBound = current.Address == address && current.State == LeaseState.Bound;
                var hostName = current.HostName;
                var expires = current.ExpiresUtc;
                Delete(current);
                Put(new Lease { Mac = mac, Address = address, IsStatic = true, State = keepBound ? LeaseState.Bound : LeaseState.Reserved, ExpiresUtc = keepBound ? expires : null, HostName = hostName, Name = name });
            }
            else
            {
                Put(new Lease { Mac = mac, Address = address, IsStatic = true, State = LeaseState.Reserved, Name = name });
            }

            _conflicts.Remove(address);
        }

        return errors;
    }

    /// <summary>"Make static": the dynamic lease becomes a static one (same address), named by its host name.</summary>
    public bool MakeStatic(ulong mac)
    {
        lock (_sync)
        {
            if (!_byMac.TryGetValue(mac, out var lease) || lease.IsStatic)
            {
                return false;
            }

            if (lease.OfferExpiresUtc is not null)
            {
                EndOffer(lease);
                if (!_byMac.ContainsKey(mac))
                {
                    return false;
                }
            }

            lease.IsStatic = true;
            lease.Name ??= lease.HostName;
            if (lease.State is LeaseState.Expired or LeaseState.Released)
            {
                lease.State = LeaseState.Reserved;
                lease.ExpiresUtc = null;
            }

            Changed(lease);
            return true;
        }
    }

    public bool DeleteStatic(ulong mac)
    {
        lock (_sync)
        {
            if (!_byMac.TryGetValue(mac, out var lease) || !lease.IsStatic)
            {
                return false;
            }

            Delete(lease);
            return true;
        }
    }

    /// <summary>Ends pending offers older than their expiry, expires bound leases, forgets old conflicts.</summary>
    public void Sweep(DateTime now)
    {
        lock (_sync)
        {
            foreach (var mac in _pendingOffers.ToList())
            {
                if (_byMac.TryGetValue(mac, out var lease) && lease.OfferExpiresUtc <= now)
                {
                    EndOffer(lease);
                }
            }

            foreach (var lease in _byMac.Values)
            {
                if (lease.State == LeaseState.Bound && lease.ExpiresUtc <= now)
                {
                    lease.State = lease.IsStatic ? LeaseState.Reserved : LeaseState.Expired;
                    if (lease.IsStatic)
                    {
                        lease.ExpiresUtc = null;
                    }

                    Changed(lease);
                }
            }

            foreach (var address in _conflicts.Where(c => c.Value <= now).Select(c => c.Key).ToList())
            {
                _conflicts.Remove(address);
                _version++; // a conflict ended: the address may be handed out again
            }
        }
    }

    /// <summary>Free addresses of the pool right now (for the "Address pool exhausted" status).</summary>
    public bool HasFreeAddress(AddressPool pool, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(pool);
        lock (_sync)
        {
            return NextFree(pool, now, moveCursor: false) is not null || OldestReclaimable(pool, now) is not null;
        }
    }

    private bool IsConflictLocked(uint address, DateTime now) => _conflicts.TryGetValue(address, out var until) && until > now;

    private bool IsFree(uint address, AddressPool pool, DateTime now) =>
        pool.Contains(address) && !pool.Excluded.Contains(address) && !_byAddress.ContainsKey(address) && !IsConflictLocked(address, now);

    private uint? NextFree(AddressPool pool, DateTime now, bool moveCursor = true)
    {
        if (ReferenceEquals(_noFreeAddress.Pool, pool) && _noFreeAddress.Version == _version)
        {
            return null; // nothing changed since the last full scan found nothing
        }

        var size = pool.Size;
        var start = pool.Contains(_cursor) ? _cursor : pool.Start;
        for (long i = 0; i < size; i++)
        {
            var candidate = (uint)(pool.Start + (((long)start - pool.Start + i) % size));
            if (IsFree(candidate, pool, now))
            {
                if (moveCursor)
                {
                    _cursor = candidate == pool.End ? pool.Start : candidate + 1;
                }

                return candidate;
            }
        }

        _noFreeAddress = (pool, _version);
        return null;
    }

    private Lease? OldestReclaimable(AddressPool pool, DateTime now)
    {
        Lease? oldest = null;
        foreach (var lease in _byMac.Values)
        {
            if (!lease.IsStatic && lease.State is LeaseState.Expired or LeaseState.Released && lease.OfferExpiresUtc is null
                && pool.Contains(lease.Address) && !pool.Excluded.Contains(lease.Address) && !IsConflictLocked(lease.Address, now)
                && (oldest is null || (lease.ExpiresUtc ?? DateTime.MinValue) < (oldest.ExpiresUtc ?? DateTime.MinValue)))
            {
                oldest = lease;
            }
        }

        return oldest;
    }

    private void MarkOffer(Lease lease, DateTime offerExpires, string? hostName)
    {
        if (lease.OfferExpiresUtc is null)
        {
            lease.StateBeforeOffer = lease.State;
        }

        lease.OfferExpiresUtc = offerExpires;
        if (!string.IsNullOrEmpty(hostName))
        {
            lease.HostName = hostName;
        }

        if (!lease.IsStatic && lease.State is LeaseState.Expired or LeaseState.Released)
        {
            lease.State = LeaseState.Offered;
            Changed(lease);
        }

        _pendingOffers.Add(lease.Mac);
    }

    private void EndOffer(Lease lease)
    {
        _pendingOffers.Remove(lease.Mac);
        var before = lease.StateBeforeOffer;
        lease.OfferExpiresUtc = null;
        lease.StateBeforeOffer = null;
        if (lease.State != LeaseState.Offered)
        {
            return;
        }

        if (before is null)
        {
            Delete(lease);
        }
        else
        {
            lease.State = before.Value;
            Changed(lease);
        }
    }

    private void Bind(Lease lease, DateTime expires, string? hostName)
    {
        _pendingOffers.Remove(lease.Mac);
        lease.OfferExpiresUtc = null;
        lease.StateBeforeOffer = null;
        lease.State = LeaseState.Bound;
        lease.ExpiresUtc = expires;
        if (!string.IsNullOrEmpty(hostName))
        {
            lease.HostName = hostName;
        }

        Changed(lease);
    }

    private void Put(Lease lease)
    {
        _byMac[lease.Mac] = lease;
        _byAddress[lease.Address] = lease;
        _removed.Remove(lease.Mac);
        Changed(lease);
    }

    private void Delete(Lease lease)
    {
        _byMac.Remove(lease.Mac);
        if (_byAddress.TryGetValue(lease.Address, out var holder) && ReferenceEquals(holder, lease))
        {
            _byAddress.Remove(lease.Address);
        }

        _pendingOffers.Remove(lease.Mac);
        _changed.Remove(lease.Mac);
        _removed.Add(lease.Mac);
        _version++;
        _dirty = true;
    }

    private void Changed(Lease lease)
    {
        _changed.Add(lease.Mac);
        _version++;
        _dirty = true;
    }
}
