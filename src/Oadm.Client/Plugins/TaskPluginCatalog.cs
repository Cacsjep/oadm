using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;

namespace Oadm.Client.Plugins;

/// <summary>
/// Cached result of TaskService.ListTaskPlugins. The runnable device ids depend on device state, so the
/// catalog is refreshed on connect and (debounced) whenever the device table changes.
/// </summary>
public sealed partial class TaskPluginCatalog(IOadmApi api, IUiDispatcher ui, ILogger<TaskPluginCatalog> logger)
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _debounce;

    public IReadOnlyList<TaskPluginInfo> Plugins { get; private set; } = [];

    /// <summary>Raised on the UI thread after <see cref="Plugins"/> changed.</summary>
    public event EventHandler? Changed;

    public TimeSpan DebounceDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<TaskPluginInfo> plugins = await api.ListTaskPluginsAsync(ct).ConfigureAwait(false);
            ui.Post(() =>
            {
                Plugins = plugins;
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch (Exception ex)
        {
            LogRefreshFailed(logger, ex.Message);
        }
    }

    /// <summary>Coalesces bursts of device changes into one ListTaskPlugins call.</summary>
    public void RequestRefresh()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = cts = new CancellationTokenSource();
        }

        CancellationToken ct = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RefreshAsync(ct).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    /// <summary>Plugins whose CanRun is true for every one of the given devices.</summary>
    public static IEnumerable<TaskPluginInfo> RunnableFor(IEnumerable<TaskPluginInfo> plugins, IReadOnlyCollection<string> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (deviceIds.Count == 0)
        {
            return [];
        }

        return plugins.Where(p => deviceIds.All(id => p.RunnableDeviceIds.Contains(id)));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not load task plugins: {Reason}")]
    private static partial void LogRefreshFailed(ILogger logger, string reason);
}
