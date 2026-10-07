using System.Globalization;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Acap;

/// <summary>
/// "Applications (ACAP)..." task: install/upgrade an .eap, remove, start or stop an application on
/// the selected devices. Follows the device safety rule: API check against the cached list in
/// <see cref="CanRun"/>, fresh <c>Require</c> plus package/device validation before the first write.
/// </summary>
public sealed class AcapTaskPlugin : ITaskPlugin, ITaskPluginQuery
{
    public static readonly TimeSpan DefaultVerifyInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultVerifyTimeout = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _verifyInterval;
    private readonly TimeSpan _verifyTimeout;
    private readonly TimeProvider _time;

    public AcapTaskPlugin()
        : this(DefaultVerifyInterval, DefaultVerifyTimeout, TimeProvider.System)
    {
    }

    /// <param name="verifyInterval">Poll interval when the upload answer was lost and the install is verified via list.cgi.</param>
    /// <param name="verifyTimeout">How long to keep verifying.</param>
    public AcapTaskPlugin(TimeSpan verifyInterval, TimeSpan verifyTimeout, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(verifyInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(verifyTimeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _verifyInterval = verifyInterval;
        _verifyTimeout = verifyTimeout;
        _time = timeProvider;
    }

    public string Id => AcapPlugin.Id;

    public string DisplayName => "Applications (ACAP)";

    public string Group => TaskGroups.Applications;

    public string? IconKey => "plugin";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => true;

    /// <summary>Reachable with credentials, and the cached API list offers the Application API 1.x.</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status is DeviceStatus.Ok or DeviceStatus.Unknown
            && device.Apis.Supports(AcapPlugin.ApplicationApiId, AcapPlugin.ApplicationApiMinVersion);
    }

    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(method, AcapPlugin.ListApplicationsMethod, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unknown query method \"{method}\".");
        }

