namespace Oadm.Sdk.Network;

/// <summary>Limits of <see cref="KeyedRateLimiter{TKey}"/>.</summary>
public sealed record KeyedRateLimitOptions
{
    /// <summary>Requests a client may send back to back.</summary>
    public int Burst { get; init; } = 8;

    /// <summary>One request token is added every this many seconds.</summary>
    public double RefillSeconds { get; init; } = 2;

    /// <summary>Most clients tracked; the least recently seen one is forgotten first.</summary>
    public int MaxClients { get; init; } = 10_000;

    /// <summary>Clients not seen for this long are forgotten.</summary>
    public TimeSpan IdleExpiry { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Requests per second over all clients; beyond that everything is dropped.</summary>
    public int GlobalPerSecond { get; init; } = 2_000;

    /// <summary>
    /// Per-client notices while the client is over its limit, each at most once per its interval (e.g. NTP: one
    /// Kiss-o'-Death per minute, one log entry per minute). Up to 8.
    /// </summary>
    public IReadOnlyList<TimeSpan> NoticeIntervals { get; init; } = [];
}

public enum RateLimitDecision
{
    /// <summary>Handle the request.</summary>
    Allow = 0,

    /// <summary>Over the client's limit: drop.</summary>
    Drop = 1,

    /// <summary>Over the global limit: drop.</summary>
    GlobalDrop = 2,
}

/// <param name="Decision">What to do with the request.</param>
/// <param name="DueNotices">Bit i set: notice i (<see cref="KeyedRateLimitOptions.NoticeIntervals"/>) is due now.</param>
public readonly record struct RateLimitResult(RateLimitDecision Decision, int DueNotices)
{
    public bool IsDue(int notice) => (DueNotices & (1 << notice)) != 0;
}

/// <summary>
/// Token buckets per client key (address, MAC) in a bounded LRU table plus one global bucket, shared by the network
/// service plugins (NTP, DHCP). Thread safe; no allocations in the steady state (table nodes are reused once the table is
/// full or entries expire). Floods of new keys only ever cost one table entry each, never more than
/// <see cref="KeyedRateLimitOptions.MaxClients"/> entries.
/// </summary>
public sealed class KeyedRateLimiter<TKey>
    where TKey : notnull
{
    private readonly KeyedRateLimitOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();
    private readonly Dictionary<TKey, LinkedListNode<Client>> _clients = [];
    private readonly LinkedList<Client> _lru = new(); // first = most recently seen
    private readonly Stack<LinkedListNode<Client>> _free = new();
    private double _globalTokens;
    private long _globalRefill;
    private long _lastGlobalDrop;

    public KeyedRateLimiter(KeyedRateLimitOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new KeyedRateLimitOptions();
        if (_options.NoticeIntervals.Count > 8)
        {
            throw new ArgumentException("At most 8 notices.", nameof(options));
        }

        _time = time ?? TimeProvider.System;
        _globalTokens = _options.GlobalPerSecond;
        _globalRefill = _time.GetTimestamp();
    }

    public KeyedRateLimitOptions Options => _options;

    /// <summary>Clients currently tracked (bounded by <see cref="KeyedRateLimitOptions.MaxClients"/>).</summary>
    public int ClientCount
    {
        get
        {
            lock (_sync)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>The global limit dropped a request within <paramref name="window"/>.</summary>
    public bool GloballyLimitedWithin(TimeSpan window)
    {
        var last = Interlocked.Read(ref _lastGlobalDrop);
        return last != 0 && _time.GetElapsedTime(last) <= window;
    }

    public RateLimitResult Check(TKey client)
    {
        var now = _time.GetTimestamp();
        lock (_sync)
        {
            // Global bucket first: a flood must not even touch the client table.
            _globalTokens = Math.Min(_options.GlobalPerSecond, _globalTokens + (Seconds(_globalRefill, now) * _options.GlobalPerSecond));
            _globalRefill = now;
            if (_globalTokens < 1)
            {
                Interlocked.Exchange(ref _lastGlobalDrop, now);
                return new RateLimitResult(RateLimitDecision.GlobalDrop, 0);
            }

            _globalTokens -= 1;

            ExpireIdle(now);
            var entry = Touch(client, now);
            entry.Tokens = Math.Min(_options.Burst, entry.Tokens + (Seconds(entry.LastRefill, now) / _options.RefillSeconds));
            entry.LastRefill = now;
            if (entry.Tokens >= 1)
            {
                entry.Tokens -= 1;
                return new RateLimitResult(RateLimitDecision.Allow, 0);
            }

            var due = 0;
            for (var i = 0; i < _options.NoticeIntervals.Count; i++)
            {
                var last = entry.LastNotice[i];
                if (last == 0 || Seconds(last, now) >= _options.NoticeIntervals[i].TotalSeconds)
                {
                    entry.LastNotice[i] = now;
                    due |= 1 << i;
                }
            }

            return new RateLimitResult(RateLimitDecision.Drop, due);
        }
    }

    private Client Touch(TKey key, long now)
    {
        if (_clients.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            node.Value.LastSeen = now;
            return node.Value;
        }

        if (_clients.Count >= _options.MaxClients && _lru.Last is { } oldest)
        {
            _lru.RemoveLast();
            _clients.Remove(oldest.Value.Key);
            node = oldest;
        }
        else
        {
            node = _free.Count > 0 ? _free.Pop() : new LinkedListNode<Client>(new Client(_options.NoticeIntervals.Count));
        }

        node.Value.Reset(key, _options.Burst, now);
        _lru.AddFirst(node);
        _clients[key] = node;
        return node.Value;
    }

    private void ExpireIdle(long now)
    {
        var idle = _options.IdleExpiry.TotalSeconds;
        while (_lru.Last is { } last && Seconds(last.Value.LastSeen, now) >= idle)
        {
            _lru.RemoveLast();
            _clients.Remove(last.Value.Key);
            _free.Push(last);
        }
    }

    private double Seconds(long from, long to) => _time.GetElapsedTime(from, to).TotalSeconds;

    private sealed class Client(int notices)
    {
        public TKey Key = default!;
        public double Tokens;
        public long LastRefill;
        public long LastSeen;
        public readonly long[] LastNotice = new long[notices];

        public void Reset(TKey key, double tokens, long now)
        {
            Key = key;
            Tokens = tokens;
            LastRefill = now;
            LastSeen = now;
            Array.Clear(LastNotice);
        }
    }
}
