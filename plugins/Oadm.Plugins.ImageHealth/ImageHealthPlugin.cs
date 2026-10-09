using Oadm.Plugins.ImageHealth.Monitoring;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ImageHealth;

/// <summary>
/// Core plugin "Image Health Dashboard": the status of AXIS Image Health Analytics (blur, block, redirect,
/// under-exposure, unsuitability) on every camera that has the app, in one table. Off by default (plugin.json). The
/// server asks the cameras only when the page asks (<see cref="ImageHealthMonitor"/>): when it opens, on Refresh and with
/// Auto refresh every 10 s. Read-only for devices.
/// </summary>
public sealed class ImageHealthPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly ImageHealthOptions _options;
    private ImageHealthMonitor? _monitor;

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

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        var monitor = _monitor ?? throw new InvalidOperationException("The Image Health Dashboard plugin is not running.");
        return method switch
        {
            ImageHealthMethods.Check => Task.FromResult<string?>(ImageHealthJson.Serialize(monitor.StartCheck())),
            ImageHealthMethods.GetState => Task.FromResult<string?>(ImageHealthJson.Serialize(monitor.Snapshot(withRows: true))),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
