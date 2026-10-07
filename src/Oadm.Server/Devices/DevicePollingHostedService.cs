using Oadm.Core.Devices;
using Oadm.Core.Tasks;
using Oadm.Server.Tasks;

namespace Oadm.Server.Devices;

/// <summary>
/// Runs <see cref="DevicePollingService"/> for the lifetime of the host and queues a full refresh
/// of every device of a task once that task finished.
/// </summary>
public sealed class DevicePollingHostedService(DevicePollingService polling, TaskEngine engine) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        engine.TaskChanged += OnTaskChanged;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        engine.TaskChanged -= OnTaskChanged;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => polling.RunAsync(stoppingToken);

    private void OnTaskChanged(object? sender, TaskChange change)
    {
        var task = change.Task;
        if (change.Kind == TaskChangeKind.Updated
            && task.State.IsTerminal())
        {
            polling.QueueRefresh(task.Devices.Select(d => d.DeviceId));
        }
    }
}
