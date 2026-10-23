using System.Reflection;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.SystemReport;

/// <summary>
/// Core plugin "System report" (like ADM's "Get system report"): downloads the server report of the selected devices
/// (VAPIX serverreport.cgi, read-only) with the stored credentials and bundles them into one ZIP for Axis support. No rail
/// page (<see cref="HasPage"/> false): the client part is a toolbar button on the Devices page. Backend methods:
/// <see cref="SystemReportMethods"/>; every method needs the Operator role, starting a job is audited.
/// </summary>
public sealed class SystemReportPlugin : ICorePlugin, IDisposable
{
    private SystemReportJobs? _jobs;

    public string Id => SystemReportPluginInfo.PluginId;

    public string DisplayName => SystemReportPluginInfo.DisplayName;

    public string? IconKey => SystemReportPluginInfo.IconKey;
    public CorePluginGroup Group => CorePluginGroup.Reporting;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    public bool HasPage => false;

    /// <summary>Overrides the per-device timeout (tests).</summary>
    public TimeSpan? DeviceTimeout { get; init; }

    /// <summary>Overrides the time provider (tests).</summary>
    public TimeProvider? Time { get; init; }

    /// <summary>The jobs while the plugin runs (tests).</summary>
    public SystemReportJobs? Jobs => _jobs;

    /// <summary>The OADM version written into the summary (host application version).</summary>
    public static string HostVersion
    {
        get
        {
            var host = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Oadm.Server")
                ?? Assembly.GetEntryAssembly()
                ?? typeof(SystemReportPlugin).Assembly;
            var version = host.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? SdkInfo.Version;
            var plus = version.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? version[..plus] : version;
        }
    }

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Stop();
        // The data folder is admin-only; reports hold the device configuration, so never a shared temp folder there.
        var root = ctx.DataDirectory is { } data
            ? Path.Combine(data, "jobs")
            : Path.Combine(Path.GetTempPath(), "oadm-system-report-" + Guid.NewGuid().ToString("N"));
        _jobs = new SystemReportJobs(ctx.Devices, new ServerReportDownloader(ctx.Vapix, DeviceTimeout), root, HostVersion, Time, ctx.Logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        Stop();
        return Task.CompletedTask;
    }

    /// <summary>Starting a job is written to the audit log (user, client, plugin, method); reads are not.</summary>
    public bool IsAudited(string method) => method == SystemReportMethods.Start;

    /// <summary>No page: the toolbar button is the one right.</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new("download", "Download system reports", "Download the system reports of the selected devices as one ZIP file for Axis support."),
    ];

    public string? RequiredPermission(string method) => "download";

    /// <summary>
    /// Like backups and the audit log, system reports stay with users who have all devices (docs/spec/device-scope.md,
    /// Decisions): the host refuses every method for users with a device group before it runs.
    /// </summary>
    public bool RequiresAllDevices(string method) => true;

    /// <summary>Only users with all devices reach it (<see cref="RequiresAllDevices"/>); a group user's call is refused here too.</summary>
    public Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.HasAllDevices ? InvokeAsync(method, payloadJson, ct) : throw new DeviceAccessRefusedException();
    }

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        var jobs = _jobs ?? throw new InvalidOperationException("The system report plugin is not running.");
        switch (method)
        {
            case SystemReportMethods.Start:
                return SystemReportJson.Serialize(await jobs.StartAsync(SystemReportJson.Deserialize<StartRequest>(payloadJson), ct).ConfigureAwait(false));

            case SystemReportMethods.Status:
            {
                var request = SystemReportJson.Deserialize<StatusRequest>(payloadJson);
                return SystemReportJson.Serialize(jobs.Status(request.JobId, request.SinceVersion));
            }

            case SystemReportMethods.Read:
            {
                var request = SystemReportJson.Deserialize<ReadRequest>(payloadJson);
                return SystemReportJson.Serialize(await jobs.ReadAsync(request.JobId, request.Offset, ct).ConfigureAwait(false));
            }

            case SystemReportMethods.Delete:
                jobs.Delete(SystemReportJson.Deserialize<JobRequest>(payloadJson).JobId);
                return null;

            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    public void Dispose() => Stop();

    private void Stop()
    {
        _jobs?.Dispose();
        _jobs = null;
    }
}
