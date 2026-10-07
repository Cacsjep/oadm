using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Restart;

/// <summary>
/// Restarts a device via restart.cgi, then polls basicdeviceinfo until the device has gone down
/// and answers again. Fails with <see cref="TimeoutException"/> when it is not back in time.
/// </summary>
public sealed class RestartTaskPlugin : ITaskPlugin
{
    public const string PluginId = "oadm.restart";

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

    public string? IconKey => "restart";

    public bool ShowInToolbar => true;

    public bool RequiresDialog => false;

    /// <summary>Needs working credentials; Unknown is allowed because status may not be polled yet.</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status is DeviceStatus.Ok or DeviceStatus.Unknown;
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);

        ctx.ReportProgress(0, "Sending restart command");
        await ctx.Vapix.RestartAsync(ct).ConfigureAwait(false);
        ctx.ReportProgress(10, "Waiting for the device to go down");

        var start = _time.GetTimestamp();
        var wentDown = false;
        while (true)
        {
            await Task.Delay(_pollInterval, _time, ct).ConfigureAwait(false);
            var elapsed = _time.GetElapsedTime(start);
            if (elapsed >= _timeout)
            {
                throw new TimeoutException(wentDown
                    ? $"Device did not come back within {_timeout.TotalMinutes:0.#} minutes after restart."
                    : $"Device did not restart within {_timeout.TotalMinutes:0.#} minutes.");
            }

            var answered = await TryPingAsync(ctx, ct).ConfigureAwait(false);
            var progress = 10 + (int)(80 * Math.Min(1.0, elapsed / _timeout));
            if (!answered)
            {
                wentDown = true;
                ctx.ReportProgress(progress, "Waiting for the device to come back");
            }
            else if (wentDown)
            {
                ctx.ReportProgress(100, "Device is back online");
                return;
            }
            else
            {
                ctx.ReportProgress(progress, "Waiting for the device to go down");
            }
        }
    }

    private async Task<bool> TryPingAsync(ITaskExecutionContext ctx, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(_requestTimeout);
        try
        {
            await ctx.Vapix.GetBasicDeviceInfoAsync(attempt.Token).ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // Any error or request timeout while rebooting means "not answering yet".
        catch (Exception)
#pragma warning restore CA1031
        {
            // A failure that raced with task cancellation must surface as cancellation.
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }
}
