using System.Globalization;

using Oadm.Plugins.Shared;
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

    /// <summary>Installing, downgrading, starting and stopping, and removing applications are rights of their own.</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(AcapPermissions.Install, "Install and upgrade applications", "Install applications on devices or upgrade them."),
        new(AcapPermissions.Downgrade, "Downgrade applications", "Install an older version of an application.") { OperatorDefault = false },
        new(AcapPermissions.StartStop, "Start and stop applications", "Start and stop applications that are installed."),
        new(AcapPermissions.Remove, "Remove applications", "Delete applications from devices; their settings are lost.") { OperatorDefault = false },
        new(AcapPermissions.License, "Install license keys", "Install license key files for applications."),
        new(AcapPermissions.Settings, "Change application settings", "Change the settings applications register on devices."),
    ];

    /// <summary>The permission of the run's action; an install with "Allow downgrade" also needs "downgrade".</summary>
    public IReadOnlyList<string>? RequiredPermissions(string? payloadJson)
    {
        var payload = AcapPayload.Parse(payloadJson);
        var license = payload.LicenseKeys.Count > 0 ? new[] { AcapPermissions.License } : [];
        return payload.Action switch
        {
            AcapAction.Install when payload.AllowDowngrade => [AcapPermissions.Install, AcapPermissions.Downgrade, .. license],
            AcapAction.Install => [AcapPermissions.Install, .. license],
            AcapAction.InstallLicense => [AcapPermissions.License],
            AcapAction.Start or AcapAction.Stop => [AcapPermissions.StartStop],
            AcapAction.Remove => [AcapPermissions.Remove],
            AcapAction.Settings => [AcapPermissions.Settings],
            _ => null,
        };
    }

    /// <summary>"Install AXIS Video Motion Detection 4.5.2", "Upgrade ... to ...", "Remove ...", "Start ...", "Stop ...".</summary>
    public string GetTaskName(string? payloadJson)
    {
        try
        {
            return AcapPayload.Parse(payloadJson).TaskName();
        }
        catch (ArgumentException)
        {
            return DisplayName;
        }
    }

    private static readonly Version MinApplicationApi = Version.Parse(AcapPlugin.ApplicationApiMinVersion);

    /// <summary>Reachable with credentials, and the cached API list offers the Application API 1.x (cheap: called for every device).</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status is DeviceStatus.Ok or DeviceStatus.Unknown
            && CachedApiCheck.Supports(device.Apis, AcapPlugin.ApplicationApiId, MinApplicationApi);
    }

    /// <summary>Plain-language reason for the greyed menu entry when <see cref="CanRun"/> is false (cached data only).</summary>
    public string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device) ?? TaskSupportReasons.NeedsApi("the application API", device);
    }

    /// <summary>The user's query (SDK 0.4.0): reads only the queried device, which the host checked against the user's view.</summary>
    public Task<string?> QueryAsync(ITaskQueryContext ctx, ICallContext caller, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        QueryAsync(ctx, device, method, payloadJson, ct);

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
        public const string ReadDevelopmentVersion = "Read SDK version";
        public const string ReadUnsignedSetting = "Read unsigned application setting";
        public const string ReadApplications = "Read installed applications";
        public const string CheckPackage = "Check package";
        public const string Upload = "Upload package";
        public const string VerifyInstallation = "Verify installation";
        public const string Start = "Start application";
        public const string Stop = "Stop application";
        public const string Remove = "Remove application";
        public const string VerifyState = "Verify application state";
        public const string VerifyRemoval = "Verify removal";
        public const string CheckLicense = "Check license key";
        public const string InstallLicense = "Install license key";
        public const string VerifyLicense = "Verify license";
        public const string ReadSettings = "Read settings";
        public const string ChangeSettings = "Change settings";
        public const string VerifySettings = "Verify settings";

        /// <summary>A device of the selection without a key in the chosen files.</summary>
        public const string NoKey = LicenseKeyMatcher.NoKeyText;
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = AcapPayload.ParseShared(payloadJson);
        string[] license = payload.LicenseKeys.Count > 0 ? [Steps.CheckLicense, Steps.InstallLicense, Steps.VerifyLicense] : [];
        string[] install = [Steps.CheckCompatibility, Steps.ReadPackage, Steps.ReadDeviceInfo, Steps.ReadDevelopmentVersion, Steps.ReadUnsignedSetting, Steps.ReadApplications, Steps.CheckPackage, Steps.Upload, Steps.VerifyInstallation, .. license];
        string[] planned = payload.Action switch
        {
            AcapAction.Install when payload.StartAfterInstall => [.. install, Steps.Start, Steps.VerifyState],
            AcapAction.Install => install,
            AcapAction.InstallLicense => [Steps.CheckCompatibility, Steps.ReadApplications, .. license],
            AcapAction.Remove => [Steps.CheckCompatibility, Steps.ReadApplications, Steps.Remove, Steps.VerifyRemoval],
            AcapAction.Start => [Steps.CheckCompatibility, Steps.ReadApplications, Steps.Start, Steps.VerifyState],
            AcapAction.Stop => [Steps.CheckCompatibility, Steps.ReadApplications, Steps.Stop, Steps.VerifyState],
            AcapAction.Settings => [Steps.CheckCompatibility, Steps.ReadApplications, Steps.ReadSettings, Steps.ChangeSettings, Steps.VerifySettings],
            _ => [Steps.CheckCompatibility],
        };
        ctx.PlanSteps(planned);

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
                await InstallAsync(ctx, device, client, payload, planned, ct).ConfigureAwait(false);
                break;
            case AcapAction.InstallLicense:
                await InstallLicenseOnlyAsync(ctx, device, client, payload, ct).ConfigureAwait(false);
                break;
            case AcapAction.Remove:
                await RemoveAsync(ctx, client, payload.Application!, _verifyInterval, _verifyTimeout, _time, ct).ConfigureAwait(false);
                break;
            case AcapAction.Start:
            case AcapAction.Stop:
                await StartStopAsync(ctx, client, payload.Application!, payload.Action == AcapAction.Start, ct).ConfigureAwait(false);
                break;
            case AcapAction.Settings:
                await ChangeSettingsAsync(ctx, client, payload, ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException("Unknown action. Nothing was changed.", nameof(payloadJson));
        }
    }

    private async Task InstallAsync(ITaskExecutionContext ctx, IDeviceInfo device, ApplicationApiClient client, AcapPayload payload, string[] planned, CancellationToken ct)
    {
        // Every package of the payload is read and checked first: one application and version, one per architecture.
        var entries = payload.AllFiles();
        var packages = new List<(UploadedFile File, EapManifest Package)>(entries.Count);
        using (var step = ctx.BeginStep(Steps.ReadPackage))
        {
            foreach (var entry in entries)
            {
                packages.Add(await ReadPackageAsync(ctx, entry, payload.Application, ct).ConfigureAwait(false));
            }

            var first = packages[0].Package;
            if (packages.Count > 1)
            {
                if (AcapCompatibility.CheckPackageSet([.. packages.Select(p => (p.File.Name, p.Package))]) is { } refused)
                {
                    throw new InvalidOperationException(refused + " Nothing was changed.");
                }

                step.Complete($"{first.AppName} {first.Version}, {string.Join(" and ", packages.Select(p => p.Package.Architecture))}");
            }
            else
            {
                step.Complete($"{first.AppName} {first.Version}, {first.Architecture ?? "any architecture"}");
            }
        }

        // One step per device request. The package is chosen again from the fresh architecture.
        BasicDeviceInfo info;
        UploadedFile file;
        EapManifest package;
        using (var step = ctx.BeginStep(Steps.ReadDeviceInfo))
        {
            info = await ctx.Vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            var index = AcapCompatibility.ChoosePackage([.. packages.Select(p => p.Package.Architecture)], info.Architecture);
            if (index < 0)
            {
                // Several packages, none for this device: skipped, nothing changed.
                var reason = AcapCompatibility.NoPackageFor(info.Architecture);
                ctx.Log(TaskLogLevel.Warning, $"{reason} The packages are for {string.Join(", ", packages.Select(p => p.Package.Architecture))}.");
                step.Warn(reason + " Nothing was changed.");
                foreach (var name in planned.SkipWhile(n => n != Steps.ReadDeviceInfo).Skip(1))
                {
                    ctx.SkipStep(name, reason);
                }

                return;
            }

            (file, package) = packages[index];
            if (packages.Count > 1)
            {
                ctx.Log(TaskLogLevel.Info, $"Uses the {package.Architecture} package {file.Name}.");
                step.Complete($"AXIS OS {info.Version}, {info.Architecture}: {package.Architecture} package");
            }
            else
            {
                step.Complete($"AXIS OS {info.Version}, {info.Architecture}");
            }
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
        IReadOnlyList<InstalledApplication> installedApps;
        using (var step = ctx.BeginStep(Steps.ReadApplications))
        {
            var before = await client.ListAsync(ct).ConfigureAwait(false);
            installedApps = before;
            installed = before.FirstOrDefault(a => string.Equals(a.Name, package.AppName, StringComparison.Ordinal));
            step.Complete(installed is null
                ? $"{before.Count} installed, {package.AppName} not installed"
                : $"{before.Count} installed, {package.AppName} {installed.Version} ({installed.Status})");
        }

        AcapCompatibilityReport report;
        using (var step = ctx.BeginStep(Steps.CheckPackage))
        {
            report = AcapCompatibility.Check(package, facts, installed, payload.AllowDowngrade, installedApps);
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
                catch (Exception ex) when (!ct.IsCancellationRequested && (ex is TaskCanceledException or TimeoutException or HttpRequestException || ex is AcapDeviceException { OutcomeUnknown: true }))
                {
                    // The device answers upload.cgi only after installing; slow installs can outlive the HTTP timeout, and
                    // error 15 says the device itself does not know the outcome.
                    answerLost = true;
                    ctx.Log(TaskLogLevel.Warning, "No answer to the upload in time (" + ex.Message + "); checking whether the installation completed.");
                    step.Complete("No answer in time; the installation is verified next");
                }
                catch (AcapDeviceException ex) when (ex.Code == 2 && allowUnsigned != false)
                {
                    throw new AcapDeviceException(2, "The package signature is not valid. Download the package again. (error 2)");
                }
                catch (AcapDeviceException ex) when (installed is not null)
                {
                    // VAPIX: a failed or interrupted upgrade removes both versions. Say so when it happened.
                    if (await TryFindAsync(client, package.AppName, ct).ConfigureAwait(false) is false)
                    {
                        ctx.Log(TaskLogLevel.Warning, $"{installed.DisplayName} is no longer installed after the failed upgrade.");
                        throw new AcapDeviceException(ex.Code, $"{ex.Message} The device removed {installed.DisplayName} too: install it again; its settings come back.");
                    }

                    throw;
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

        if (payload.LicenseKeys.Count > 0)
        {
            // The application is installed: a key problem of this device is a warning, never a failure.
            if (await InstallLicenseAsync(ctx, device, client, app, payload, failOnProblem: false, ct).ConfigureAwait(false) is { } licensed)
            {
                app = licensed;
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
            var after = await WaitForStateAsync(client, package.AppName, running: true, ct).ConfigureAwait(false);
            if (after?.IsRunning == true)
            {
                ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} is running.");
                step.Complete($"{app.DisplayName} is running");
            }
            else
            {
                step.Warn($"{app.DisplayName} was installed but did not start: {NotStartedReason(after)}");
            }
        }
    }

    /// <summary>One uploaded package: still there, the SHA-256 the dialog checked, a readable .eap of the expected application.</summary>
    private static async Task<(UploadedFile File, EapManifest Package)> ReadPackageAsync(ITaskExecutionContext ctx, AcapPackageFile entry, string? expectedName, CancellationToken ct)
    {
        var file = await ctx.Files.FindAsync(entry.FileId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The uploaded package is no longer available on the server. Nothing was changed.");
        if (entry.Sha256 is { Length: > 0 } sha && !string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The uploaded package does not match the one checked in the dialog. Nothing was changed.");
        }

        EapManifest package;
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

        if (expectedName is { Length: > 0 } && !string.Equals(expectedName, package.AppName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The package is \"{package.AppName}\", the dialog checked \"{expectedName}\". Nothing was changed.");
        }

        if (entry.Architecture is { Length: > 0 } declared && !AcapCompatibility.IsArchitectureIndependent(declared)
            && (AcapCompatibility.IsArchitectureIndependent(package.Architecture)
                || AcapCompatibility.NormalizeArchitecture(declared) != AcapCompatibility.NormalizeArchitecture(package.Architecture!)))
        {
            throw new InvalidOperationException($"{file.Name} is not the {declared} package the dialog checked. Nothing was changed.");
        }

        ctx.Log(TaskLogLevel.Info, $"Package {package.AppName} {package.Version} ({package.Architecture ?? "any architecture"}) from {file.Name}, {file.Size} bytes, read from {package.Source}.");
        return (file, package);
    }

    /// <summary>
    /// Change settings: the application is installed, every name is one of its settings and every value fits the type and
    /// range, else Failed with nothing changed; only values that differ are sent, in one request; then read back (passwords are
    /// never read back). Values never reach a log or a step text, only names.
    /// </summary>
    private static async Task ChangeSettingsAsync(ITaskExecutionContext ctx, ApplicationApiClient client, AcapPayload payload, CancellationToken ct)
    {
        var name = payload.Application!;
        InstalledApplication app;
        using (var step = ctx.BeginStep(Steps.ReadApplications))
        {
            app = await FindAsync(client, name, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"{payload.AppDisplayName ?? name} is not installed on this device. Nothing was changed.");
            step.Complete($"{app.DisplayName} {app.Version} ({app.Status})");
        }

        var values = payload.Values!;
        Dictionary<string, string> changed;
        IReadOnlyList<AppSettingDefinition> definitions;
        HashSet<string> passwordNames;
        using (var step = ctx.BeginStep(Steps.ReadSettings))
        {
            definitions = await client.ReadSettingsAsync(name, ct).ConfigureAwait(false);
            if (definitions.Count == 0)
            {
                throw new InvalidOperationException($"{app.DisplayName} has no settings here. Nothing was changed.");
            }

            var problems = AppSettings.Check(definitions, values, app.DisplayName, out var normalized);
            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                {
                    ctx.Log(TaskLogLevel.Error, problem);
                }

                throw new InvalidOperationException(string.Join(" ", problems) + " Nothing was changed.");
            }

            var current = new Dictionary<string, AppSettingDefinition>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                current.TryAdd(definition.Name, definition);
            }

            passwordNames = [.. current.Values.Where(d => d.Kind == AppSettingKind.Password).Select(d => d.Name)];
            changed = normalized
                .Where(v => current[v.Key].Kind == AppSettingKind.Password || !string.Equals(current[v.Key].Value, v.Value, StringComparison.Ordinal))
                .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
            step.Complete(changed.Count == values.Count
                ? Count(definitions.Count, "setting")
                : string.Create(CultureInfo.InvariantCulture, $"{Count(definitions.Count, "setting")}, {values.Count - changed.Count:N0} already set"));
        }

        if (changed.Count == 0)
        {
            ctx.Log(TaskLogLevel.Info, $"Every setting of {app.DisplayName} already has its new value.");
            ctx.SkipStep(Steps.ChangeSettings, "Every value is already set.");
            ctx.SkipStep(Steps.VerifySettings, "Every value is already set.");
            return;
        }

        var names = string.Join(", ", changed.Keys.Order(StringComparer.Ordinal));
        using (var step = ctx.BeginStep(Steps.ChangeSettings))
        {
            ctx.Log(TaskLogLevel.Info, $"Changing settings of {app.DisplayName}: {names}.");
            await client.UpdateSettingsAsync(name, changed, ct).ConfigureAwait(false);
            step.Complete(names);
        }

        using (var step = ctx.BeginStep(Steps.VerifySettings))
        {
            var after = new Dictionary<string, AppSettingDefinition>(StringComparer.Ordinal);
            foreach (var definition in await client.ReadSettingsAsync(name, ct).ConfigureAwait(false))
            {
                after.TryAdd(definition.Name, definition);
            }

            var passwords = changed.Keys.Count(passwordNames.Contains);
            var differ = changed
                .Where(v => !(after.TryGetValue(v.Key, out var d) && (d.Kind == AppSettingKind.Password || SameValue(d, v.Value))))
                .Select(v => v.Key)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (differ.Count > 0)
            {
                throw new InvalidOperationException($"The device did not keep the new value of {string.Join(", ", differ)}.");
            }

            ctx.Log(TaskLogLevel.Info, $"Settings of {app.DisplayName} changed: {names}.");
            step.Complete(passwords == 0
                ? Count(changed.Count, "setting") + " changed"
                : string.Create(CultureInfo.InvariantCulture, $"{Count(changed.Count, "setting")} changed, {Count(passwords, "password")} not read back"));
        }
    }

    /// <summary>The device kept the value (numbers by value, yes/no and list values as the device spells them).</summary>
    private static bool SameValue(AppSettingDefinition definition, string sent) =>
        string.Equals(definition.Value, sent, StringComparison.Ordinal)
        || (AppSettings.TryNormalize(definition, definition.Value, out var kept, out _) && string.Equals(kept, sent, StringComparison.Ordinal));

    private static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n:N0} {noun}{(n == 1 ? string.Empty : "s")}");

    private async Task InstallLicenseOnlyAsync(ITaskExecutionContext ctx, IDeviceInfo device, ApplicationApiClient client, AcapPayload payload, CancellationToken ct)
    {
        var name = payload.Application!;
        InstalledApplication? app;
        using (var step = ctx.BeginStep(Steps.ReadApplications))
        {
            app = await FindAsync(client, name, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"{payload.AppDisplayName ?? name} is not installed on this device. Nothing was changed.");
            step.Complete($"{app.DisplayName} {app.Version}, license {LicenseText(app)}");
        }

        await InstallLicenseAsync(ctx, device, client, app, payload, failOnProblem: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Check license key, Install license key, Verify license. The key must name this device's MAC address and the installed
    /// application and must not be expired; it is checked before anything is sent. Without a key for this device: a warning,
    /// nothing sent. Another problem fails the task (license only) or is a warning (after an install). Returns the
    /// application as listed after the key, null when no key was installed.
    /// </summary>
    private async Task<InstalledApplication?> InstallLicenseAsync(
        ITaskExecutionContext ctx, IDeviceInfo device, ApplicationApiClient client, InstalledApplication app, AcapPayload payload, bool failOnProblem, CancellationToken ct)
    {
        LicenseKey? key = null;
        using (var step = ctx.BeginStep(Steps.CheckLicense))
        {
            var mac = LicenseKey.NormalizeMac(device.Serial);
            var entry = mac is null ? null : FittingKey(payload.KeysFor(mac), app.Version);
            string? problem = null;
            if (entry is null)
            {
                step.Warn(Steps.NoKey + " Nothing was sent.");
            }
            else
            {
                try
                {
                    key = LicenseKey.Parse(entry.Xml);
                    problem = KeyProblem(key, mac!, app, DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime), payload.LicenseApplicationId);
                }
                catch (FormatException ex)
                {
                    problem = ex.Message;
                }

                if (problem is not null)
                {
                    key = null;
                    if (failOnProblem)
                    {
                        throw new InvalidOperationException(problem + " Nothing was changed.");
                    }

                    step.Warn(problem + " The key was not installed.");
                }
                else
                {
                    step.Complete($"Key for {app.DisplayName}, {key!.ValidityText}");
                }
            }
        }

        if (key is null)
        {
            ctx.SkipStep(Steps.InstallLicense, "No usable key.");
            ctx.SkipStep(Steps.VerifyLicense, "No usable key.");
            return null;
        }

        using (var step = ctx.BeginStep(Steps.InstallLicense))
        {
            try
            {
                await client.UploadLicenseKeyAsync(app.Name, key.Xml, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!failOnProblem && !ct.IsCancellationRequested && ex is AcapDeviceException or UnauthorizedAccessException or TaskCanceledException or TimeoutException or HttpRequestException)
            {
                step.Warn(ex.Message);
                ctx.SkipStep(Steps.VerifyLicense, "The key was not installed.");
                return null;
            }

            ctx.Log(TaskLogLevel.Info, $"License key for {app.DisplayName} installed ({key.ValidityText}).");
            step.Complete(app.DisplayName);
        }

        using (var step = ctx.BeginStep(Steps.VerifyLicense))
        {
            var after = await FindAsync(client, app.Name, ct).ConfigureAwait(false);
            if (after is not null && string.Equals(after.License, "Valid", StringComparison.OrdinalIgnoreCase))
            {
                step.Complete($"{after.DisplayName}: license {LicenseText(after)}");
            }
            else
            {
                step.Warn($"The device took the key, but shows the license of {app.DisplayName} as {after?.License ?? "unknown"}.");
            }

            return after;
        }
    }

    /// <summary>The key that fits the installed version, else the first (its problem then says why).</summary>
    private static LicenseKeyEntry? FittingKey(IReadOnlyList<LicenseKeyEntry> entries, string? installedVersion)
    {
        foreach (var entry in entries)
        {
            try
            {
                if (LicenseKey.Parse(entry.Xml).Fits(installedVersion))
                {
                    return entry;
                }
            }
            catch (FormatException)
            {
            }
        }

        return entries.Count > 0 ? entries[0] : null;
    }

    /// <summary>
    /// Why <paramref name="key"/> cannot be installed for <paramref name="app"/> on the device <paramref name="mac"/>; null when it
    /// can. The application id is the device's, else <paramref name="expectedApplicationId"/> (the package's or the chosen one).
    /// </summary>
    public static string? KeyProblem(LicenseKey key, string mac, InstalledApplication app, DateOnly today, string? expectedApplicationId = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(app);
        if (key.DeviceId != mac)
        {
            return $"The key is for the device {key.DeviceId}, not {mac}.";
        }

        var id = app.ApplicationId is { Length: > 0 } reported && reported != "0" ? reported
            : expectedApplicationId is { Length: > 0 } expected && expected != "0" ? expected
            : null;
        if (id is not null && key.ApplicationId != id)
        {
            return $"The key is for another application (ID {key.ApplicationId}; {app.DisplayName} has ID {id}).";
        }

        if (key.IsExpired(today))
        {
            return $"The key expired on {key.ExpirationDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.";
        }

        return key.Fits(app.Version) ? null : $"The key is for {key.VersionRangeText} of {app.DisplayName}; {app.Version} is installed.";
    }

    /// <summary>"Valid until 2027-09-18", "Not required", ... (<see cref="InstalledApplication.LicenseText"/>).</summary>
    public static string LicenseText(InstalledApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.LicenseText;
    }

    private static async Task RemoveAsync(ITaskExecutionContext ctx, ApplicationApiClient client, string name, TimeSpan interval, TimeSpan timeout, TimeProvider time, CancellationToken ct)
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

        var answerLost = false;
        using (var step = ctx.BeginStep(Steps.Remove))
        {
            ctx.Log(TaskLogLevel.Info, $"Removing {app.DisplayName} {app.Version} ({app.Status}).");
            try
            {
                await client.ControlAsync(AcapControlAction.Remove, name, ct).ConfigureAwait(false);
                step.Complete(app.DisplayName);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (ex is TaskCanceledException or TimeoutException or HttpRequestException || ex is AcapDeviceException { OutcomeUnknown: true }))
            {
                // The device may still be removing it (or says the outcome is unknown): the next step looks for it until it is gone.
                answerLost = true;
                ctx.Log(TaskLogLevel.Warning, "No answer to the remove command in time (" + ex.Message + "); checking whether the application is gone.");
                step.Complete("No answer in time; the removal is verified next");
            }
        }

        using (var step = ctx.BeginStep(Steps.VerifyRemoval))
        {
            var start = time.GetTimestamp();
            while (await IsInstalledAsync(client, name, answerLost, ct).ConfigureAwait(false) != false)
            {
                if (!answerLost || time.GetElapsedTime(start) >= timeout)
                {
                    throw new InvalidOperationException(answerLost
                        ? $"The device did not confirm the removal and {app.DisplayName} is still installed."
                        : $"The device accepted the remove command but {app.DisplayName} is still installed.");
                }

                await Task.Delay(interval, time, ct).ConfigureAwait(false);
            }

            ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} removed.");
            if (answerLost)
            {
                step.Warn($"The remove command was not confirmed in time, but {app.DisplayName} is no longer installed.");
            }
            else
            {
                step.Complete($"{app.DisplayName} is no longer installed");
            }
        }
    }

    private async Task StartStopAsync(ITaskExecutionContext ctx, ApplicationApiClient client, string name, bool start, CancellationToken ct)
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
            var after = await WaitForStateAsync(client, name, start, ct).ConfigureAwait(false);
            if (after is null || after.IsRunning != start)
            {
                if (start)
                {
                    throw new InvalidOperationException($"{app.DisplayName} did not start: {NotStartedReason(after)}");
                }

                throw new InvalidOperationException($"{app.DisplayName} did not stop (status {after?.Status ?? "unknown"}).");
            }

            ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} is {after.Status}.");
            step.Complete($"{app.DisplayName} is {after.Status}");
        }
    }

    /// <summary>
    /// Whether the application is listed; null while polling and the device does not answer (it may still be busy removing).
    /// </summary>
    private static async Task<bool?> IsInstalledAsync(ApplicationApiClient client, string name, bool polling, CancellationToken ct)
    {
        try
        {
            return await FindAsync(client, name, ct).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (polling && !ct.IsCancellationRequested && ex is TaskCanceledException or TimeoutException or HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Start/stop treating "already running" (6) and "not running" (7) as success, and an unknown outcome (15, no answer) as
    /// something the next step verifies.
    /// </summary>
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
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is AcapDeviceException { OutcomeUnknown: true } or TaskCanceledException or TimeoutException or HttpRequestException))
        {
            // The device may still be starting or stopping it: the state is verified next.
        }
    }

    /// <summary>Polls until the application is (not) running, at most 15 s (an application needs a moment to start).</summary>
    private async Task<InstalledApplication?> WaitForStateAsync(ApplicationApiClient client, string name, bool running, CancellationToken ct)
    {
        var start = _time.GetTimestamp();
        var limit = _verifyTimeout < TimeSpan.FromSeconds(15) ? _verifyTimeout : TimeSpan.FromSeconds(15);
        while (true)
        {
            var app = await FindAsync(client, name, ct).ConfigureAwait(false);
            if (app is null || app.IsRunning == running || _time.GetElapsedTime(start) >= limit)
            {
                return app;
            }

            await Task.Delay(_verifyInterval, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Why an application is not running: its license when that is the reason, else its status.</summary>
    public static string NotStartedReason(InstalledApplication? app) => app switch
    {
        null => "it is no longer installed.",
        { License: "Missing" } => "its license is missing. Install a license key.",
        { License: "Invalid" } => "its license is not valid. Install a new license key.",
        _ => $"its status is {app.Status ?? "unknown"}. Check its settings.",
    };

    /// <summary>Whether the application is listed; null when the list cannot be read.</summary>
    private static async Task<bool?> TryFindAsync(ApplicationApiClient client, string name, CancellationToken ct)
    {
        try
        {
            return await FindAsync(client, name, ct).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is AcapDeviceException or TaskCanceledException or TimeoutException or HttpRequestException)
        {
            return null;
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
