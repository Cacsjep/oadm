using Oadm.Sdk.Sites;

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
/// The probe on the device's route (<see cref="IDeviceNetwork"/>: the server's own network, or the site node of a device
/// at a site): a ping (<see cref="PingAttempts"/> tries with <see cref="PingTimeout"/> each) plus a TCP connect to port 80
/// and 443, closed right away. Nothing else is sent to whatever answers.
/// </summary>
public sealed class NetworkAddressProbe(IDeviceNetwork network) : IAddressProbe
{
    public const string NoNetworkText = "This OADM server cannot check addresses. Nothing was changed.";

    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(1);

    public int PingAttempts { get; init; } = 2;

    /// <summary>Connect timeout per port. A free address times out; a host answers within milliseconds on a LAN.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(800);

    public IReadOnlyList<int> Ports { get; init; } = [80, 443];

    /// <summary>The probe on <paramref name="network"/> (the context's route); a host without it refuses.</summary>
    public static IAddressProbe For(IDeviceNetwork? network) =>
        network is null ? throw new InvalidOperationException(NoNetworkText) : new NetworkAddressProbe(network);

    public async Task<AddressProbeResult> ProbeAsync(string address, CancellationToken ct)
    {
        var target = address.Trim().Trim('[', ']');
        var ping = PingAsync(target, ct);
        var tcp = AnswersTcpAsync(target, ct);
        await Task.WhenAll(ping, tcp).ConfigureAwait(false);
        return new AddressProbeResult(await ping.ConfigureAwait(false), await tcp.ConfigureAwait(false));
    }

    private async Task<bool> PingAsync(string address, CancellationToken ct)
    {
        for (var attempt = 0; attempt < PingAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (await network.PingAsync(address, PingTimeout, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> AnswersTcpAsync(string address, CancellationToken ct)
    {
        var results = await Task.WhenAll(Ports.Select(port => network.ProbeTcpAsync(address, port, ConnectTimeout, ct))).ConfigureAwait(false);
        return results.Any(r => r);
    }
}
