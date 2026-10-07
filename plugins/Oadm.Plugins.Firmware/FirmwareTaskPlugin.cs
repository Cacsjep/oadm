using System.Globalization;
using System.Text;
using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Firmware;

/// <summary>Timings of the upgrade task. Production defaults are generous; tests shrink them.</summary>
public sealed record FirmwareTaskTimings
{
    /// <summary>Interval between "is it back?" probes while the device installs and reboots.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Time allowed from the end of the upload until the device answers with the new version.</summary>
    public TimeSpan RestartTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Timeout of one probe request.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Requested timeout of the upload request (the device verifies the image before it answers).</summary>
    public TimeSpan UploadTimeout { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Delay between commit attempts.</summary>
    public TimeSpan CommitRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    public int CommitAttempts { get; init; } = 3;

    /// <summary>
    /// autoRollback for upgrades that keep the settings: the device reverts by itself if OADM never
    /// verifies and commits (OADM crashed, network lost). Longer than <see cref="RestartTimeout"/>.
    /// </summary>
    public int AutoRollbackMinutes { get; init; } = 30;
}

/// <summary>
/// "Upgrade firmware..." task: installs an uploaded AXIS OS image through the fwmgr API, waits for the
/// device to restart, verifies the new version and commits it. See README.md for the decision table.
/// </summary>
public sealed class FirmwareTaskPlugin : ITaskPlugin, ITaskPluginQuery
{
    public const string PluginId = "oadm.firmware";
    public const string StatusQuery = "status";

    private readonly FirmwareTaskTimings _timings;
    private readonly TimeProvider _time;

    public FirmwareTaskPlugin()
        : this(new FirmwareTaskTimings(), TimeProvider.System)
    {
    }

    public FirmwareTaskPlugin(FirmwareTaskTimings timings, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timings.PollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timings.RestartTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(timings.CommitAttempts, 1);
        _timings = timings;
        _time = timeProvider;
    }

    public string Id => PluginId;

    public string DisplayName => "Upgrade firmware...";

    public string? IconKey => "firmware";

    public bool ShowInToolbar => true;

    public bool RequiresDialog => true;

    /// <summary>Only devices that are reachable with working credentials and offer fwmgr 1.x.</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status == DeviceStatus.Ok && device.Apis.Supports(FwmgrClient.ApiId, FwmgrClient.MinApiVersion);
    }

    /// <summary>Step names, in order (see README.md).</summary>
    public static class Steps
    {
        public const string CheckCompatibility = "Check compatibility";
        public const string ReadDeviceInfo = "Read device info";
        public const string ValidateFile = "Validate file";
        public const string ReadFirmwareStatus = "Read firmware status";
        public const string Upload = "Upload firmware";
        public const string Install = "Install firmware";
        public const string WaitForDevice = "Wait for device to come back";
        public const string VerifyVersion = "Verify version";
        public const string ReadCommitState = "Read commit state";
        public const string Commit = "Commit firmware";
        public const string WaitBeforeRetry = "Wait before retrying the commit";

        public static readonly string[] Planned =
            [CheckCompatibility, ReadDeviceInfo, ValidateFile, ReadFirmwareStatus, Upload, Install, WaitForDevice, VerifyVersion, ReadCommitState, Commit];
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);

        // 1. Validate everything before the first request that changes the device.
        var payload = FirmwarePayload.Parse(payloadJson);
        ctx.PlanSteps(Steps.Planned);

        using (var step = ctx.BeginStep(Steps.CheckCompatibility))
        {
            if (device.Status != DeviceStatus.Ok)
            {
                throw new InvalidOperationException($"The device status is {device.Status}; firmware is only installed on devices with status OK. Nothing was changed.");
            }

            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            apis.Require(FwmgrClient.ApiId, FwmgrClient.MinApiVersion);
            step.Complete("fwmgr " + (apis.FindApi(FwmgrClient.ApiId, 1)?.Version ?? FwmgrClient.MinApiVersion));
        }

