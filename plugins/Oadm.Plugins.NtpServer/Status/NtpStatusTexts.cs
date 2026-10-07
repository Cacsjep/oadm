using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Oadm.Sdk.Network;

namespace Oadm.Plugins.NtpServer.Status;

/// <summary>The status line texts (spec "NTP server", one of them at a time) and the mapping of bind errors.</summary>
public static class NtpStatusTexts
{
    public static HostOs CurrentOs => HostOsInfo.Current;

    public static ServiceStatus Stopped { get; } = new(ServiceStatus.Neutral, "Stopped");

    /// <summary>"Running on 10.0.0.17:123", "Running on [fd00::17]:123", "Running on all interfaces, port 123".</summary>
    public static ServiceStatus Running(IReadOnlyList<IPEndPoint> endpoints, bool allInterfaces)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var detail = string.Join(", ", endpoints.Select(e => e.ToString()));
        if (allInterfaces || endpoints.Count == 0)
        {
            var port = endpoints.Count > 0 ? endpoints[0].Port : 0;
            return new(ServiceStatus.Ok, string.Create(CultureInfo.InvariantCulture, $"Running on all interfaces, port {port}"), "Listening on " + detail);
        }

        var first = endpoints[0].ToString();
        return new(ServiceStatus.Ok, "Running on " + first, endpoints.Count > 1 ? "Listening on " + detail : null);
    }

    public static ServiceStatus InterfaceNotAvailable(string interfaceName) =>
        new(ServiceStatus.Error, $"Interface {interfaceName} is not available",
            "The interface is down or has no address on this server. OADM tries again every 30 s; select another interface to serve now.");

    public static ServiceStatus TooManyRequests(int perSecond) =>
        new(ServiceStatus.Warning, "Too many requests, dropping",
            string.Create(CultureInfo.InvariantCulture, $"More than {perSecond:N0} requests per second arrive; requests above the limit are dropped."));

    public static ServiceStatus UpstreamNotReachable(string host, string? error) =>
        new(ServiceStatus.Warning, $"Upstream {host} does not answer, using this computer's time",
            (error is null ? string.Empty : error + ". ") + "The cameras get this computer's time until the upstream answers again.");

    public static ServiceStatus ClockDiffers(double offsetSeconds) =>
        new(ServiceStatus.Warning,
            string.Create(CultureInfo.InvariantCulture, $"Server clock differs from upstream by {Math.Abs(offsetSeconds):0.0} s"),
            "OADM serves the server clock and does not set it. Synchronize the server's clock (Windows Time, chrony, systemd-timesyncd).");

    /// <summary>
    /// Maps a failed bind of the NTP port to the status line. Windows has no privileged ports: an access error there
    /// means another socket holds the port exclusively, so it reads "in use" like EADDRINUSE.
    /// </summary>
    /// <param name="error">Socket error of the bind.</param>
    /// <param name="port">The port (123).</param>
    /// <param name="interfaceName">Selected interface ("All interfaces").</param>
    /// <param name="os">Server OS.</param>
    /// <param name="windowsTimeRunning">Windows: the Windows Time service (W32Time) runs, so it most likely holds the port.</param>
    public static ServiceStatus ForBindError(SocketError error, int port, string interfaceName, HostOs os, bool windowsTimeRunning = false)
    {
        var p = port.ToString(CultureInfo.InvariantCulture);
        switch (PortBindErrors.Classify(error, os))
        {
            case BindErrorKind.InUse:
                if (os == HostOs.Windows && windowsTimeRunning)
                {
                    return new(ServiceStatus.Error, PortBindErrors.InUseText(port) + " (Windows Time service)",
                        $"The Windows Time service (W32Time) holds UDP port {p}. To serve time from OADM, stop it as administrator: " +
                        "\"net stop w32time\" and \"sc config w32time start= disabled\". Windows then no longer synchronizes its own clock; " +
                        "configure an upstream server in OADM instead.");
                }

                return new(ServiceStatus.Error, PortBindErrors.InUseText(port),
                    os == HostOs.Other ? "Stop the other NTP server on this machine." : PortBindErrors.FindUdpPortOwner(os, port, os switch
                    {
                        HostOs.Linux => "often chronyd or ntpd",
                        HostOs.MacOs => "timed holds it on some versions",
                        _ => null,
                    }));

            case BindErrorKind.Permission:
                return new(ServiceStatus.Error, PortBindErrors.PermissionText(port),
                    PortBindErrors.PermissionFix(os, macOsAlternative: ", or choose \"All interfaces\""));

            case BindErrorKind.AddressNotAvailable:
                return InterfaceNotAvailable(interfaceName);

            default:
                return new(ServiceStatus.Error, PortBindErrors.OtherText(port, error), null);
        }
    }

    /// <summary>The server executable for the setcap hint.</summary>
    public static string ExecutablePath() => HostOsInfo.ExecutablePath();
}

/// <summary>Windows: whether the Windows Time service runs (it binds UDP 123). Best effort, never throws.</summary>
public interface IWindowsTimeProbe
{
    Task<bool> IsRunningAsync(CancellationToken ct);
}

/// <summary>The shared <see cref="ScQueryServiceProbe"/> for the service W32Time.</summary>
public sealed class ScQueryWindowsTimeProbe : IWindowsTimeProbe
{
    public Task<bool> IsRunningAsync(CancellationToken ct) => ScQueryServiceProbe.Instance.IsRunningAsync("W32Time", ct);
}
