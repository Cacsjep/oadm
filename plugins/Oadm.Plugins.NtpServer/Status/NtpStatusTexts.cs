using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Oadm.Plugins.NtpServer.Status;

public enum HostOs
{
    Windows = 0,
    Linux = 1,
    MacOs = 2,
    Other = 3,
}

/// <summary>The status line texts (spec "NTP server", one of them at a time) and the mapping of bind errors.</summary>
public static class NtpStatusTexts
{
    public static HostOs CurrentOs =>
        OperatingSystem.IsWindows() ? HostOs.Windows
        : OperatingSystem.IsLinux() ? HostOs.Linux
        : OperatingSystem.IsMacOS() ? HostOs.MacOs
        : HostOs.Other;

    public static NtpStatusInfo Stopped { get; } = new(NtpStatusInfo.Neutral, "Stopped");

    /// <summary>"Running on 10.0.0.17:123", "Running on [fd00::17]:123", "Running on all interfaces, port 123".</summary>
    public static NtpStatusInfo Running(IReadOnlyList<IPEndPoint> endpoints, bool allInterfaces)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var detail = string.Join(", ", endpoints.Select(e => e.ToString()));
        if (allInterfaces || endpoints.Count == 0)
        {
            var port = endpoints.Count > 0 ? endpoints[0].Port : 0;
            return new(NtpStatusInfo.Ok, string.Create(CultureInfo.InvariantCulture, $"Running on all interfaces, port {port}"), "Listening on " + detail);
        }

        var first = endpoints[0].ToString();
        return new(NtpStatusInfo.Ok, "Running on " + first, endpoints.Count > 1 ? "Listening on " + detail : null);
    }

    public static NtpStatusInfo InterfaceNotAvailable(string interfaceName) =>
        new(NtpStatusInfo.Error, $"Interface {interfaceName} is not available",
            "The interface is down or has no address on this server. OADM tries again every 30 s; select another interface to serve now.");

    public static NtpStatusInfo TooManyRequests(int perSecond) =>
        new(NtpStatusInfo.Warning, "Too many requests, dropping",
            string.Create(CultureInfo.InvariantCulture, $"More than {perSecond:N0} requests per second arrive; requests above the limit are dropped."));

    public static NtpStatusInfo UpstreamNotReachable(string host, string? error) =>
        new(NtpStatusInfo.Warning, $"Upstream {host} not reachable, serving the server clock",
            (error is null ? string.Empty : error + ". ") + "The cameras get the server clock (stratum 10) until the upstream answers again.");

    public static NtpStatusInfo ClockDiffers(double offsetSeconds) =>
        new(NtpStatusInfo.Warning,
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
    public static NtpStatusInfo ForBindError(SocketError error, int port, string interfaceName, HostOs os, bool windowsTimeRunning = false)
    {
        var p = port.ToString(CultureInfo.InvariantCulture);
        switch (error)
        {
            case SocketError.AddressAlreadyInUse:
            case SocketError.AccessDenied when os == HostOs.Windows:
                if (os == HostOs.Windows && windowsTimeRunning)
                {
                    return new(NtpStatusInfo.Error, $"Port {p} is in use by another program (Windows Time service)",
                        $"The Windows Time service (W32Time) holds UDP port {p}. To serve time from OADM, stop it as administrator: " +
                        "\"net stop w32time\" and \"sc config w32time start= disabled\". Windows then no longer synchronizes its own clock; " +
                        "configure an upstream server in OADM instead.");
                }

                return new(NtpStatusInfo.Error, $"Port {p} is in use by another program",
                    os switch
                    {
                        HostOs.Windows => $"Find it with \"netstat -abno -p UDP\" (as administrator) and stop it.",
                        HostOs.Linux => $"Find it with \"sudo ss -ulpn 'sport = :{p}'\" (often chronyd or ntpd) and stop it or move it to another port.",
                        HostOs.MacOs => $"Find it with \"sudo lsof -nP -iUDP:{p}\" (timed holds it on some versions) and stop it.",
                        _ => "Stop the other NTP server on this machine.",
                    });

            case SocketError.AccessDenied:
                return new(NtpStatusInfo.Error, $"Insufficient permission to use port {p}",
                    os switch
                    {
                        HostOs.Linux => $"Ports below 1024 need root. Run the OADM server as root or grant the capability once: " +
                                        $"sudo setcap 'cap_net_bind_service=+ep' {ExecutablePath()}",
                        HostOs.MacOs => "Ports below 1024 on a single address need root on macOS. Run the OADM server with sudo, or choose \"All interfaces\".",
                        _ => "Run the OADM server with administrator rights.",
                    });

            case SocketError.AddressNotAvailable:
                return InterfaceNotAvailable(interfaceName);

            default:
                return new(NtpStatusInfo.Error, $"Cannot use port {p}: {error}", null);
        }
    }

    /// <summary>The server executable for the setcap hint.</summary>
    public static string ExecutablePath() => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Oadm.Server");
}

/// <summary>Windows: whether the Windows Time service runs (it binds UDP 123). Best effort, never throws.</summary>
public interface IWindowsTimeProbe
{
    Task<bool> IsRunningAsync(CancellationToken ct);
}

/// <summary>Runs <c>sc.exe query W32Time</c> (state names are not localized) with a 3 s timeout.</summary>
public sealed class ScQueryWindowsTimeProbe : IWindowsTimeProbe
{
    public async Task<bool> IsRunningAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("sc.exe", "query W32Time")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return output.Contains("RUNNING", StringComparison.Ordinal);
        }
#pragma warning disable CA1031 // Best effort: a missing sc.exe or a timeout only loses the hint.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
