using Oadm.Plugins.DhcpServer.Leases;
using Oadm.Plugins.DhcpServer.Protocol;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.DhcpServer.Serving;

/// <summary>
/// The read-only view of the DHCP server for other plugins (<see cref="IDhcpServerLeases"/>, offered while the plugin runs):
/// the state, the most recent <see cref="Capacity"/> acknowledgements in a ring with sequence numbers, and the active leases.
/// <see cref="OnAck"/> never blocks the receive loop (one short lock). Nothing here changes the DHCP server.
/// </summary>
public sealed class DhcpLeaseFeed(Func<DhcpServerSummary> summary, LeaseStore store, TimeProvider time) : IDhcpServerLeases
{
    /// <summary>Acknowledgements kept for readers (a reader that falls further behind gets <see cref="DhcpAcks.Missed"/>).</summary>
    public const int Capacity = 4_096;

    private readonly DhcpAck?[] _ring = new DhcpAck?[Capacity];
    private readonly Lock _gate = new();
    private long _last;

    /// <summary>The sequence number of the newest acknowledgement (0: none yet).</summary>
    public long Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>The engine acknowledged <paramref name="address"/> to <paramref name="mac"/>.</summary>
    public void OnAck(ulong mac, uint address)
    {
        var at = time.GetUtcNow();
        lock (_gate)
        {
            _last++;
            _ring[_last % Capacity] = new DhcpAck(_last, MacAddress.Format(mac), Ip4.Format(address), at);
        }
    }

    /// <inheritdoc />
    public DhcpServerSummary Summary() => summary();

    /// <inheritdoc />
    public DhcpAcks AcksSince(long after, int max)
    {
        max = Math.Clamp(max, 1, 10_000);
        lock (_gate)
        {
            after = Math.Clamp(after, 0, _last);
            var oldest = Math.Max(1, _last - Capacity + 1);
            var missed = after + 1 < oldest;
            var from = Math.Max(after + 1, oldest);
            var list = new List<DhcpAck>((int)Math.Min(max, _last - from + 1));
            for (var seq = from; seq <= _last && list.Count < max; seq++)
            {
                list.Add(_ring[seq % Capacity]!);
            }

            return new DhcpAcks(list, list.Count > 0 ? list[^1].Sequence : _last, missed);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DhcpAck> ActiveLeases(IReadOnlyCollection<uint> ouiBlocks, int max)
    {
        ArgumentNullException.ThrowIfNull(ouiBlocks);
        var result = new List<DhcpAck>();
        foreach (var lease in store.Snapshot(out _))
        {
            if (result.Count >= max)
            {
                break;
            }

            if (lease.State != LeaseInfo.Active || !MacAddress.TryParse(lease.Mac, out var mac))
            {
                continue;
            }

            if (ouiBlocks.Count > 0 && !ouiBlocks.Contains((uint)((mac >> 24) & 0xFFFFFF)))
            {
                continue;
            }

            result.Add(new DhcpAck(0, lease.Mac, lease.Address, time.GetUtcNow()));
        }

        return result;
    }
}
