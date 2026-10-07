using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Oadm.Server.Hosting;

/// <summary>
/// Running the server as an operating system service: Windows service (installed by the MSI as
/// "OADM Server"), systemd unit (oadm-server.service of the .deb) or launchd daemon (com.oadm.server
/// of the macOS .pkg). The integrations are no-ops when the server runs as a plain console process.
/// The data folder is never derived from the service account: the service definitions pass it
/// explicitly (<c>--Oadm:DataDir</c> or env <c>OADM_DATA_DIR</c>), see <see cref="Core.Persistence.OadmPaths"/>.
/// </summary>
public static class ServiceHosting
{
    /// <summary>Windows service name used by the MSI (display name "OADM Server").</summary>
    public const string WindowsServiceName = "OadmServer";

    /// <summary>True when the process was started by the Windows service control manager or by systemd.</summary>
    public static bool IsRunningAsService() =>
        WindowsServiceHelpers.IsWindowsService() || SystemdHelpers.IsSystemdService();

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
