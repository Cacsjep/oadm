using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;

namespace Oadm.Sdk.Network;

/// <summary>The server's operating system, for OS-specific status texts (bind errors, setup hints).</summary>
public enum HostOs
{
    Windows = 0,
    Linux = 1,
    MacOs = 2,
    Other = 3,
}

public static class HostOsInfo
{
    public static HostOs Current =>
        OperatingSystem.IsWindows() ? HostOs.Windows
        : OperatingSystem.IsLinux() ? HostOs.Linux
        : OperatingSystem.IsMacOS() ? HostOs.MacOs
        : HostOs.Other;

    /// <summary>The server executable (for setcap hints).</summary>
    public static string ExecutablePath() => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Oadm.Server");
}

/// <summary>
/// The status line of a network service plugin (NTP server, DHCP server): kind ok / neutral / warning / error, a plain
/// text for technicians and an optional detail (tooltip) with the fix.
/// </summary>
public sealed record ServiceStatus(string Kind, string Text, string? Detail = null)
{
    public const string Ok = "ok";
    public const string Neutral = "neutral";
    public const string Warning = "warning";
    public const string Error = "error";
}

/// <summary>What a failed bind of a service port means.</summary>
public enum BindErrorKind
{
    /// <summary>Another program holds the port.</summary>
    InUse = 0,

    /// <summary>The process may not bind the (privileged) port.</summary>
    Permission = 1,

    /// <summary>The address is not on this machine (any more): the interface is down or gone.</summary>
    AddressNotAvailable = 2,

    Other = 3,
}

/// <summary>
/// Maps a failed bind of a service port (NTP 123, DHCP 67) per OS and builds the shared parts of the status texts.
/// Windows has no privileged ports: AccessDenied there means another socket holds the port exclusively, so it is
/// "in use" like EADDRINUSE. Linux needs root or capabilities below 1024; macOS needs root for a single address.
/// </summary>
public static class PortBindErrors
{
    public static BindErrorKind Classify(SocketError error, HostOs os) => error switch
    {
        SocketError.AddressAlreadyInUse => BindErrorKind.InUse,
        SocketError.AccessDenied when os == HostOs.Windows => BindErrorKind.InUse,
        SocketError.AccessDenied => BindErrorKind.Permission,
        SocketError.AddressNotAvailable => BindErrorKind.AddressNotAvailable,
        _ => BindErrorKind.Other,
    };

    /// <summary>"Port 67 is in use by another program".</summary>
    public static string InUseText(int port) => string.Create(CultureInfo.InvariantCulture, $"Port {port} is in use by another program");

    /// <summary>"Insufficient permission to use port 67".</summary>
    public static string PermissionText(int port) => string.Create(CultureInfo.InvariantCulture, $"Insufficient permission to use port {port}");

    /// <summary>"Cannot use port 67: NetworkDown".</summary>
    public static string OtherText(int port, SocketError error) => string.Create(CultureInfo.InvariantCulture, $"Cannot use port {port}: {error}");

    /// <summary>How to find the program that holds a UDP port, per OS.</summary>
    /// <param name="os">Server OS.</param>
    /// <param name="port">The port.</param>
    /// <param name="usualSuspects">"often chronyd or ntpd"; appended in brackets on Linux and macOS when given.</param>
    public static string FindUdpPortOwner(HostOs os, int port, string? usualSuspects = null)
    {
        var p = port.ToString(CultureInfo.InvariantCulture);
        var suspects = string.IsNullOrEmpty(usualSuspects) ? string.Empty : $" ({usualSuspects})";
        return os switch
        {
            HostOs.Windows => "Find it with \"netstat -abno -p UDP\" (as administrator) and stop it.",
            HostOs.Linux => $"Find it with \"sudo ss -ulpn 'sport = :{p}'\"{suspects} and stop it or move it to another port.",
            HostOs.MacOs => $"Find it with \"sudo lsof -nP -iUDP:{p}\"{suspects} and stop it.",
            _ => "Stop the other program on this machine.",
        };
    }

    /// <summary>How to allow a port below 1024, per OS.</summary>
    /// <param name="os">Server OS.</param>
    /// <param name="capabilities">Linux capabilities to grant, e.g. "cap_net_bind_service".</param>
    /// <param name="macOsAlternative">macOS: an alternative to sudo (", or choose \"All interfaces\"").</param>
    public static string PermissionFix(HostOs os, string capabilities = "cap_net_bind_service", string? macOsAlternative = null) => os switch
    {
        HostOs.Linux => $"Ports below 1024 need root. Run the OADM server as root or grant the capability once: " +
                        $"sudo setcap '{capabilities}=+ep' {HostOsInfo.ExecutablePath()}",
        HostOs.MacOs => "Ports below 1024 on a single address need root on macOS. Run the OADM server with sudo" + (macOsAlternative ?? string.Empty) + ".",
        _ => "Run the OADM server with administrator rights.",
    };
}

/// <summary>Windows: whether a Windows service runs (it may hold a port). Best effort, never throws.</summary>
public interface IWindowsServiceProbe
{
    Task<bool> IsRunningAsync(string serviceName, CancellationToken ct);
}

/// <summary>Runs <c>sc.exe query &lt;service&gt;</c> (state names are not localized) with a 3 s timeout; false elsewhere.</summary>
public sealed class ScQueryServiceProbe : IWindowsServiceProbe
{
    public static ScQueryServiceProbe Instance { get; } = new();

    public async Task<bool> IsRunningAsync(string serviceName, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(serviceName) || serviceName.Any(c => !char.IsAsciiLetterOrDigit(c)))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("sc.exe", "query " + serviceName)
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

/// <summary>One entry of a service page's interface select (the server's interfaces, listed by the server).</summary>
/// <param name="Id">OS interface id.</param>
/// <param name="Label">"Ethernet - 10.0.0.17 (Intel I219)".</param>
/// <param name="Name">"Ethernet".</param>
/// <param name="Addresses">Its addresses as text.</param>
public sealed record InterfaceOption(string Id, string Label, string Name, IReadOnlyList<string> Addresses);

public static class InterfaceOptions
{
    /// <summary>"Ethernet - 10.0.0.17 (Intel I219)"; the description is left out when it equals the name.</summary>
    public static string Label(ServerNetworkInterface nic, string addressText)
    {
        ArgumentNullException.ThrowIfNull(nic);
        return $"{nic.Name} - {addressText}" + (string.Equals(nic.Description, nic.Name, StringComparison.Ordinal) ? string.Empty : $" ({nic.Description})");
    }

    /// <summary>The option of an interface; <paramref name="addressText"/> defaults to its primary address.</summary>
    public static InterfaceOption From(ServerNetworkInterface nic, string? addressText = null)
    {
        ArgumentNullException.ThrowIfNull(nic);
        return new InterfaceOption(nic.Id, Label(nic, addressText ?? nic.PrimaryAddress?.ToString() ?? "no address"), nic.Name, [.. nic.Addresses.Select(a => a.ToString())]);
    }
}
