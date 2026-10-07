using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.DhcpServer.Protocol;

namespace Oadm.Plugins.DhcpServer.Serving;

/// <summary>Result of a check for other DHCP servers.</summary>
/// <param name="Servers">Addresses of other servers that answered (empty: none).</param>
/// <param name="Error">The check could not run (e.g. port 68 not available); null when it ran.</param>
public sealed record OtherServerResult(IReadOnlyList<string> Servers, string? Error)
{
    public static OtherServerResult None { get; } = new([], null);
}

/// <summary>Looks for other DHCP servers on the interface. Injectable so tests use a fake second server.</summary>
public interface IOtherServerCheck
{
    Task<OtherServerResult> RunAsync(DhcpBinding binding, CancellationToken ct);
}

/// <summary>
/// Sends a discover (broadcast flag set, a random locally administered MAC address that never gets a lease) from port 68
/// and collects the offers of other servers for <see cref="Wait"/>; the probe is sent twice in case one is lost. The own
/// server ignores the probe MAC (<see cref="IsProbeMac"/>), so it never answers itself. The other server keeps a short
/// pending offer for the probe MAC, which it drops when no request follows.
/// </summary>
public sealed partial class OtherServerCheck : IOtherServerCheck
{
    private readonly ConcurrentDictionary<ulong, byte> _probeMacs = new();
    private readonly IDhcpSocketFactory _sockets;
    private readonly ILogger _logger;

    public OtherServerCheck(IDhcpSocketFactory sockets, int serverPort = 67, int clientPort = 68, ILogger? logger = null)
    {
        _sockets = sockets ?? throw new ArgumentNullException(nameof(sockets));
        ServerPort = serverPort;
        ClientPort = clientPort;
        _logger = logger ?? NullLogger.Instance;
    }

    public int ServerPort { get; }

    public int ClientPort { get; }

    /// <summary>How long offers are collected.</summary>
    public TimeSpan Wait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>True for the MAC address of a running check (the own server ignores these messages).</summary>
    public bool IsProbeMac(ulong mac) => _probeMacs.ContainsKey(mac);

    public async Task<OtherServerResult> RunAsync(DhcpBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        IDhcpSocket socket;
        try
        {
            socket = _sockets.Open(binding, ClientPort, reusePort: true);
        }
        catch (SocketException ex)
        {
            LogCannotCheck(binding.InterfaceName, ex.SocketErrorCode);
            return new OtherServerResult([], $"The check for other DHCP servers could not run ({ex.SocketErrorCode}).");
        }

        var mac = 0x02_0A_D4_00_00_00UL | (ulong)(uint)RandomNumberGenerator.GetInt32(0x1000000); // locally administered
        var xid = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
        _probeMacs[mac] = 0;
        var own = binding.Address;
        var found = new List<string>();
        try
        {
            await using (socket.ConfigureAwait(false))
            {
                var discover = new DhcpMessage
                {
                    Op = DhcpMessage.BootRequest,
                    TransactionId = xid,
                    Flags = DhcpMessage.BroadcastFlag,
                    MessageType = DhcpMessageType.Discover,
                    ParameterRequestList = [DhcpOption.SubnetMask, DhcpOption.Router, DhcpOption.DnsServers],
                    Mac = mac,
                };
                var bytes = new byte[DhcpMessage.MaxLength];
                var length = discover.Write(bytes);
                var target = new IPEndPoint(IPAddress.Broadcast, ServerPort);

                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                window.CancelAfter(Wait);
                var receive = ReceiveOffersAsync(socket, xid, own, found, window.Token);
                await socket.SendAsync(bytes.AsMemory(0, length), target, ct).ConfigureAwait(false);
                try
                {
                    await Task.Delay(Wait / 3, window.Token).ConfigureAwait(false);
                    await socket.SendAsync(bytes.AsMemory(0, length), target, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                }

                await receive.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (SocketException ex)
        {
            LogCannotCheck(binding.InterfaceName, ex.SocketErrorCode);
            return new OtherServerResult([], $"The check for other DHCP servers could not run ({ex.SocketErrorCode}).");
        }
        finally
        {
            _probeMacs.TryRemove(mac, out _);
        }

        if (found.Count > 0)
        {
            LogFound(binding.InterfaceName, string.Join(", ", found));
        }

        return new OtherServerResult(found, null);
    }

    private static async Task ReceiveOffersAsync(IDhcpSocket socket, uint xid, IPAddress own, List<string> found, CancellationToken ct)
    {
        var buffer = new byte[DhcpMessage.MaxLength];
        while (!ct.IsCancellationRequested)
        {
            DhcpReceived received;
            try
            {
                received = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!DhcpMessage.TryRead(buffer.AsSpan(0, received.Length), out var reply, out _) || reply is null
                || reply.Op != DhcpMessage.BootReply || reply.TransactionId != xid || reply.MessageType != DhcpMessageType.Offer)
            {
                continue;
            }

            var server = reply.ServerId is { } id ? Ip4.ToAddress(id) : received.Remote.Address;
            if (server.Equals(own) || received.Remote.Address.Equals(own))
            {
                continue;
            }

            var text = server.ToString();
            lock (found)
            {
                if (!found.Contains(text))
                {
                    found.Add(text);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "DHCP: the check for other DHCP servers on {Interface} could not run: {Error}")]
    private partial void LogCannotCheck(string @interface, SocketError error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DHCP: other DHCP servers answer on {Interface}: {Servers}")]
    private partial void LogFound(string @interface, string servers);
}
