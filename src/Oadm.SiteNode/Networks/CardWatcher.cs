using System.Net.NetworkInformation;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Oadm.SiteNode.Networks;

/// <summary>
/// Looks at the network cards every <see cref="Interval"/> and when the OS reports an address change: a new card, a
/// card gone or a new address (a DHCP renumber) raises <see cref="NetworkScope.CardsChanged"/>, so relays bind again and
/// the server's Watch and the app see the cards as they are.
/// </summary>
public sealed partial class CardWatcher(NetworkScope networks, NodeOptions options, ILogger<CardWatcher> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var signal = new SemaphoreSlim(0, 1);
        void OnAddressChanged(object? sender, EventArgs e)
        {
            try
            {
                signal.Release();
            }
            catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
            {
                // a check is already due, or the watcher stopped
            }
        }

        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        try
        {
            Check();
            while (!stoppingToken.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var changed = signal.WaitAsync(wait.Token);
                var tick = Task.Delay(Interval, options.Time, wait.Token);
                await Task.WhenAny(changed, tick).ConfigureAwait(false);
                await wait.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(changed, tick).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                if (!stoppingToken.IsCancellationRequested)
                    Check();
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        }
    }

    private void Check()
    {
        try
        {
            networks.CheckCards();
        }
        catch (Exception e) when (e is NetworkInformationException or IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            LogCheckFailed(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The network cards cannot be read; trying again shortly")]
    private static partial void LogCheckFailed(ILogger logger, Exception error);
}
