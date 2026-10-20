using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Oadm.SiteNode.State;

/// <summary>
/// Counts the service's run time against the binding window: a tick every minute (saved to node.json) and one at stop.
/// A tick that cannot be saved (disk full, a locked file) is logged and the clock goes on: it never stops the service.
/// </summary>
public sealed partial class BindingClock(BindingState binding, NodeOptions options, ILogger<BindingClock>? logger = null) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly ILogger _logger = logger ?? NullLogger<BindingClock>.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, options.Time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                TryTick();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        TryTick();
        return base.StopAsync(cancellationToken);
    }

    private void TryTick()
    {
        try
        {
            binding.Tick();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogTickFailed(_logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The binding time left cannot be saved; trying again in a minute")]
    private static partial void LogTickFailed(ILogger logger, Exception error);
}
