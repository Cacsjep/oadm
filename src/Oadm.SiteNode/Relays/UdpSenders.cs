using System.Net;
using System.Net.Sockets;

namespace Oadm.SiteNode.Relays;

/// <summary>
/// The senders a UDP relay heard from, so answers of the server go back only to them, through the socket that received
/// the datagram. A sender is forgotten <see cref="Idle"/> after its last datagram (an SNTP client takes a new source port
/// for every query), and the table never holds more than its capacity: a datagram from a new sender while it is full of
/// recent ones is not relayed.
/// </summary>
public sealed class UdpSenders(TimeProvider time, int capacity = UdpSenders.DefaultCapacity)
{
    public const int DefaultCapacity = 100_000;

    /// <summary>How long a sender may wait for its answer.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);

    private readonly Lock _lock = new();
    private readonly Dictionary<IPEndPoint, (Socket Socket, long Seen)> _senders = [];
    private long _prunedAt;

    public int Count
    {
        get
        {
            lock (_lock)
                return _senders.Count;
        }
    }

    /// <summary>Notes a datagram of <paramref name="sender"/>; false when the table is full of recent senders.</summary>
    public bool Touch(IPEndPoint sender, Socket socket)
    {
        ArgumentNullException.ThrowIfNull(sender);
        var now = time.GetTimestamp();
        lock (_lock)
        {
            if (!_senders.ContainsKey(sender) && _senders.Count >= capacity)
            {
                Prune(now);
                if (_senders.Count >= capacity)
                    return false;
            }
            else if (time.GetElapsedTime(_prunedAt, now) >= Idle)
            {
                Prune(now);
            }

            _senders[sender] = (socket, now);
            return true;
        }
    }

    /// <summary>The socket that heard <paramref name="sender"/> within <see cref="Idle"/>, else null.</summary>
    public Socket? Find(IPEndPoint sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        lock (_lock)
        {
            if (!_senders.TryGetValue(sender, out var entry))
                return null;
            if (time.GetElapsedTime(entry.Seen) < Idle)
                return entry.Socket;
            _senders.Remove(sender);
            return null;
        }
    }

    public void Clear()
    {
        lock (_lock)
            _senders.Clear();
    }

    private void Prune(long now)
    {
        _prunedAt = now;
        foreach (var (sender, entry) in _senders.ToList())
        {
            if (time.GetElapsedTime(entry.Seen, now) >= Idle)
                _senders.Remove(sender);
        }
    }
}
