using Microsoft.Extensions.Hosting;

namespace Oadm.SiteNode.State;

/// <summary>Counts the service's run time against the binding window: a tick every minute (saved to node.json) and one at stop.</summary>
public sealed class BindingClock(BindingState binding, NodeOptions options) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, options.Time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                binding.Tick();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        binding.Tick();
        return base.StopAsync(cancellationToken);
    }
}
