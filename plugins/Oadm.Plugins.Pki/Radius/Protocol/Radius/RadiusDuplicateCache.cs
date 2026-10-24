using System.Net;
using System.Security.Cryptography;

namespace Oadm.Plugins.Radius.Protocol.Radius;

/// <summary>What the duplicate cache says about a request.</summary>
public enum DuplicateState
{
    /// <summary>A new request: process it, then <see cref="RadiusDuplicateCache.Complete"/>.</summary>
    New,
    /// <summary>A retransmission whose answer is not ready yet: drop it (RFC 5080 2.2.2 "MUST silently discard").</summary>
    InProgress,
    /// <summary>A retransmission with an answer: send the cached answer again, never a second step.</summary>
    Answered,
}

/// <summary>The cache's verdict; <see cref="Response"/> is set for <see cref="DuplicateState.Answered"/>.</summary>
public readonly record struct DuplicateCheck(DuplicateState State, byte[]? Response);

/// <summary>
/// Duplicate detection with cached answers (RFC 5080 2.2.2, RFC 2865 3 "Identifier"): a request is a duplicate when the same
/// switch sends it from the same address and port with the same Identifier and Request Authenticator. Its cached answer is
/// sent again; one still in work is dropped. A request with the same switch, address, port and Identifier but another
/// authenticator replaces the entry (RFC 5080 2.2.2 "MUST also be purged"). Only requests whose Message-Authenticator was
/// verified reach the cache. At most <see cref="Capacity"/> entries and <see cref="MaxBytes"/> of answers (the oldest
/// answered ones go first; a request still in work is never evicted), each kept for <see cref="Lifetime"/> (30 s, the RFC's
/// upper bound). Separately, the final answer of every ended session (Access-Accept or Access-Reject) is kept for the same
/// time by switch and State (<see cref="RememberFinal"/>, <see cref="FindFinal"/>), so a retransmission of the last request
/// gets the same answer even when the busy cache evicted it (RFC 5080 2.2.2). Thread-safe.
/// </summary>
public sealed class RadiusDuplicateCache
{
    /// <summary>The default capacity (docs/spec/radius-server.md "Duplicates"): twice the open sessions allowed.</summary>
    public const int DefaultCapacity = 20_000;

    /// <summary>The default bytes of cached answers (about 20,000 answers of a fragment each).</summary>
    public const long DefaultMaxBytes = 32L * 1024 * 1024;

    /// <summary>Final answers of ended sessions kept at most (more than 30,000 devices ending within 30 s).</summary>
    public const int FinalCapacity = 65_536;

    /// <summary>The default lifetime of an entry (RFC 5080 2.2.2: 5..30 s).</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly Dictionary<Key, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _byAge = new();
    private readonly Dictionary<FinalKey, LinkedListNode<Final>> _finals = [];
    private readonly LinkedList<Final> _finalsByAge = new();
    private readonly Lock _lock = new();
    private long _bytes;

    public RadiusDuplicateCache(TimeProvider time, int capacity = DefaultCapacity, TimeSpan? lifetime = null, long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _time = time;
        Capacity = capacity;
        MaxBytes = maxBytes;
        Lifetime = lifetime ?? DefaultLifetime;
    }

    public int Capacity { get; }

    /// <summary>The most bytes of cached answers.</summary>
    public long MaxBytes { get; }

    /// <summary>Final answers of ended sessions held now.</summary>
    public int FinalCount
    {
        get
        {
            lock (_lock)
            {
                return _finals.Count;
            }
        }
    }

    public TimeSpan Lifetime { get; }

    /// <summary>Entries held now (expired ones included until the next call removes them).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Looks up a verified request and, when it is new, marks it as in work.</summary>
    public DuplicateCheck Begin(string client, IPEndPoint source, byte identifier, ReadOnlySpan<byte> authenticator)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(source);
        var key = new Key(client, source, identifier);
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            Expire(now);
            if (_entries.TryGetValue(key, out var node))
            {
                if (CryptographicOperations.FixedTimeEquals(node.Value.Authenticator, authenticator))
                {
                    return node.Value.Response is { } response ? new DuplicateCheck(DuplicateState.Answered, response) : new DuplicateCheck(DuplicateState.InProgress, null);
                }

                Remove(node);
            }

            if (_entries.Count >= Capacity)
            {
                EvictAnswered(Capacity - 1, MaxBytes);
            }

