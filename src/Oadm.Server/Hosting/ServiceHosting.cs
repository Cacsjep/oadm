using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Oadm.Server.Hosting;

/// <summary>
/// Running the server as an operating system service: Windows service (installed by the MSI as
/// "OADM Server"), systemd unit (oadm-server.service of the .deb) or launchd daemon (com.oadm.server
/// of the macOS .pkg, detected through OADM_SERVICE=launchd). The integrations are no-ops when the server runs as a
/// plain console process.
/// The data folder is never derived from the service account: the service definitions pass it
/// explicitly (<c>--Oadm:DataDir</c> or env <c>OADM_DATA_DIR</c>), see <see cref="Core.Persistence.OadmPaths"/>.
/// </summary>
public static class ServiceHosting
{
    /// <summary>Windows service name used by the MSI (display name "OADM Server").</summary>
    public const string WindowsServiceName = "OadmServer";

    /// <summary>Environment variable the LaunchDaemon (com.oadm.server.plist) sets: launchd has no reliable marker of its own.</summary>
    public const string ServiceEnvironmentVariable = "OADM_SERVICE";

    /// <summary>
    /// True when the process runs as the installed service: started by the Windows service control manager, by systemd,
    /// or by launchd with <c>OADM_SERVICE=launchd</c>. Only then the server enforces admin-only folders, manages Windows
    /// firewall rules and ignores the development plugin folder; console runs and tests are unaffected.
    /// </summary>
    public static bool IsRunningAsService() =>
        WindowsServiceHelpers.IsWindowsService() || SystemdHelpers.IsSystemdService() || IsLaunchdService();

    public static bool IsLaunchdService() =>
        OperatingSystem.IsMacOS() && string.Equals(Environment.GetEnvironmentVariable(ServiceEnvironmentVariable), "launchd", StringComparison.Ordinal);

    /// <summary>Folder permissions of this OS (Windows ACLs, else owner and mode through find/chown/chmod).</summary>
    public static IFolderPermissions CreateFolderPermissions() =>
        OperatingSystem.IsWindows() ? new WindowsFolderPermissions() : new UnixFolderPermissions(new ProcessCommandRunner());

    /// <summary>
    /// Folders checked at start: the data folder and, when set, the folder the single-file host extracts its native
    /// libraries to (<c>DOTNET_BUNDLE_EXTRACT_BASE_DIR</c>, set by every service definition).
    /// </summary>
    public static IReadOnlyList<string> ServerFolders(string dataDirectory)
    {
        var folders = new List<string> { dataDirectory };
        if (Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is { Length: > 0 } extract
            && !folders.Exists(f => IsSameOrBelow(Path.GetFullPath(extract), f)))
        {
            folders.Add(Path.GetFullPath(extract));
        }

        return folders;
    }

    private static bool IsSameOrBelow(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    /// <summary>
    /// Builder options. As a service the working directory is System32 (Windows) or / (systemd), so the
    /// content root (appsettings.json) is the app folder instead; a console run keeps the working directory.
    /// </summary>
    public static WebApplicationOptions CreateBuilderOptions(string[] args, bool runningAsService) => new()
    {
        Args = args,
        ContentRootPath = runningAsService ? AppContext.BaseDirectory : null,
    };

    /// <summary>Service lifetimes: start/stop notifications to the SCM or systemd (sd_notify). No-ops on a console.</summary>
    public static IServiceCollection AddServiceLifetimes(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddWindowsService(o => o.ServiceName = WindowsServiceName);
        services.AddSystemd();
        return services;
    }
}
