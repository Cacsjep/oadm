using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Oadm.Plugins.Network;

/// <summary>What answered at an address: an ICMP echo (ping) and/or a TCP connect to port 80 or 443.</summary>
public sealed record AddressProbeResult(bool AnswersPing, bool AnswersTcp)
{
    public static AddressProbeResult Silent { get; } = new(false, false);

    /// <summary>Something answered: the address is taken (unless it is the device's own address).</summary>
    public bool InUse => AnswersPing || AnswersTcp;

    /// <summary>"answers ping", "answers on port 80/443"; null when nothing answered.</summary>
    public string? Reason => AnswersPing ? "answers ping" : AnswersTcp ? "answers on port 80/443" : null;
}

/// <summary>
/// Is an address taken? Used by the dialog's address check (query "checkAddresses") and by the task step
/// "Check address is free" before any static address is written. Replaceable in tests (no network in unit tests).
/// </summary>
public interface IAddressProbe
{
    Task<AddressProbeResult> ProbeAsync(string address, CancellationToken ct);
}

/// <summary>
/// The server-side probe: ICMP echo (<see cref="Ping"/>, <see cref="PingAttempts"/> tries with
/// <see cref="PingTimeout"/> each; cross-platform, on Linux .NET falls back to the ping utility without root) plus a
/// plain TCP connect to port 80 and 443, closed right away. Nothing else is sent to whatever answers.
/// </summary>
public sealed class NetworkAddressProbe : IAddressProbe
{
    public static NetworkAddressProbe Instance { get; } = new();

    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(1);

    public int PingAttempts { get; init; } = 2;

    /// <summary>Connect timeout per port. A free address times out; a host answers within milliseconds on a LAN.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(800);

    public IReadOnlyList<int> Ports { get; init; } = [80, 443];

    public async Task<AddressProbeResult> ProbeAsync(string address, CancellationToken ct)
    {
        var ip = IPAddress.Parse(address.Trim().Trim('[', ']'));
        var ping = PingAsync(ip, ct);
        var tcp = AnswersTcpAsync(ip, ct);
        await Task.WhenAll(ping, tcp).ConfigureAwait(false);
        return new AddressProbeResult(await ping.ConfigureAwait(false), await tcp.ConfigureAwait(false));
    }

    private async Task<bool> PingAsync(IPAddress ip, CancellationToken ct)
    {
        using var ping = new Ping();
        for (var attempt = 0; attempt < PingAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(ip, PingTimeout, buffer: null, options: null, ct).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    return true;
                }
            }
            catch (PingException)
            {
                // No route, ping not available: counts as "no answer"; the TCP probe still runs.
            }
        }

        return false;
    }

    private async Task<bool> AnswersTcpAsync(IPAddress ip, CancellationToken ct)
    {
        var results = await Task.WhenAll(Ports.Select(port => ConnectAsync(ip, port, ct))).ConfigureAwait(false);
        return results.Any(r => r);
    }

    /// <summary>True when a TCP connection is accepted or actively refused (a host sent a reset: the address is taken).</summary>
    internal async Task<bool> ConnectAsync(IPAddress ip, int port, CancellationToken ct)
    {
        using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
