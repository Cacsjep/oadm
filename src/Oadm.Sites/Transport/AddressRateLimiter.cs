using System.Net;
using System.Net.Sockets;

namespace Oadm.Sites.Transport;

/// <summary>
/// Counts attempts per source address and in all within a fixed window (docs/spec/sites.md, Security review): the site port of
/// the server before TLS, the pairing calls of the node. IPv6 addresses count per /64 (one host owns a whole /64). At most
/// <c>maxAddresses</c> are kept: entries older than the window go first, and when the table is full the address whose
/// window started first makes room (a flood from many addresses never locks out a new one). Every call is O(1) apart from
/// the stale entries it drops, so memory and the time under the lock stay bounded.
/// </summary>
public sealed class AddressRateLimiter
{
    private readonly int _perAddress;
    private readonly int _total;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly int _maxAddresses;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<Bucket>> _buckets = new(StringComparer.Ordinal);
    private readonly LinkedList<Bucket> _byStart = new(); // oldest window start first
    private long _totalStart;
    private int _totalCount;

    /// <param name="perAddress">Attempts per address in a window.</param>
    /// <param name="total">Attempts from all addresses in a window; 0 = no total limit.</param>
    public AddressRateLimiter(int perAddress, int total, TimeSpan window, TimeProvider time, int maxAddresses = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perAddress);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAddresses);
        ArgumentNullException.ThrowIfNull(time);
        _perAddress = perAddress;
        _total = total;
        _window = window;
        _time = time;
        _maxAddresses = maxAddresses;
        _totalStart = time.GetTimestamp();
    }

    /// <summary>Addresses kept now (tests).</summary>
    public int Addresses
    {
        get
        {
            lock (_lock)
                return _buckets.Count;
        }
    }

    /// <summary>"10.0.0.5"; IPv6 by its /64: "2001:db8:1:2::/64"; IPv4-mapped addresses as IPv4.</summary>
    public static string KeyOf(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>Counts one attempt from <paramref name="address"/>; false when it is over a limit (nothing counted).</summary>
    public bool TryAcquire(IPAddress? address) => TryAcquire(address is null ? "?" : KeyOf(address));

    /// <summary>Counts one attempt from the source <paramref name="key"/> (<see cref="KeyOf"/>).</summary>
    public bool TryAcquire(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var now = _time.GetTimestamp();
        lock (_lock)
        {
            // Windows that ended: their addresses start again from nothing, so they can go.
            while (_byStart.First is { } oldest && Ended(oldest.Value.Start, now))
            {
                _buckets.Remove(oldest.Value.Key);
                _byStart.RemoveFirst();
            }

            if (Ended(_totalStart, now))
            {
                _totalStart = now;
                _totalCount = 0;
            }

            if (_total > 0 && _totalCount >= _total)
                return false;
            if (!_buckets.TryGetValue(key, out var node))
            {
                if (_buckets.Count >= _maxAddresses && _byStart.First is { } first)
                {
                    _buckets.Remove(first.Value.Key);
                    _byStart.RemoveFirst();
                }

                node = _byStart.AddLast(new Bucket(key) { Start = now });
                _buckets[key] = node;
            }

            if (node.Value.Count >= _perAddress)
                return false;
            node.Value.Count++;
            _totalCount++;
            return true;
        }
    }

    private bool Ended(long start, long now) => _time.GetElapsedTime(start, now) >= _window;

    private sealed class Bucket(string key)
    {
        public string Key { get; } = key;

        public long Start;
        public int Count;
    }
}
