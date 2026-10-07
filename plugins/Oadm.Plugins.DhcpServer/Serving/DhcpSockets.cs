using System.Net;
using System.Net.Sockets;
using System.Text;

using Oadm.Sdk.Network;

namespace Oadm.Plugins.DhcpServer.Serving;

/// <summary>Where a DHCP socket listens: one interface of the server.</summary>
/// <param name="InterfaceName">OS name ("Ethernet", "eth0", "en0"): Linux binds the socket to this device.</param>
/// <param name="InterfaceIndex">IPv4 interface index (-1 unknown): macOS binds to it, every OS filters received packets by it.</param>
/// <param name="Address">The server's IPv4 address on the interface.</param>
public sealed record DhcpBinding(string InterfaceName, int InterfaceIndex, IPAddress Address);

/// <summary>A received datagram: its length in the buffer and where it came from.</summary>
public readonly record struct DhcpReceived(int Length, IPEndPoint Remote);

/// <summary>A UDP socket of the DHCP server (port 67) or of the other-server check (port 68). Injectable: tests use an in-memory network.</summary>
public interface IDhcpSocket : IAsyncDisposable
{
    /// <summary>Waits for the next datagram of the bound interface.</summary>
    ValueTask<DhcpReceived> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>Sends a datagram; <see cref="IPAddress.Broadcast"/> goes out on the bound interface only.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken ct);
}

/// <summary>Opens DHCP sockets. Throws <see cref="SocketException"/> when the port cannot be bound.</summary>
public interface IDhcpSocketFactory
{
    /// <param name="binding">Interface.</param>
    /// <param name="port">67 (server) or 68 (check for other servers).</param>
    /// <param name="reusePort">Share the port with other programs (port 68 is often held by the OS DHCP client).</param>
    IDhcpSocket Open(DhcpBinding binding, int port, bool reusePort);
}

/// <summary>
/// Real UDP sockets, per OS (a DHCP server must receive broadcasts of clients without an address, on one interface):
/// <list type="bullet">
/// <item>Windows: bind to the interface address; Windows delivers broadcasts that arrive on that interface to it.</item>
/// <item>Linux: bind 0.0.0.0 and SO_BINDTODEVICE to the interface (root or CAP_NET_RAW on kernels before 5.7).</item>
/// <item>macOS: bind 0.0.0.0 and IP_BOUND_IF to the interface index (BSD does not deliver broadcasts to an address-bound socket).</item>
/// </list>
/// Every OS also checks the receiving interface of each packet (IP_PKTINFO) when bound to 0.0.0.0. A socket bound to a
/// loopback address never sends broadcasts: they go to the loopback address instead (tests).
/// </summary>
public sealed class UdpDhcpSocketFactory : IDhcpSocketFactory
{
    public static UdpDhcpSocketFactory Instance { get; } = new();

    private const int SolSocket = 1;
    private const int SoBindToDevice = 25;
    private const int IpBoundIf = 25;

    public IDhcpSocket Open(DhcpBinding binding, int port, bool reusePort)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.EnableBroadcast = true;
            if (reusePort)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }

            if (OperatingSystem.IsWindows())
            {
                // SIO_UDP_CONNRESET off: an ICMP "port unreachable" for an earlier answer must not fail the next receive.
                socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            }

            var loopback = IPAddress.IsLoopback(binding.Address);
            var filterIndex = -1;
            if (loopback || OperatingSystem.IsWindows())
            {
                socket.Bind(new IPEndPoint(binding.Address, port));
            }
            else
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                if (OperatingSystem.IsLinux())
                {
                    // Needs root or CAP_NET_RAW (kernels before 5.7); EPERM surfaces as AccessDenied -> "Insufficient permission".
                    socket.SetRawSocketOption(SolSocket, SoBindToDevice, Encoding.ASCII.GetBytes(binding.InterfaceName + "\0"));
                }
                else if (OperatingSystem.IsMacOS() && binding.InterfaceIndex > 0)
                {
                    socket.SetRawSocketOption(0 /* IPPROTO_IP */, IpBoundIf, BitConverter.GetBytes(binding.InterfaceIndex));
                }

                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                filterIndex = binding.InterfaceIndex;
            }

            return new UdpDhcpSocket(socket, binding, loopback, filterIndex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class UdpDhcpSocket(Socket socket, DhcpBinding binding, bool loopback, int filterIndex) : IDhcpSocket
    {
        private readonly IPEndPoint _any = new(IPAddress.Any, 0);

        public async ValueTask<DhcpReceived> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            while (true)
            {
                try
                {
                    if (filterIndex < 0)
                    {
                        var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, _any, ct).ConfigureAwait(false);
                        return new DhcpReceived(result.ReceivedBytes, (IPEndPoint)result.RemoteEndPoint);
                    }

                    var message = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, _any, ct).ConfigureAwait(false);
                    if (message.PacketInformation.Interface != filterIndex)
                    {
                        continue; // another interface (no device binding on this OS)
                    }

                    return new DhcpReceived(message.ReceivedBytes, (IPEndPoint)message.RemoteEndPoint);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize or SocketError.NetworkReset)
                {
                    // ignore and receive the next one
                }
            }
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint destination, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (loopback && !IPAddress.IsLoopback(destination.Address))
            {
                destination = new IPEndPoint(binding.Address, destination.Port); // never on the wire from a loopback binding
            }

            await socket.SendToAsync(datagram, SocketFlags.None, destination, ct).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Converts the selected interface into a binding (IPv4 address, index, name).</summary>
public static class DhcpBindings
{
    public static DhcpBinding? For(ServerNetworkInterface nic) =>
        nic?.PrimaryIpv4 is { } address ? new DhcpBinding(nic.Name, nic.Ipv4Index, address) : null;
}
