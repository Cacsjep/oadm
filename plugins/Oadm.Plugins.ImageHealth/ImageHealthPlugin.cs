using Microsoft.Extensions.Logging;

using Oadm.Plugins.ImageHealth.Monitoring;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ImageHealth;

/// <summary>
/// Core plugin "Image Health Dashboard": the status of AXIS Image Health Analytics (blur, block, redirect,
/// under-exposure, unsuitability) on every camera that has the app, in one table. Off by default (plugin.json). The
/// server asks the cameras when the page asks (<see cref="ImageHealthMonitor"/>): when it opens, on Refresh and with
/// Auto refresh every 10 s; with "Monitor continuously" on (user decision 2026-10-10, off by default) also every 30 s in
/// the background, and then it publishes the event rule events (<see cref="ImageHealthEvents"/>). Read-only for devices.
/// </summary>
public sealed partial class ImageHealthPlugin : ICorePlugin, IEventSource, IAsyncDisposable
{
    public const string ConfigKey = "config";

    private readonly ImageHealthOptions _options;
    private ImageHealthMonitor? _monitor;
    private ICorePluginContext? _ctx;
    private CancellationTokenSource? _monitoring;
    private Task _monitorLoop = Task.CompletedTask;

    public ImageHealthPlugin()
        : this(new ImageHealthOptions())
    {
    }

    public ImageHealthPlugin(ImageHealthOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Id => ImageHealthPluginInfo.PluginId;

    public string DisplayName => ImageHealthPluginInfo.DisplayName;

    public string? IconKey => ImageHealthPluginInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Monitoring;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    public IReadOnlyList<EventTypeInfo> EventTypes => ImageHealthEvents.Types;

    /// <summary>Changing "Monitor continuously" is a server setting: Admin only (audited).</summary>
    public UserRole RequiredRole(string method) => method == ImageHealthMethods.SetMonitoring ? UserRole.Admin : UserRole.Operator;

    /// <summary>"Monitor continuously" is on.</summary>
    public bool IsMonitoring => _monitoring is not null;

    /// <summary>The monitor while the plugin runs (tests).</summary>
    internal ImageHealthMonitor? Monitor => _monitor;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        _ctx = ctx;
        var monitor = new ImageHealthMonitor(ctx, _options);
        monitor.CheckFinished += PublishEvents;
        _monitor = monitor;
        var config = await ReadConfigAsync(ctx, ct).ConfigureAwait(false);
        SetMonitoring(config.MonitorContinuously);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await StopMonitoringAsync().ConfigureAwait(false);
        if (_monitor is { } monitor)
        {
            _monitor = null;
            monitor.CheckFinished -= PublishEvents;
            await monitor.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        var monitor = _monitor ?? throw new InvalidOperationException("The Image Health Dashboard plugin is not running.");
        switch (method)
        {
            case ImageHealthMethods.Check:
                return ImageHealthJson.Serialize(monitor.StartCheck());
            case ImageHealthMethods.GetState:
                return ImageHealthJson.Serialize(monitor.Snapshot(withRows: true));
            case ImageHealthMethods.SetMonitoring:
                var request = ImageHealthJson.Deserialize<MonitoringRequest>(payloadJson ?? "{}");
                if (_ctx is { } ctx)
                {
                    await ctx.Settings.SetAsync(ConfigKey, ImageHealthJson.Serialize(new ImageHealthConfig(request.Enabled)), ct).ConfigureAwait(false);
                }

                if (!request.Enabled)
                {
                    await StopMonitoringAsync().ConfigureAwait(false);
                }

                SetMonitoring(request.Enabled);
                return ImageHealthJson.Serialize(monitor.Snapshot(withRows: true));
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    /// <summary>
    /// On: a background loop starts a check every <see cref="ImageHealthOptions.MonitorInterval"/> (the first at once);
    /// every finished check publishes the events. Off: nothing runs in the background and every event is off.
    /// </summary>
    private void SetMonitoring(bool enabled)
    {
        var monitor = _monitor;
        if (monitor is null)
        {
            return;
        }

        monitor.Monitoring = enabled;
        if (!enabled)
        {
            PublishAllOff();
            return;
        }

        if (_monitoring is not null)
        {
            return;
        }

        _monitoring = new CancellationTokenSource();
        var token = _monitoring.Token;
        _monitorLoop = Task.Run(() => MonitorLoopAsync(monitor, token), CancellationToken.None);
    }

    private async Task StopMonitoringAsync()
    {
        if (_monitoring is not { } cts)
        {
            return;
        }

        _monitoring = null;
        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _monitorLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        cts.Dispose();
    }

    private async Task MonitorLoopAsync(ImageHealthMonitor monitor, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.MonitorInterval, _options.Time);
        try
        {
            do
            {
                if (!monitor.IsChecking)
                {
                    monitor.StartCheck();
                }

                await monitor.CurrentCheck.WaitAsync(ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>After every check while monitoring: the full picture of every event type (the engine works out changes).</summary>
    private void PublishEvents(IReadOnlyList<ImageHealthRow> rows)
    {
        if (_ctx?.EventPublisher is not { } publisher || _monitoring is null)
        {
            return;
        }

        foreach (var (typeId, isOn) in ImageHealthEvents.Rules)
        {
            publisher.ReplaceStates(typeId, [.. rows.Where(isOn).Select(r => r.DeviceId)]);
        }
    }

    private void PublishAllOff()
    {
        if (_ctx?.EventPublisher is not { } publisher)
        {
            return;
        }

        foreach (var type in ImageHealthEvents.Types)
        {
            publisher.ReplaceStates(type.Id, []);
        }
    }

    private static async Task<ImageHealthConfig> ReadConfigAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        try
        {
            var json = await ctx.Settings.GetAsync(ConfigKey, ct).ConfigureAwait(false);
            return json is null ? new ImageHealthConfig(false) : ImageHealthJson.Deserialize<ImageHealthConfig>(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            LogConfigUnreadable(ctx.Logger, ex);
            return new ImageHealthConfig(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image Health Dashboard: the stored settings could not be read; monitoring stays off")]
    private static partial void LogConfigUnreadable(ILogger logger, Exception ex);

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