        BasicDeviceInfo info;
        using (var step = ctx.BeginStep(Steps.ReadDeviceInfo))
        {
            info = await ctx.Vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            step.Complete($"AXIS {info.ProdNbr}, AXIS OS {info.Version}");
        }

        UploadedFile file;
        FirmwareImageInfo image;
        using (var step = ctx.BeginStep(Steps.ValidateFile))
        {
            file = await ctx.Files.FindAsync(payload.FileId, ct).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The uploaded firmware file is no longer available on the server. Upload it again. Nothing was changed.");
            var header = await ReadHeaderAsync(ctx.Files, file.Id, ct).ConfigureAwait(false);
            image = FirmwareImageInspector.Inspect(payload.FileName ?? file.Name, file.Size, header);
            var check = FirmwareCompatibility.Evaluate(info.ProdNbr, info.Version, image, payload.FactoryDefaultMode, payload.AllowDowngrade);
            ctx.Log(TaskLogLevel.Info, Invariant($"Device {info.ProdNbr} runs {info.Version}; file {image.FileName} ({file.Size} bytes, SHA-256 {file.Sha256}): {check.Message}"));
            if (check.IsNoOp)
            {
                step.Warn($"Already up to date: the device runs {info.Version}. Nothing was installed.");
                SkipRemaining(ctx, "Already up to date.");
                return;
            }

            if (!check.WillInstall)
            {
                throw new InvalidOperationException(check.Message + " Nothing was changed.");
            }

            step.Complete(Invariant($"{image.FileName}, {file.Size / (1024 * 1024)} MB: {check.Message}"));
        }

        var fwmgr = new FwmgrClient(ctx.Vapix);
        using (var step = ctx.BeginStep(Steps.ReadFirmwareStatus))
        {
            var before = await fwmgr.GetStatusAsync(ct).ConfigureAwait(false);
            if (before.HasUncommittedUpgrade)
            {
                throw new InvalidOperationException(
                    $"A previous firmware upgrade on this device is not committed yet (active {before.ActiveFirmwareVersion}, previous {before.InactiveFirmwareVersion ?? "unknown"}). Wait for it to finish or commit it first. Nothing was changed.");
            }

            step.Complete("AXIS OS " + (before.ActiveFirmwareVersion ?? info.Version) + ", committed");
        }

        // 2. Upload. From here on the device may change.
        var newVersionText = await UploadAsync(ctx, fwmgr, file, image, payload.FactoryDefaultMode, info.Version, ct).ConfigureAwait(false);
        var expected = AxisOsVersion.TryParse(newVersionText) ?? image.Version;
        var old = AxisOsVersion.TryParse(info.Version);

        // 3. Wait for the restart and verify the version.
        var backVersionText = await WaitForRestartAsync(ctx, info.Version, payload.FactoryDefaultMode, ct).ConfigureAwait(false);
        AxisOsVersion actual;
        using (var step = ctx.BeginStep(Steps.VerifyVersion))
        {
            var parsed = AxisOsVersion.TryParse(backVersionText);
            if (parsed is null || (expected is not null && parsed != expected) || (expected is null && parsed == old))
            {
                if (parsed is not null && parsed == old)
                {
                    throw new InvalidOperationException(
                        $"The device came back with its previous firmware {info.Version}: the new firmware{(expected is null ? string.Empty : " " + expected)} did not start and the device rolled back. Settings are unchanged.");
                }

                throw new InvalidOperationException(
                    $"The device reports firmware {backVersionText ?? "unknown"} after the upgrade, expected {expected?.Text ?? "a new version"}. Check the device; the previous firmware {info.Version} is kept for rollback.");
            }

            actual = parsed;
            step.Complete($"AXIS OS {actual}");
        }

