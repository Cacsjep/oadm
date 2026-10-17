using Microsoft.Extensions.Hosting;

using Oadm.Core.Plugins;

namespace Oadm.Server.Plugins;

/// <summary>Checks the plugin licenses again every hour, so an expired license or trial reaches the plugins the same day.</summary>
public sealed class PluginLicenseRefreshHostedService(PluginLicenses licenses, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                licenses.Refresh();
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}
