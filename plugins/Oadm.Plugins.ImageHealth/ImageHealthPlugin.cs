using Oadm.Plugins.ImageHealth.Monitoring;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ImageHealth;

/// <summary>
/// Core plugin "Image Health Dashboard": the status of AXIS Image Health Analytics (blur, block, redirect,
/// under-exposure, unsuitability) on the chosen cameras that have the app, in one table. Off by default (plugin.json). A
/// live display only: the server asks the page's cameras when the page asks (<see cref="ImageHealthMonitor"/>): when it opens, on
/// Refresh and with Auto refresh every 10 s. Event rules use the app's own events through Device triggers.
/// Read-only for devices.
/// </summary>
public sealed class ImageHealthPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly ImageHealthOptions _options;
    private const string NotRunning = "The Image Health Dashboard plugin is not running.";
    private ImageHealthMonitor? _monitor;
    private ICorePluginContext? _ctx;

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

    /// <summary>The monitor while the plugin runs (tests).</summary>
    internal ImageHealthMonitor? Monitor => _monitor;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        _ctx = ctx;
        _monitor = new ImageHealthMonitor(ctx, _options);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_monitor is { } monitor)
        {
            _monitor = null;
            await monitor.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>In-process calls without a user (the host itself, tests): the table of all devices.</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        InvokeAsync(new ContextCall(_ctx ?? throw new InvalidOperationException(NotRunning)), method, payloadJson, ct);

    /// <summary>
    /// The page's calls: the table of the caller's devices (one per device group). A check's chosen cameras must all be in
    /// the caller's view (else NOT_FOUND, nothing read); without a choice it covers the caller's video devices.
    /// </summary>
    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var monitor = _monitor ?? throw new InvalidOperationException(NotRunning);
        switch (method)
        {
            case ImageHealthMethods.Check:
                var ids = CheckIds(payloadJson);
                if (ids is not null)
                {
                    await CallDevices.RequireAsync(caller, ids, ct).ConfigureAwait(false);
                }

                return ImageHealthJson.Serialize(monitor.StartCheck(caller.DeviceScopeKey, ids));
            case ImageHealthMethods.GetState:
                return ImageHealthJson.Serialize(monitor.Snapshot(caller.DeviceScopeKey, withRows: true));
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    /// <summary>The cameras a check covers: the page's choice (1 to 30,000), or every video device of the caller for an older page without one.</summary>
    private static HashSet<Guid>? CheckIds(string? payloadJson)
    {
        var ids = string.IsNullOrWhiteSpace(payloadJson) ? null : ImageHealthJson.Deserialize<CheckRequest>(payloadJson).DeviceIds;
        if (ids is null)
        {
            return null;
        }

        return ids.Count is 0 or > DeviceLimits.MaxDevices ? throw new ArgumentException(DeviceLimits.ChooseRangeText("cameras"), nameof(payloadJson)) : ids.ToHashSet();
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