        // 4. Commit (only for upgrades that keep the settings; factory default upgrades commit themselves).
        if (payload.FactoryDefaultMode == FactoryDefaultMode.None)
        {
            await CommitAsync(ctx, fwmgr, actual, info.Version, ct).ConfigureAwait(false);
        }
        else
        {
            const string Reason = "The device commits a factory default upgrade by itself.";
            ctx.SkipStep(Steps.ReadCommitState, Reason);
            ctx.SkipStep(Steps.Commit, Reason);
            ctx.ReportWarning(payload.FactoryDefaultMode == FactoryDefaultMode.Hard
                ? "Hard factory default applied: all settings including the IP configuration and the users were reset. Set a password and check the address."
                : "Soft factory default applied: settings and users were reset (network settings kept). Set a password for the device.");
        }

        ctx.Log(TaskLogLevel.Info, Invariant($"Firmware {actual} installed (previous {info.Version} kept as rollback image)."));
    }

    /// <summary>Marks every planned step after the current one as Skipped with <paramref name="reason"/>.</summary>
    private static void SkipRemaining(ITaskExecutionContext ctx, string reason)
    {
        foreach (var name in Steps.Planned.SkipWhile(n => n != Steps.ValidateFile).Skip(1))
        {
            ctx.SkipStep(name, reason);
        }
    }

    /// <summary>Read-only: "status" returns <see cref="FirmwareStatusInfo"/> JSON for one device.</summary>
    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(method, StatusQuery, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unknown query '{method}'.");
        }

        var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
        var api = apis.FindApi(FwmgrClient.ApiId, 1);
        var info = await ctx.Vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
        if (api is null || !apis.Supports(FwmgrClient.ApiId, FwmgrClient.MinApiVersion))
        {
            return new FirmwareStatusInfo { Supported = false, ActiveVersion = info.Version, Model = info.ProdNbr, Architecture = info.Architecture }.ToJson();
        }

        var status = await new FwmgrClient(ctx.Vapix).GetStatusAsync(ct).ConfigureAwait(false);
        return ToInfo(status, api.Version, info).ToJson();
    }

    public static FirmwareStatusInfo ToInfo(FwmgrStatus status, string? fwmgrVersion, BasicDeviceInfo? info)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new FirmwareStatusInfo
        {
            Supported = true,
            FwmgrVersion = fwmgrVersion,
            ActiveVersion = status.ActiveFirmwareVersion ?? info?.Version,
            ActivePart = status.ActiveFirmwarePart,
            InactiveVersion = status.InactiveFirmwareVersion,
            IsCommitted = status.IsCommitted,
            PendingCommit = status.PendingCommit,
            TimeToRollback = status.TimeToRollback,
            LastUpgradeAt = status.LastUpgradeAt,
            ResetSource = status.ResetSource,
            Model = info?.ProdNbr,
            Architecture = info?.Architecture,
        };
    }

    private async Task<string?> UploadAsync(ITaskExecutionContext ctx, FwmgrClient fwmgr, UploadedFile file, FirmwareImageInfo image, FactoryDefaultMode mode, string oldVersion, CancellationToken ct)
    {
        // Settings kept: OADM commits after verifying; if OADM never does, the device rolls back by itself.
        // Factory default: the device commits when it has started, because OADM's credentials are gone.
        var options = mode == FactoryDefaultMode.None
            ? new FwmgrUpgradeOptions(mode, "never", _timings.AutoRollbackMinutes.ToString(CultureInfo.InvariantCulture))
            : new FwmgrUpgradeOptions(mode, "started", "never");

        using var step = ctx.BeginStep(Steps.Upload);
        var totalMb = file.Size / (1024 * 1024);
        var lastPercent = -1;
        using var content = new FirmwareStreamContent(
            token => ctx.Files.OpenReadAsync(file.Id, token),
            file.Size,
            sent =>
            {
                var percent = (int)(100 * (double)sent / Math.Max(1, file.Size));
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    step.ReportProgress(percent, Invariant($"{sent / (1024 * 1024)} of {totalMb} MB"));
                }
            });

        ctx.Log(TaskLogLevel.Info, Invariant($"Uploading {image.FileName}: factoryDefaultMode={options.FactoryDefaultMode.ToString().ToLowerInvariant()}, autoCommit={options.AutoCommit}, autoRollback={options.AutoRollback}."));
        try
        {
            var version = await fwmgr.UpgradeAsync(content, image.FileName, options, _timings.UploadTimeout, ct).ConfigureAwait(false);
            ctx.Log(TaskLogLevel.Info, $"Device accepted the image (version {version ?? "not reported"}).");
            step.Complete(Invariant($"{totalMb} MB, device accepted AXIS OS {version ?? "(version not reported)"}"));
            return version;
        }
        catch (FwmgrException ex) when (ex.NothingInstalled)
        {
            throw new InvalidOperationException($"{ex.Message} The device still runs {oldVersion}.", ex);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && content.BytesSent == file.Size
            && ex is HttpRequestException or IOException or TaskCanceledException)
        {
            // The whole image went out but the answer was lost (the device may already reboot). Verify by version.
            ctx.Log(TaskLogLevel.Warning, "The upload completed but the device's answer was lost: " + ex.Message + " Checking the version after the restart.");
            step.Complete("Image sent; the device's answer was lost, the version is checked after the restart");
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException)
        {
            throw new InvalidOperationException(
                Invariant($"The upload was interrupted after {content.BytesSent / (1024 * 1024)} of {totalMb} MB ({ex.Message}). The device still runs {oldVersion}."), ex);
        }
    }

    /// <summary>
    /// Two steps sharing one timeout: "Install firmware" waits until the device goes offline to restart
    /// (or already answers with another version), "Wait for device to come back" until it answers again.
    /// </summary>
    private async Task<string?> WaitForRestartAsync(ITaskExecutionContext ctx, string oldVersion, FactoryDefaultMode mode, CancellationToken ct)
    {
        var start = _time.GetTimestamp();
        var minutes = _timings.RestartTimeout.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture);
        string? changedVersion = null;
        using (var step = ctx.BeginStep(Steps.Install))
        {
            while (true)
            {
                await Task.Delay(_timings.PollInterval, _time, ct).ConfigureAwait(false);
                var elapsed = _time.GetElapsedTime(start);
                var version = await TryReadVersionAsync(ctx, ct).ConfigureAwait(false);
                if (version is null)
                {
                    step.Complete("The device restarts");
                    break;
                }

                if (!string.Equals(version, oldVersion, StringComparison.Ordinal))
                {
                    changedVersion = version;
                    step.Complete("The device restarted");
                    break;
                }

                if (elapsed >= _timings.RestartTimeout)
                {
                    throw new TimeoutException($"The device did not restart within {minutes} minutes after the upload and still runs {oldVersion}.");
                }

                step.ReportProgress(Percent(elapsed), "The device installs the firmware");
            }
        }

        using (var step = ctx.BeginStep(Steps.WaitForDevice))
        {
            if (changedVersion is not null)
            {
                step.Complete($"Answers with AXIS OS {changedVersion}");
                return changedVersion;
            }

            while (true)
            {
                await Task.Delay(_timings.PollInterval, _time, ct).ConfigureAwait(false);
                var elapsed = _time.GetElapsedTime(start);
                var version = await TryReadVersionAsync(ctx, ct).ConfigureAwait(false);
                if (version is not null)
                {
                    step.Complete($"Answers with AXIS OS {version}");
                    return version;
                }

                if (elapsed >= _timings.RestartTimeout)
                {
                    var hint = mode switch
                    {
                        FactoryDefaultMode.Hard => " A hard factory default resets the IP configuration, so the device may be at another address.",
                        FactoryDefaultMode.None => Invariant($" If the new firmware starts but is not committed, the device rolls back to {oldVersion} by itself {_timings.AutoRollbackMinutes} minutes after booting."),
                        _ => string.Empty,
                    };
                    throw new TimeoutException($"The device did not come back within {minutes} minutes after the firmware upload.{hint}");
                }

                step.ReportProgress(Percent(elapsed), "Not answering yet");
            }
        }
    }

    private int Percent(TimeSpan elapsed) => (int)(100 * Math.Min(1.0, elapsed / _timings.RestartTimeout));

    /// <summary>Anonymous basicdeviceinfo (works after a factory default too); null while the device does not answer.</summary>
    private async Task<string?> TryReadVersionAsync(ITaskExecutionContext ctx, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(_timings.ProbeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "axis-cgi/basicdeviceinfo.cgi")
            {
                Content = new StringContent("{\"apiVersion\":\"1.0\",\"context\":\"oadm\",\"method\":\"getAllUnrestrictedProperties\"}", Encoding.UTF8, "application/json"),
            };
            using var response = await ctx.Vapix.SendAsync(request, attempt.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("propertyList", out var props)
                && props.TryGetProperty("Version", out var v)
                && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
        }
#pragma warning disable CA1031 // Any error or timeout while the device installs and reboots means "not answering yet".
        catch (Exception)
#pragma warning restore CA1031
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>
    /// Per attempt one "Read commit state" and one "Commit firmware" step (retries are suffixed
    /// "(attempt n)"), with a "Wait before retrying the commit" step in between.
    /// </summary>
    private async Task CommitAsync(ITaskExecutionContext ctx, FwmgrClient fwmgr, AxisOsVersion actual, string oldVersion, CancellationToken ct)
    {
        Exception? last = null;
        var commitBegun = false;
        for (var attempt = 1; attempt <= _timings.CommitAttempts; attempt++)
        {
            var suffix = attempt == 1 ? string.Empty : Invariant($" (attempt {attempt})");
            try
            {
                var status = await ctx.StepAsync(Steps.ReadCommitState + suffix, async step =>
                {
                    var s = await fwmgr.GetStatusAsync(ct).ConfigureAwait(false);
                    step.Complete(s.IsCommitted == true && s.PendingCommit is null && s.TimeToRollback is null ? "Already committed" : "Not committed yet");
                    return s;
                }).ConfigureAwait(false);

                var commitName = commitBegun ? Steps.Commit + suffix : Steps.Commit;
                if (status.IsCommitted == true && status.PendingCommit is null && status.TimeToRollback is null)
                {
                    ctx.SkipStep(commitName, "Already committed by the device.");
                    ctx.Log(TaskLogLevel.Info, "Firmware already committed by the device.");
                    return;
                }

                commitBegun = true;
                var committed = await ctx.StepAsync(commitName, async step =>
                {
                    var version = await fwmgr.CommitAsync(ct).ConfigureAwait(false);
                    step.Complete($"AXIS OS {version ?? actual.Text}");
                    return version;
                }).ConfigureAwait(false);
                ctx.Log(TaskLogLevel.Info, $"Committed firmware {committed ?? actual.Text}.");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
                ctx.Log(TaskLogLevel.Warning, Invariant($"Commit attempt {attempt} failed: {ex.Message}"));
                if (attempt < _timings.CommitAttempts)
                {
                    using var wait = ctx.BeginStep(Steps.WaitBeforeRetry);
                    await Task.Delay(_timings.CommitRetryDelay, _time, ct).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException(
            Invariant($"Firmware {actual} is running but could not be committed ({last?.Message}). The device rolls back to {oldVersion} by itself {_timings.AutoRollbackMinutes} minutes after booting unless the firmware is committed."),
            last);
    }

    private static async Task<byte[]> ReadHeaderAsync(IUploadedFiles files, string fileId, CancellationToken ct)
    {
        var stream = await files.OpenReadAsync(fileId, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[FirmwareImageInspector.HeaderLength];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            return buffer[..read];
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