            var entry = new Entry(key, authenticator.ToArray(), now + Lifetime);
            _entries[key] = _byAge.AddLast(entry);
            return new DuplicateCheck(DuplicateState.New, null);
        }
    }

    /// <summary>
    /// Stores the answer of a request from <see cref="Begin"/>; null (the request was dropped) forgets the entry, so a
    /// retransmission is processed again.
    /// </summary>
    public void Complete(string client, IPEndPoint source, byte identifier, ReadOnlySpan<byte> authenticator, byte[]? response)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(source);
        var key = new Key(client, source, identifier);
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var node) || !CryptographicOperations.FixedTimeEquals(node.Value.Authenticator, authenticator))
            {
                return;
            }

            if (response is null)
            {
                Remove(node);
            }
            else if (node.Value.Response is null)
            {
                node.Value.Response = response;
                _bytes += response.Length;
                if (_bytes > MaxBytes)
                {
                    EvictAnswered(Capacity, MaxBytes);
                }
            }
        }
    }

    /// <summary>
    /// Keeps the answer that ended a session (Access-Accept or Access-Reject) for <see cref="Lifetime"/>: a retransmission of
    /// that request (same switch, State and Request Authenticator) gets it again instead of "Session not found".
    /// </summary>
    public void RememberFinal(string client, ReadOnlySpan<byte> state, ReadOnlySpan<byte> authenticator, byte[] response)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(response);
        var key = new FinalKey(client, Convert.ToHexString(state));
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            ExpireFinals(now);
            if (_finals.Remove(key, out var old))
            {
                _finalsByAge.Remove(old);
            }

            while (_finals.Count >= FinalCapacity && _finalsByAge.First is { } oldest)
            {
                _finals.Remove(oldest.Value.Key);
                _finalsByAge.RemoveFirst();
            }

            _finals[key] = _finalsByAge.AddLast(new Final(key, authenticator.ToArray(), response, now + Lifetime));
        }
    }

    /// <summary>The final answer of an ended session for a retransmission of its last request, else null.</summary>
    public byte[]? FindFinal(string client, ReadOnlySpan<byte> state, ReadOnlySpan<byte> authenticator)
    {
        ArgumentNullException.ThrowIfNull(client);
        var key = new FinalKey(client, Convert.ToHexString(state));
        lock (_lock)
        {
            ExpireFinals(_time.GetUtcNow());
            return _finals.TryGetValue(key, out var node) && CryptographicOperations.FixedTimeEquals(node.Value.Authenticator, authenticator)
                ? node.Value.Response
                : null;
        }
    }

    /// <summary>Forgets every entry of <paramref name="client"/> (a removed switch).</summary>
    public void Forget(string client)
    {
        lock (_lock)
        {
            for (var node = _byAge.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.Key.Client == client)
                {
                    Remove(node);
                }

                node = next;
            }

            for (var node = _finalsByAge.First; node is not null;)
            {
                var next = node.Next;
                if (node.Value.Key.Client == client)
                {
                    _finals.Remove(node.Value.Key);
                    _finalsByAge.Remove(node);
                }

                node = next;
            }
        }
    }

    /// <summary>Drops the oldest answered entries until at most <paramref name="count"/> entries and <paramref name="bytes"/> remain; requests in work stay.</summary>
    private void EvictAnswered(int count, long bytes)
    {
        for (var node = _byAge.First; node is not null && (_entries.Count > count || _bytes > bytes);)
        {
            var next = node.Next;
            if (node.Value.Response is not null)
            {
                Remove(node);
            }

            node = next;
        }
    }

    private void ExpireFinals(DateTimeOffset now)
    {
        while (_finalsByAge.First is { } oldest && oldest.Value.Expires <= now)
        {
            _finals.Remove(oldest.Value.Key);
            _finalsByAge.RemoveFirst();
        }
    }

    private void Expire(DateTimeOffset now)
    {
        while (_byAge.First is { } oldest && oldest.Value.Expires <= now)
        {
            Remove(oldest);
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _entries.Remove(node.Value.Key);
        _byAge.Remove(node);
        _bytes -= node.Value.Response?.Length ?? 0;
    }

    private readonly record struct Key(string Client, IPEndPoint Source, byte Identifier);

    private readonly record struct FinalKey(string Client, string State);

    private sealed record Final(FinalKey Key, byte[] Authenticator, byte[] Response, DateTimeOffset Expires);

    private sealed class Entry(Key key, byte[] authenticator, DateTimeOffset expires)
    {
        public Key Key { get; } = key;

        public byte[] Authenticator { get; } = authenticator;

        public DateTimeOffset Expires { get; } = expires;

        public byte[]? Response { get; set; }
    }
}
