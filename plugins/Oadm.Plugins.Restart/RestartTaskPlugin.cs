using System.Globalization;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Restart;

/// <summary>
/// Restarts a device via restart.cgi, then polls basicdeviceinfo until the device has gone down
/// and answers again. Fails with <see cref="TimeoutException"/> when it is not back in time.
/// Steps: Check device, Send restart, Wait for the device to go offline, Wait for the device to come
/// back, Verify device.
/// </summary>
public sealed class RestartTaskPlugin : ITaskPlugin
{
    public const string PluginId = "oadm.restart";

    public const string StepCheck = "Check device";
    public const string StepRestart = "Send restart";
    public const string StepWaitOffline = "Wait for the device to go offline";
    public const string StepWaitOnline = "Wait for the device to come back";
    public const string StepVerify = "Verify device";

    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _time;

    /// <summary>Production defaults: poll every 5 s, give up after 3 min.</summary>
    public RestartTaskPlugin()
        : this(DefaultPollInterval, DefaultTimeout, DefaultRequestTimeout, TimeProvider.System)
    {
    }

    public RestartTaskPlugin(TimeSpan pollInterval, TimeSpan timeout, TimeSpan requestTimeout, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _pollInterval = pollInterval;
        _timeout = timeout;
        _requestTimeout = requestTimeout;
        _time = timeProvider;
    }

    public string Id => PluginId;

    public string DisplayName => "Restart";

    /// <summary>The task name in the tasks pane.</summary>
    public string GetTaskName(string? payloadJson) => "Restart device";

    public string Group => TaskGroups.Maintenance;

    public string? IconKey => "restart";

    public bool ShowInToolbar => true;

    public bool RequiresDialog => false;

    /// <summary>Needs working credentials; Unknown is allowed because status may not be polled yet.</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status is DeviceStatus.Ok or DeviceStatus.Unknown;
    }

    /// <summary>Plain-language reason for the greyed menu entry when <see cref="CanRun"/> is false (cached data only).</summary>
    public string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device.Status);
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);

        ctx.PlanSteps(StepCheck, StepRestart, StepWaitOffline, StepWaitOnline, StepVerify);

        using (var step = ctx.BeginStep(StepCheck))
        {
            var info = await TryReadAsync(ctx, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The device does not answer. Nothing was changed.");
            step.Complete(Describe(info));
        }

        using (var step = ctx.BeginStep(StepRestart))
        {
            await ctx.Vapix.RestartAsync(ct).ConfigureAwait(false);
            step.Complete("Restart accepted");
        }

        // One deadline for both waits, as before: the whole restart must finish within the timeout.
        var start = _time.GetTimestamp();
        using (var step = ctx.BeginStep(StepWaitOffline))
        {
            await WaitAsync(ctx, step, start, wantAnswer: false, ct).ConfigureAwait(false);
            step.Complete("The device went offline");
        }

        BasicDeviceInfo back;
        using (var step = ctx.BeginStep(StepWaitOnline))
        {
            back = await WaitAsync(ctx, step, start, wantAnswer: true, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The device answered without device information.");
            step.Complete(string.Create(CultureInfo.InvariantCulture, $"Back after {_time.GetElapsedTime(start).TotalSeconds:0} s"));
        }

        using (var step = ctx.BeginStep(StepVerify))
        {
            if (!string.IsNullOrEmpty(device.Serial) && !string.Equals(back.SerialNumber, device.Serial, StringComparison.OrdinalIgnoreCase))
            {
                step.Warn($"The device now reports serial number {back.SerialNumber}, expected {device.Serial}.");
            }
            else
            {
                step.Complete(Describe(back));
            }
        }
    }

    private static string Describe(BasicDeviceInfo info) =>
        string.IsNullOrEmpty(info.Version) ? info.ProdNbr : $"{info.ProdNbr}, AXIS OS {info.Version}";

    /// <summary>Polls until the device stops answering (<paramref name="wantAnswer"/> false) or answers again.</summary>
    private async Task<BasicDeviceInfo?> WaitAsync(ITaskExecutionContext ctx, ITaskStep step, long start, bool wantAnswer, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(_pollInterval, _time, ct).ConfigureAwait(false);
            var elapsed = _time.GetElapsedTime(start);
            if (elapsed >= _timeout)
            {
                throw new TimeoutException(wantAnswer
                    ? $"Device did not come back within {_timeout.TotalMinutes:0.#} minutes after restart."
                    : $"Device did not restart within {_timeout.TotalMinutes:0.#} minutes.");
            }

            var info = await TryReadAsync(ctx, ct).ConfigureAwait(false);
            if ((info is not null) == wantAnswer)
            {
                return info;
            }

            step.ReportProgress(
                (int)(100 * Math.Min(1.0, elapsed / _timeout)),
                string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0} s of at most {_timeout.TotalSeconds:0} s"));
        }
    }

    private async Task<BasicDeviceInfo?> TryReadAsync(ITaskExecutionContext ctx, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(_requestTimeout);
        try
        {
            return await ctx.Vapix.GetBasicDeviceInfoAsync(attempt.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any error or request timeout while rebooting means "not answering yet".
        catch (Exception)
#pragma warning restore CA1031
        {
            // A failure that raced with task cancellation must surface as cancellation.
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }
}
