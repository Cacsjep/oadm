using Oadm.Core.Events;
using Oadm.Core.Settings;

namespace Oadm.Server.Events;

/// <summary>
/// Event log retention: one minute after start, then hourly, deletes lines older than <c>Events.LogRetentionDays</c>
/// (default 30, 0 = no age limit) and beyond the newest <c>Events.LogMaxEntries</c> (default 100,000, 0 = no limit).
/// Between two runs the log's writer does the same each time it wrote as many lines as the log may hold.
/// </summary>
public sealed partial class EventLogRetentionHostedService(
    EventLog log,
    ServerSettingsStore settings,
    TimeProvider time,
    ILogger<EventLogRetentionHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromHours(1);

    public static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    /// <summary>Applies the retention settings once. Returns the number of deleted lines.</summary>
    public static async Task<int> PruneOnceAsync(EventLog log, ServerSettingsStore settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        var (maxAge, maxEntries) = await RetentionAsync(settings, ct).ConfigureAwait(false);
        return await log.PruneAsync(maxAge, maxEntries, ct).ConfigureAwait(false);
    }

    /// <summary>The retention settings now (null = no limit); the log's writer asks them between two hourly runs.</summary>
    public static async Task<(TimeSpan? MaxAge, int? MaxEntries)> RetentionAsync(ServerSettingsStore settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var days = await settings.GetAsync<int?>(SettingKeys.EventsLogRetentionDays, ct).ConfigureAwait(false) ?? ServerSettings.DefaultEventsLogRetentionDays;
        var max = await settings.GetAsync<int?>(SettingKeys.EventsLogMaxEntries, ct).ConfigureAwait(false) ?? ServerSettings.DefaultEventsLogMaxEntries;
        return (days > 0 ? TimeSpan.FromDays(days) : null, max > 0 ? max : null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A flood of lines between two runs: the log's writer prunes with the same settings (EventLog.Retention).
        log.Retention = ct => RetentionAsync(settings, ct);
        try
        {
            await Task.Delay(InitialDelay, time, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Period, time);
            do
            {
                try
                {
                    await PruneOnceAsync(log, settings, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event log retention failed")]
    private partial void LogFailed(Exception ex);
}
