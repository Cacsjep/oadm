using System.Net.Sockets;

using Oadm.Core.Sites;

namespace Oadm.Core.LiveView.Probes;

/// <summary>
/// The local UDP ports of RTP over UDP (docs/spec/live-view.md, Stream probes): pairs from a fixed range, the even port
/// for RTP and the next one for RTCP (RFC 3550 11), so one firewall rule covers them. 400 pairs, two per stream for up to
/// 200 streams at once with room for ports other programs hold. Thread-safe: a pair is handed out once until its lease
/// ends; a pair whose bind fails (another program) is skipped. The Load test plugin opens its firewall rule for
/// the same range (its own copy of the two numbers: plugins never reference Oadm.Core).
/// </summary>
public sealed class RtpUdpPorts
{
    /// <summary>The first port of the range (even).</summary>
    public const int FirstPort = 50000;

    /// <summary>The last port of the range (odd).</summary>
    public const int LastPort = 50799;

    private readonly Lock _gate = new();
    private readonly bool[] _used;
    private int _next;

    /// <param name="firstPort">The first port, even (tests use their own range).</param>
    /// <param name="lastPort">The last port; a range of n pairs ends on an odd port.</param>
    public RtpUdpPorts(int firstPort = FirstPort, int lastPort = LastPort)
    {
        if (firstPort % 2 != 0 || firstPort < 1024 || lastPort > 65535 || lastPort <= firstPort)
        {
            throw new ArgumentOutOfRangeException(nameof(firstPort), $"{firstPort}-{lastPort} is no range of RTP port pairs.");
        }

        First = firstPort;
        _used = new bool[(lastPort - firstPort + 1) / 2];
    }

    /// <summary>The first port of this range.</summary>
    public int First { get; }

    /// <summary>The pairs of the range.</summary>
    public int Pairs => _used.Length;

    /// <summary>Pairs handed out now.</summary>
    public int InUse
    {
        get
        {
            lock (_gate)
            {
                return _used.Count(u => u);
            }
        }
    }

    /// <summary>
    /// The next free pair, bound with <paramref name="bind"/> (RTP port, then RTCP port); null when every pair is in use
    /// or taken by another program. The caller then uses TCP.
    /// </summary>
    public RtpPortLease? TryRent(Func<int, IUdpPort> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        for (var attempt = 0; attempt < _used.Length; attempt++)
        {
            int pair;
            lock (_gate)
            {
                pair = -1;
                for (var i = 0; i < _used.Length; i++)
                {
                    var candidate = (_next + i) % _used.Length;
                    if (!_used[candidate])
                    {
                        pair = candidate;
                        break;
                    }
                }

                if (pair < 0)
                {
                    return null; // every pair is out
                }

                _used[pair] = true;
                _next = (pair + 1) % _used.Length; // round robin: a port just freed rests before it is used again
            }

            var port = First + (2 * pair);
            IUdpPort? rtp = null;
            try
            {
                rtp = bind(port);
                var rtcp = bind(port + 1);
                return new RtpPortLease(this, pair, rtp, rtcp);
            }
            catch (SocketException)
            {
                rtp?.Dispose();
                Release(pair); // another program holds it: try the next pair
            }
        }

        return null;
    }

    internal void Release(int pair)
    {
        lock (_gate)
        {
            _used[pair] = false;
        }
    }
}

/// <summary>A rented RTP / RTCP port pair; disposing closes both ports and frees the pair.</summary>
public sealed class RtpPortLease : IDisposable
{
    private readonly RtpUdpPorts _owner;
    private readonly int _pair;
    private int _disposed;

    internal RtpPortLease(RtpUdpPorts owner, int pair, IUdpPort rtp, IUdpPort rtcp)
    {
        _owner = owner;
        _pair = pair;
        Rtp = rtp;
        Rtcp = rtcp;
    }

    /// <summary>The even port: RTP.</summary>
    public IUdpPort Rtp { get; }

    /// <summary>The odd port: RTCP.</summary>
    public IUdpPort Rtcp { get; }

    /// <summary>The RTP port number.</summary>
    public int RtpPort => _owner.First + (2 * _pair);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Rtp.Dispose();
        Rtcp.Dispose();
        _owner.Release(_pair);
    }
}
