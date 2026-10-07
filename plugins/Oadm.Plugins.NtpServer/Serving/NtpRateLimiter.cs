namespace Oadm.Plugins.NtpServer.Serving;

/// <summary>Limits of <see cref="NtpRateLimiter"/>.</summary>
public sealed record RateLimitOptions
{
    /// <summary>Requests a client may send back to back.</summary>
    public int Burst { get; init; } = 8;

    /// <summary>One request token is added every this many seconds (1 request / 2 s).</summary>
    public double RefillSeconds { get; init; } = 2;

    /// <summary>Most clients tracked; the least recently seen one is forgotten first.</summary>
    public int MaxClients { get; init; } = 10_000;

    /// <summary>Clients not seen for this long are forgotten.</summary>
    public TimeSpan IdleExpiry { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Requests per second over all clients; beyond that everything is dropped.</summary>
    public int GlobalPerSecond { get; init; } = 2_000;

    /// <summary>At most one Kiss-o'-Death "RATE" per client in this time.</summary>
    public TimeSpan KissInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>At most one "Rate limited" log entry per client in this time.</summary>
    public TimeSpan LimitedLogInterval { get; init; } = TimeSpan.FromMinutes(1);
}

public enum RateDecision
{
    /// <summary>Answer.</summary>
    Allow = 0,

    /// <summary>Over the client's limit: drop silently.</summary>
    Drop = 1,

    /// <summary>Over the client's limit: drop, but send one Kiss-o'-Death "RATE".</summary>
    DropWithKiss = 2,

    /// <summary>Over the global limit: drop.</summary>
    GlobalDrop = 3,
}

/// <param name="Decision">What to do with the request.</param>
/// <param name="Log">Write a "Rate limited" entry (rate limited itself: once per client per minute).</param>
public readonly record struct RateResult(RateDecision Decision, bool Log);

/// <summary>
/// Per-client token buckets (key = client address) in a bounded LRU table plus one global bucket. Thread safe; no
/// allocations in the steady state (table nodes are reused once the table is full or entries expire).
/// </summary>
public sealed class NtpRateLimiter
{
    private readonly RateLimitOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();
    private readonly Dictionary<UInt128, LinkedListNode<Client>> _clients = [];
    private readonly LinkedList<Client> _lru = new(); // first = most recently seen
    private readonly Stack<LinkedListNode<Client>> _free = new();
    private double _globalTokens;
    private long _globalRefill;
    private long _lastGlobalDrop;

    public NtpRateLimiter(RateLimitOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new RateLimitOptions();
        _time = time ?? TimeProvider.System;
        _globalTokens = _options.GlobalPerSecond;
        _globalRefill = _time.GetTimestamp();
    }

    public RateLimitOptions Options => _options;

    /// <summary>Clients currently tracked (bounded by <see cref="RateLimitOptions.MaxClients"/>).</summary>
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

    public RateResult Check(UInt128 client)
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
                return new RateResult(RateDecision.GlobalDrop, false);
            }

            _globalTokens -= 1;

            ExpireIdle(now);
            var entry = Touch(client, now);
            entry.Tokens = Math.Min(_options.Burst, entry.Tokens + (Seconds(entry.LastRefill, now) / _options.RefillSeconds));
            entry.LastRefill = now;
            if (entry.Tokens >= 1)
            {
                entry.Tokens -= 1;
                return new RateResult(RateDecision.Allow, false);
            }

            var kiss = entry.LastKiss == 0 || Seconds(entry.LastKiss, now) >= _options.KissInterval.TotalSeconds;
            if (kiss)
            {
                entry.LastKiss = now;
            }

            var log = entry.LastLog == 0 || Seconds(entry.LastLog, now) >= _options.LimitedLogInterval.TotalSeconds;
            if (log)
            {
                entry.LastLog = now;
            }

            return new RateResult(kiss ? RateDecision.DropWithKiss : RateDecision.Drop, log);
        }
    }

    private Client Touch(UInt128 key, long now)
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
            node = _free.Count > 0 ? _free.Pop() : new LinkedListNode<Client>(new Client());
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

    private sealed class Client
    {
        public UInt128 Key;
        public double Tokens;
        public long LastRefill;
        public long LastSeen;
        public long LastKiss;
        public long LastLog;

        public void Reset(UInt128 key, double tokens, long now)
        {
            Key = key;
            Tokens = tokens;
            LastRefill = now;
            LastSeen = now;
            LastKiss = 0;
            LastLog = 0;
        }
    }
}
