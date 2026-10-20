using System.Net;
using System.Net.Sockets;

namespace Oadm.Sites.Transport;

/// <summary>
/// Counts attempts per source address and in all within a fixed window (docs/sites.md, Security review): the site port of
/// the server before TLS, the pairing calls of the node. IPv6 addresses count per /64 (one host owns a whole /64). Entries
/// older than the window are dropped at most once per window, and at most <c>maxAddresses</c> are kept: a flood from more
/// addresses is refused until the window ends, so memory and the time under the lock stay bounded.
/// </summary>
public sealed class AddressRateLimiter
{
    private readonly int _perAddress;
    private readonly int _total;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly int _maxAddresses;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private DateTimeOffset _totalStart;
    private int _totalCount;
    private DateTimeOffset _lastPrune;

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
        _totalStart = _lastPrune = time.GetUtcNow();
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
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (now - _lastPrune >= _window)
            {
                _lastPrune = now;
                foreach (var stale in _buckets.Where(p => now - p.Value.Start >= _window).Select(p => p.Key).ToList())
                    _buckets.Remove(stale);
            }

            if (now - _totalStart >= _window)
            {
                _totalStart = now;
                _totalCount = 0;
            }

            if (_total > 0 && _totalCount >= _total)
                return false;
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                if (_buckets.Count >= _maxAddresses)
                    return false;
                bucket = new Bucket { Start = now };
                _buckets[key] = bucket;
            }
            else if (now - bucket.Start >= _window)
            {
                bucket.Start = now;
                bucket.Count = 0;
            }

            if (bucket.Count >= _perAddress)
                return false;
            bucket.Count++;
            _totalCount++;
            return true;
        }
    }

    private sealed class Bucket
    {
        public DateTimeOffset Start;
        public int Count;
    }
}
