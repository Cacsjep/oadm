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
/// verified reach the cache. At most <see cref="Capacity"/> entries (the oldest go first), each kept for
/// <see cref="Lifetime"/> (30 s, the RFC's upper bound). Thread-safe.
/// </summary>
public sealed class RadiusDuplicateCache
{
    /// <summary>The default capacity (docs/spec/radius-server.md "Duplicates").</summary>
    public const int DefaultCapacity = 4096;

    /// <summary>The default lifetime of an entry (RFC 5080 2.2.2: 5..30 s).</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly Dictionary<Key, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _byAge = new();
    private readonly Lock _lock = new();

    public RadiusDuplicateCache(TimeProvider time, int capacity = DefaultCapacity, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _time = time;
        Capacity = capacity;
        Lifetime = lifetime ?? DefaultLifetime;
    }

    public int Capacity { get; }

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

            while (_entries.Count >= Capacity)
            {
                Remove(_byAge.First!);
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
            else
            {
                node.Value.Response = response;
            }
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
    }

    private readonly record struct Key(string Client, IPEndPoint Source, byte Identifier);

    private sealed class Entry(Key key, byte[] authenticator, DateTimeOffset expires)
    {
        public Key Key { get; } = key;

        public byte[] Authenticator { get; } = authenticator;

        public DateTimeOffset Expires { get; } = expires;

        public byte[]? Response { get; set; }
    }
}
