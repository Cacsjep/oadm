using Oadm.Core.Settings;
using Oadm.Core.Tasks;

namespace Oadm.Server.Tasks;

/// <summary>
/// Task history retention: once shortly after start, then every <see cref="Period"/>, deletes finished
/// tasks older than <c>Tasks.RetentionDays</c> (default 90, 0 = no age limit) and finished tasks beyond the
/// newest <c>Tasks.MaxHistory</c> (default 50,000, 0 = no limit). Active tasks are never deleted. Watchers
/// get one Removed per deleted task.
/// </summary>
public sealed partial class TaskRetentionHostedService(
    TaskEngine engine,
    ServerSettingsStore settings,
    TimeProvider time,
    ILogger<TaskRetentionHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromHours(1);

    /// <summary>Lets startup (plugins, recovery) finish before the first cleanup.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    /// <summary>Applies the retention settings once. Returns the number of deleted tasks.</summary>
    public static async Task<int> PruneOnceAsync(TaskEngine engine, ServerSettingsStore settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(settings);
        var days = await settings.GetAsync<int?>(SettingKeys.TasksRetentionDays, ct).ConfigureAwait(false) ?? ServerSettings.DefaultTasksRetentionDays;
        var max = await settings.GetAsync<int?>(SettingKeys.TasksMaxHistory, ct).ConfigureAwait(false) ?? ServerSettings.DefaultTasksMaxHistory;
        return await engine.PruneHistoryAsync(
            days > 0 ? TimeSpan.FromDays(days) : null,
            max > 0 ? max : null,
            ct).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, time, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Period, time);
        do
        {
            try
            {
                await PruneOnceAsync(engine, settings, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failing cleanup must not stop the server; it retries next period.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task history retention failed")]
    private partial void LogFailed(Exception ex);
}
