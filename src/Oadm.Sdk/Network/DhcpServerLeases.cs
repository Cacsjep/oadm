namespace Oadm.Sdk.Network;

/// <summary>
/// The DHCP server's state and the addresses it hands out, read only (SDK 0.15.0). The DHCP server plugin offers it through
/// <see cref="Plugins.ICorePluginContext.Services"/> while it runs; Provisioning reads it to start a flow on a new camera.
/// Ask with <c>Find</c> each time and never keep it. Nothing here changes the DHCP server. Thread-safe.
/// </summary>
public interface IDhcpServerLeases
{
    /// <summary>The DHCP server's state: whether it hands out addresses, its interface and range.</summary>
    DhcpServerSummary Summary();

    /// <summary>
    /// The acknowledged leases (a new or renewed address of a device) after <paramref name="after"/>, oldest first, at most
    /// <paramref name="max"/>. The server keeps the most recent few thousand; <see cref="DhcpAcks.Missed"/> says that older
    /// ones were dropped since. Start with <see cref="DhcpAcks.Next"/> of the last answer (0 the first time).
    /// </summary>
    /// <param name="after">The sequence number of the last acknowledgement read.</param>
    /// <param name="max">At most this many (1 to 10,000).</param>
    DhcpAcks AcksSince(long after, int max);

    /// <summary>The active leases (dynamic and static) of devices with one of the MAC address blocks, at most <paramref name="max"/>.</summary>
    /// <param name="ouiBlocks">The first three bytes of the MAC addresses (0xACCC8E); empty: every lease.</param>
    /// <param name="max">At most this many.</param>
    IReadOnlyList<DhcpAck> ActiveLeases(IReadOnlyCollection<uint> ouiBlocks, int max);
}

/// <summary>The DHCP server's state (<see cref="IDhcpServerLeases.Summary"/>, SDK 0.15.0).</summary>
/// <param name="IsRunning">The server hands out addresses.</param>
/// <param name="StatusText">The status as the DHCP server page shows it ("Running", "Stopped").</param>
/// <param name="InterfaceName">The network interface it serves ("Ethernet 2"), when set.</param>
/// <param name="ServerAddress">The server's own IPv4 address on that interface, when known.</param>
/// <param name="SubnetMask">The subnet mask of that interface ("255.255.255.0"), when known.</param>
/// <param name="RangeStart">The first address of its range, when set.</param>
/// <param name="RangeEnd">The last address of its range, when set.</param>
public sealed record DhcpServerSummary(
    bool IsRunning,
    string StatusText,
    string? InterfaceName,
    string? ServerAddress,
    string? SubnetMask,
    string? RangeStart,
    string? RangeEnd);

/// <summary>One acknowledged lease (SDK 0.15.0).</summary>
/// <param name="Sequence">Its number, rising by one per acknowledgement (0 in <see cref="IDhcpServerLeases.ActiveLeases"/>).</param>
/// <param name="Mac">The device's MAC address, "AC:CC:8E:01:02:03".</param>
/// <param name="Address">The IPv4 address it got.</param>
/// <param name="At">When the server acknowledged it.</param>
public sealed record DhcpAck(long Sequence, string Mac, string Address, DateTimeOffset At);

/// <summary>The answer of <see cref="IDhcpServerLeases.AcksSince"/> (SDK 0.15.0).</summary>
/// <param name="Acks">The acknowledgements, oldest first.</param>
/// <param name="Next">The sequence number to read after next time.</param>
/// <param name="Missed">Acknowledgements after the given number were dropped before they were read.</param>
public sealed record DhcpAcks(IReadOnlyList<DhcpAck> Acks, long Next, bool Missed);