        // Read-only: API list, basicdeviceinfo, param.cgi list, config.cgi get, list.cgi.
        (await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false)).Require(AcapPlugin.ApplicationApiId, AcapPlugin.ApplicationApiMinVersion);
        var api = new ApplicationApiClient(ctx.Vapix);
        var facts = await api.GetDeviceFactsAsync(ct).ConfigureAwait(false);
        var apps = await api.ListAsync(ct).ConfigureAwait(false);
        return new ListApplicationsResult { Device = facts, Applications = apps }.ToJson();
    }

    /// <summary>Step names (see README.md).</summary>
    public static class Steps
    {
        public const string CheckCompatibility = "Check compatibility";
        public const string ReadPackage = "Read package";
        public const string ReadDeviceInfo = "Read device info";
        public const string ReadDevelopmentVersion = "Read embedded development version";
        public const string ReadUnsignedSetting = "Read unsigned application setting";
        public const string ReadApplications = "Read installed applications";
        public const string CheckPackage = "Check compatibility of package";
        public const string Upload = "Upload package";
        public const string VerifyInstallation = "Verify installation";
        public const string Start = "Start application";
        public const string Stop = "Stop application";
        public const string Remove = "Remove application";
        public const string VerifyState = "Verify application state";
        public const string VerifyRemoval = "Verify removal";
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = AcapPayload.Parse(payloadJson);
        ctx.PlanSteps(payload.Action switch
        {
            AcapAction.Install when payload.StartAfterInstall =>
                [Steps.CheckCompatibility, Steps.ReadPackage, Steps.ReadDeviceInfo, Steps.ReadDevelopmentVersion, Steps.ReadUnsignedSetting, Steps.ReadApplications, Steps.CheckPackage, Steps.Upload, Steps.VerifyInstallation, Steps.Start, Steps.VerifyState],
            AcapAction.Install =>
                [Steps.CheckCompatibility, Steps.ReadPackage, Steps.ReadDeviceInfo, Steps.ReadDevelopmentVersion, Steps.ReadUnsignedSetting, Steps.ReadApplications, Steps.CheckPackage, Steps.Upload, Steps.VerifyInstallation],
            AcapAction.Remove => [Steps.CheckCompatibility, Steps.ReadApplications, Steps.Remove, Steps.VerifyRemoval],
            AcapAction.Start => [Steps.CheckCompatibility, Steps.ReadApplications, Steps.Start, Steps.VerifyState],
            AcapAction.Stop => [Steps.CheckCompatibility, Steps.ReadApplications, Steps.Stop, Steps.VerifyState],
            _ => [Steps.CheckCompatibility],
        });

        using (var step = ctx.BeginStep(Steps.CheckCompatibility))
        {
            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            var api = apis.Require(AcapPlugin.ApplicationApiId, AcapPlugin.ApplicationApiMinVersion);
            ctx.Log(TaskLogLevel.Info, $"Device offers {api.Id} {api.Version}.");
            step.Complete($"{api.Id} {api.Version}");
        }

        var client = new ApplicationApiClient(ctx.Vapix);
        switch (payload.Action)
        {
            case AcapAction.Install:
                await InstallAsync(ctx, client, payload, ct).ConfigureAwait(false);
                break;
            case AcapAction.Remove:
                await RemoveAsync(ctx, client, payload.Application!, ct).ConfigureAwait(false);
                break;
            case AcapAction.Start:
            case AcapAction.Stop:
                await StartStopAsync(ctx, client, payload.Application!, payload.Action == AcapAction.Start, ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException("Unknown action. Nothing was changed.", nameof(payloadJson));
        }
    }

    private async Task InstallAsync(ITaskExecutionContext ctx, ApplicationApiClient client, AcapPayload payload, CancellationToken ct)
    {
        UploadedFile file;
        EapManifest package;
        using (var step = ctx.BeginStep(Steps.ReadPackage))
        {
            file = await ctx.Files.FindAsync(payload.FileId!, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The uploaded package is no longer available on the server. Nothing was changed.");
            if (payload.Sha256 is { Length: > 0 } sha && !string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The uploaded package does not match the one checked in the dialog. Nothing was changed.");
            }

            var read = await ctx.Files.OpenReadAsync(file.Id, ct).ConfigureAwait(false);
            await using (read.ConfigureAwait(false))
            {
                try
                {
                    package = await EapReader.ReadAsync(read, ct).ConfigureAwait(false);
                }
                catch (InvalidEapException ex)
                {
                    throw new InvalidOperationException(ex.Message + " Nothing was changed.", ex);
                }
            }

            if (payload.Application is { Length: > 0 } expectedName && !string.Equals(expectedName, package.AppName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The package is \"{package.AppName}\", the dialog checked \"{expectedName}\". Nothing was changed.");
            }

            ctx.Log(TaskLogLevel.Info, $"Package {package.AppName} {package.Version} ({package.Architecture ?? "any architecture"}) from {file.Name}, {file.Size} bytes, read from {package.Source}.");
            step.Complete($"{package.AppName} {package.Version}, {package.Architecture ?? "any architecture"}");
        }

        // One step per device request.
        BasicDeviceInfo info;
        using (var step = ctx.BeginStep(Steps.ReadDeviceInfo))
        {
            info = await ctx.Vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            step.Complete($"AXIS OS {info.Version}, {info.Architecture}");
        }

        string? embdev;
        using (var step = ctx.BeginStep(Steps.ReadDevelopmentVersion))
        {
            embdev = await client.TryGetEmbeddedDevelopmentVersionAsync(ct).ConfigureAwait(false);
            step.Complete(embdev ?? "Not reported");
        }

        bool? allowUnsigned;
        using (var step = ctx.BeginStep(Steps.ReadUnsignedSetting))
        {
            allowUnsigned = await client.TryGetAllowUnsignedAsync(ct).ConfigureAwait(false);
            step.Complete(allowUnsigned switch
            {
                true => "Unsigned applications allowed",
                false => "Only signed applications allowed",
                null => "Not reported",
            });
        }

        var facts = new AcapDeviceFacts
        {
            Architecture = info.Architecture,
            FirmwareVersion = info.Version,
            EmbeddedDevelopmentVersion = embdev,
            AllowUnsigned = allowUnsigned,
        };

        InstalledApplication? installed;
        using (var step = ctx.BeginStep(Steps.ReadApplications))
        {
            var before = await client.ListAsync(ct).ConfigureAwait(false);
            installed = before.FirstOrDefault(a => string.Equals(a.Name, package.AppName, StringComparison.Ordinal));
            step.Complete(installed is null
                ? $"{before.Count} installed, {package.AppName} not installed"
                : $"{before.Count} installed, {package.AppName} {installed.Version} ({installed.Status})");
        }

        AcapCompatibilityReport report;
        using (var step = ctx.BeginStep(Steps.CheckPackage))
        {
            report = AcapCompatibility.Check(package, facts, installed, payload.AllowDowngrade);
            if (!report.IsCompatible)
            {
                foreach (var problem in report.Problems)
                {
                    ctx.Log(TaskLogLevel.Error, problem);
                }

                throw new InvalidOperationException(string.Join(" ", report.Problems) + " Nothing was changed.");
            }

            foreach (var warning in report.Warnings)
            {
                ctx.Log(TaskLogLevel.Warning, warning);
            }

            ctx.Log(TaskLogLevel.Info, $"{report.KindText} on AXIS OS {facts.FirmwareVersion} ({facts.Architecture}).");
            step.Complete(report.KindText);
        }

        var answerLost = false;
        using (var step = ctx.BeginStep(Steps.Upload))
        {
            var upload = await ctx.Files.OpenReadAsync(file.Id, ct).ConfigureAwait(false);
            await using (upload.ConfigureAwait(false))
            {
                var totalMb = file.Size / (1024.0 * 1024);
                var lastPercent = -1;
                var progress = new SyncProgress<long>(bytes =>
                {
                    var percent = (int)(100 * Math.Min(1.0, file.Size <= 0 ? 0 : (double)bytes / file.Size));
                    if (percent > lastPercent)
                    {
                        lastPercent = percent;
                        step.ReportProgress(percent, percent >= 100
                            ? "The device installs the package"
                            : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.0} of {totalMb:0.0} MB"));
                    }
                });
                try
                {
                    await client.UploadAsync(upload, file.Name, progress, ct).ConfigureAwait(false);
                    step.Complete(string.Create(CultureInfo.InvariantCulture, $"{totalMb:0.0} MB, installed by the device"));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is TaskCanceledException or TimeoutException or HttpRequestException)
                {
                    // The device answers upload.cgi only after installing; slow installs can outlive the HTTP timeout.
                    answerLost = true;
                    ctx.Log(TaskLogLevel.Warning, "No answer to the upload in time (" + ex.Message + "); checking whether the installation completed.");
                    step.Complete("No answer in time; the installation is verified next");
                }
            }
        }

        InstalledApplication app;
        using (var step = ctx.BeginStep(Steps.VerifyInstallation))
        {
            app = await WaitForVersionAsync(client, package.AppName, package.Version, answerLost ? _verifyTimeout : TimeSpan.Zero, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(answerLost
                    ? $"The device did not confirm the installation and {package.AppName} {package.Version} is not in its application list."
                    : $"The device reported success but {package.AppName} {package.Version} is not in its application list.");

            ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} {app.Version} is installed ({app.Status}).");
            if (answerLost)
            {
                step.Warn($"The upload was not confirmed in time, but {package.AppName} {package.Version} is installed.");
            }
            else
            {
                var done = report.Kind switch
                {
                    InstallKind.Upgrade => "Upgraded",
                    InstallKind.Reinstall => "Reinstalled",
                    InstallKind.Downgrade => "Downgraded",
                    _ => "Installed",
                };
                step.Complete($"{done} {app.DisplayName} {app.Version} ({app.Status})");
            }
        }

        if (!payload.StartAfterInstall)
        {
            return;
        }

        if (app.IsRunning)
        {
            ctx.SkipStep(Steps.Start, $"{app.DisplayName} is already running.");
            ctx.SkipStep(Steps.VerifyState, $"{app.DisplayName} is already running.");
            return;
        }

        using (var step = ctx.BeginStep(Steps.Start))
        {
            await ControlTolerantAsync(client, AcapControlAction.Start, package.AppName, ct).ConfigureAwait(false);
            step.Complete(app.DisplayName);
        }

        using (var step = ctx.BeginStep(Steps.VerifyState))
        {
            var after = await FindAsync(client, package.AppName, ct).ConfigureAwait(false);
            if (after?.IsRunning == true)
            {
                ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} is running.");
                step.Complete($"{app.DisplayName} is running");
            }
            else
            {
                step.Warn($"{app.DisplayName} was installed but is {after?.Status ?? "not listed"} after the start command. Check its license and settings.");
            }
        }
    }

    private static async Task RemoveAsync(ITaskExecutionContext ctx, ApplicationApiClient client, string name, CancellationToken ct)
    {
        InstalledApplication? app;
        using (var step = ctx.BeginStep(Steps.ReadApplications))
        {
            app = await FindAsync(client, name, ct).ConfigureAwait(false);
            if (app is null)
            {
                step.Warn($"{name} is not installed on this device; nothing to remove.");
                ctx.SkipStep(Steps.Remove, "Not installed.");
                ctx.SkipStep(Steps.VerifyRemoval, "Not installed.");
                return;
            }

            if (app.Bundled)
            {
                throw new InvalidOperationException($"{app.DisplayName} is bundled with AXIS OS and cannot be removed. Nothing was changed.");
            }

            step.Complete($"{app.DisplayName} {app.Version} ({app.Status})");
        }

        using (var step = ctx.BeginStep(Steps.Remove))
        {
            ctx.Log(TaskLogLevel.Info, $"Removing {app.DisplayName} {app.Version} ({app.Status}).");
            await client.ControlAsync(AcapControlAction.Remove, name, ct).ConfigureAwait(false);
            step.Complete(app.DisplayName);
        }

        using (var step = ctx.BeginStep(Steps.VerifyRemoval))
        {
            if (await FindAsync(client, name, ct).ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException($"The device accepted the remove command but {app.DisplayName} is still installed.");
            }

            ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} removed.");
            step.Complete($"{app.DisplayName} is no longer installed");
        }
    }

    private static async Task StartStopAsync(ITaskExecutionContext ctx, ApplicationApiClient client, string name, bool start, CancellationToken ct)
    {
        var verb = start ? "Start" : "Stop";
        var control = start ? Steps.Start : Steps.Stop;
        var wanted = start ? "running" : "stopped";
        InstalledApplication? app;
        using (var step = ctx.BeginStep(Steps.ReadApplications))
        {
            app = await FindAsync(client, name, ct).ConfigureAwait(false);
            if (app is null)
            {
                step.Warn($"{name} is not installed on this device; skipped.");
                ctx.SkipStep(control, "Not installed.");
                ctx.SkipStep(Steps.VerifyState, "Not installed.");
                return;
            }

            step.Complete($"{app.DisplayName} {app.Version} ({app.Status})");
        }

        if (app.IsRunning == start)
        {
            ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} is already {wanted}.");
            ctx.SkipStep(control, $"{app.DisplayName} is already {wanted}.");
            ctx.SkipStep(Steps.VerifyState, $"{app.DisplayName} is already {wanted}.");
            return;
        }

        using (var step = ctx.BeginStep(control))
        {
            ctx.Log(TaskLogLevel.Info, $"{verb} {app.DisplayName} {app.Version}.");
            await ControlTolerantAsync(client, start ? AcapControlAction.Start : AcapControlAction.Stop, name, ct).ConfigureAwait(false);
            step.Complete(app.DisplayName);
        }

        using (var step = ctx.BeginStep(Steps.VerifyState))
        {
            var after = await FindAsync(client, name, ct).ConfigureAwait(false);
            if (after is null || after.IsRunning != start)
            {
                if (start)
                {
                    throw new InvalidOperationException($"{app.DisplayName} did not start (status {after?.Status ?? "unknown"}). Check its license and settings.");
                }

                throw new InvalidOperationException($"{app.DisplayName} did not stop (status {after?.Status ?? "unknown"}).");
            }

            ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} is {after.Status}.");
            step.Complete($"{app.DisplayName} is {after.Status}");
        }
    }

    /// <summary>Start/stop treating "already running" (6) and "not running" (7) as success.</summary>
    private static async Task ControlTolerantAsync(ApplicationApiClient client, AcapControlAction action, string name, CancellationToken ct)
    {
        try
        {
            await client.ControlAsync(action, name, ct).ConfigureAwait(false);
        }
        catch (AcapDeviceException ex) when ((action == AcapControlAction.Start && ex.Code == 6) || (action == AcapControlAction.Stop && ex.Code == 7))
        {
            // Already in the requested state.
        }
    }

    private static async Task<InstalledApplication?> FindAsync(ApplicationApiClient client, string name, CancellationToken ct) =>
        (await client.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    private async Task<InstalledApplication?> WaitForVersionAsync(ApplicationApiClient client, string name, string version, TimeSpan timeout, CancellationToken ct)
    {
        var start = _time.GetTimestamp();
        while (true)
        {
            try
            {
                var app = await FindAsync(client, name, ct).ConfigureAwait(false);
                if (app is not null && AcapCompatibility.CompareVersions(app.Version, version) == 0)
                {
                    return app;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && timeout > TimeSpan.Zero && ex is TaskCanceledException or TimeoutException or HttpRequestException)
            {
                // The device may still be busy installing; keep polling until the timeout.
            }

            if (_time.GetElapsedTime(start) >= timeout)
            {
                return null;
            }

            await Task.Delay(_verifyInterval, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous IProgress (Progress&lt;T&gt; would post to the thread pool and reorder reports).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
