using Oadm.Core.Events;
using Oadm.Core.Settings;

namespace Oadm.Server.Events;

/// <summary>
/// Event log retention: one minute after start, then hourly, deletes lines older than <c>Events.LogRetentionDays</c>
/// (default 30, 0 = no age limit) and beyond the newest <c>Events.LogMaxEntries</c> (default 100,000, 0 = no limit).
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
        ArgumentNullException.ThrowIfNull(settings);
        var days = await settings.GetAsync<int?>(SettingKeys.EventsLogRetentionDays, ct).ConfigureAwait(false) ?? ServerSettings.DefaultEventsLogRetentionDays;
        var max = await settings.GetAsync<int?>(SettingKeys.EventsLogMaxEntries, ct).ConfigureAwait(false) ?? ServerSettings.DefaultEventsLogMaxEntries;
        return await log.PruneAsync(days > 0 ? TimeSpan.FromDays(days) : null, max > 0 ? max : null, ct).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
