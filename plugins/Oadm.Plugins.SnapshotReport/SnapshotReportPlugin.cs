using System.Reflection;

using Oadm.Plugins.SnapshotReport.Report;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.SnapshotReport;

/// <summary>
/// Core plugin "Snapshot report" (yearly maintenance): the page shows current snapshots of all video sources
/// of the managed video devices and exports a PDF maintenance report. Read-only for the devices: it lists
/// video sources and takes JPEG snapshots with the stored credentials, server side.
/// Page backend methods: <see cref="SnapshotReportMethods"/>. Device groups (SDK 0.4.0): every method works on the devices
/// of the user's view, reports are jobs of their user (<see cref="ICorePluginContext.Jobs"/>).
/// </summary>
public sealed class SnapshotReportPlugin : ICorePlugin, IDisposable
{
    private SnapshotService? _snapshots;
    private ReportJobs? _reports;
    private ICorePluginContext? _ctx;

    public string Id => SnapshotReportPluginInfo.PluginId;

    public string DisplayName => SnapshotReportPluginInfo.DisplayName;

    public string? IconKey => SnapshotReportPluginInfo.IconKey;
    public CorePluginGroup Group => CorePluginGroup.Reporting;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    /// <summary>
    /// Creating snapshots and the PDF reads every camera: a right of its own, which the built-in Viewer also has (the
    /// page shows nothing without it).
    /// </summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new("report", "Create snapshots and reports", "Take a current picture of every camera and export the PDF report.") { ViewerDefault = true },
    ];

    public string? RequiredPermission(string method) => method == SnapshotReportMethods.ListSources ? PermissionKeys.Show : "report";

    /// <summary>The OADM version printed on the report cover (host application version).</summary>
    public static string HostVersion
    {
        get
        {
            // The server host assembly (also when it runs inside a test host), else the entry assembly.
            var host = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Oadm.Server")
                ?? Assembly.GetEntryAssembly()
                ?? typeof(SnapshotReportPlugin).Assembly;
            var version = host.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? SdkInfo.Version;
            var plus = version.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? version[..plus] : version;
        }
    }

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        Stop();
        _ctx = ctx;
        _snapshots = new SnapshotService(ctx.Devices, ctx.Vapix);
        _reports = new ReportJobs(_snapshots, logger: ctx.Logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        Stop();
        return Task.CompletedTask;
    }

    /// <summary>In-process callers (tests): the plugin's own devices (the context refuses them in a group user's call).</summary>
    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        var snapshots = _snapshots ?? throw new InvalidOperationException("The snapshot report plugin is not running.");
        var reports = _reports!;
        switch (method)
        {
            case SnapshotReportMethods.ListSources:
                return SnapshotReportJson.Serialize(await snapshots.ListSourcesAsync(SnapshotReportJson.Deserialize<ListSourcesRequest>(payloadJson), ct).ConfigureAwait(false));
            case SnapshotReportMethods.Snapshot:
            {
                var request = SnapshotRequestOf(payloadJson);
                return SnapshotReportJson.Serialize(ToResult(await snapshots.TakeAsync(request.DeviceId, request.Camera, request.MaxWidth, request.MaxHeight, ct).ConfigureAwait(false)));
            }

            case SnapshotReportMethods.GenerateReport:
                return SnapshotReportJson.Serialize(reports.Start(SnapshotReportJson.Deserialize<ReportRequest>(payloadJson), HostVersion));
            case SnapshotReportMethods.ReportStatus:
                return SnapshotReportJson.Serialize(reports.Status(SnapshotReportJson.Deserialize<ReportJobRequest>(payloadJson).JobId));
            case SnapshotReportMethods.ReadReport:
            {
                var request = SnapshotReportJson.Deserialize<ReadReportRequest>(payloadJson);
                return SnapshotReportJson.Serialize(reports.Read(request.JobId, request.Offset));
            }

            case SnapshotReportMethods.DeleteReport:
                reports.Delete(SnapshotReportJson.Deserialize<ReportJobRequest>(payloadJson).JobId);
                return null;
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    /// <summary>A user's call: the devices of the user's view; reports are the user's jobs.</summary>
    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var snapshots = _snapshots ?? throw new InvalidOperationException("The snapshot report plugin is not running.");
        var reports = _reports!;
        var jobs = _ctx?.Jobs ?? throw new InvalidOperationException("This OADM server cannot run reports.");
        switch (method)
        {
            case SnapshotReportMethods.ListSources:
                return SnapshotReportJson.Serialize(await snapshots.ListSourcesAsync(caller, SnapshotReportJson.Deserialize<ListSourcesRequest>(payloadJson), ct).ConfigureAwait(false));
            case SnapshotReportMethods.Snapshot:
            {
                var request = SnapshotRequestOf(payloadJson);
                return SnapshotReportJson.Serialize(ToResult(await snapshots.TakeAsync(caller, request.DeviceId, request.Camera, request.MaxWidth, request.MaxHeight, ct).ConfigureAwait(false)));
            }

            case SnapshotReportMethods.GenerateReport:
                return SnapshotReportJson.Serialize(await reports.StartAsync(caller, jobs, SnapshotReportJson.Deserialize<ReportRequest>(payloadJson), HostVersion, ct).ConfigureAwait(false));
            case SnapshotReportMethods.ReportStatus:
                return SnapshotReportJson.Serialize(reports.Status(caller, jobs, SnapshotReportJson.Deserialize<ReportJobRequest>(payloadJson).JobId));
            case SnapshotReportMethods.ReadReport:
            {
                var request = SnapshotReportJson.Deserialize<ReadReportRequest>(payloadJson);
                return SnapshotReportJson.Serialize(reports.Read(caller, jobs, request.JobId, request.Offset));
            }

            case SnapshotReportMethods.DeleteReport:
                reports.Delete(caller, jobs, SnapshotReportJson.Deserialize<ReportJobRequest>(payloadJson).JobId);
                return null;
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    private static SnapshotRequest SnapshotRequestOf(string? payloadJson)
    {
        var request = SnapshotReportJson.Deserialize<SnapshotRequest>(payloadJson);
        return request.DeviceId == Guid.Empty ? throw new ArgumentException("deviceId is required.", nameof(payloadJson)) : request;
    }

    public static SnapshotResult ToResult(CapturedSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SnapshotResult
        {
            JpegBase64 = snapshot.Jpeg is null ? null : Convert.ToBase64String(snapshot.Jpeg),
            Width = snapshot.Width,
            Height = snapshot.Height,
            CapturedUtc = snapshot.CapturedUtc,
            Error = snapshot.Error,
        };
    }

    public void Dispose() => Stop();

    private void Stop()
    {
        _reports?.Dispose();
        _reports = null;
        _snapshots?.Dispose();
        _snapshots = null;
    }
}
