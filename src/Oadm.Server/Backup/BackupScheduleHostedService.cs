using System.Globalization;

namespace Oadm.Server.Backup;

/// <summary>
/// Makes the automatic backup once a day at the chosen time (server time), when it is turned on. Checked every 30 seconds;
/// a day whose time passed while the server was off is made up at the next check.
/// </summary>
public sealed class BackupScheduleHostedService(BackupManager backups, TimeProvider time) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    /// <summary>True when the backup of the slot at or before <paramref name="now"/> is due (no backup since that slot).</summary>
    public static bool IsDue(BackupScheduleSetting schedule, DateTimeOffset now, DateTimeOffset? last)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!schedule.Enabled || !TimeOnly.TryParseExact(schedule.TimeOfDay, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            return false;
        }

        var today = new DateTimeOffset(now.Date + at.ToTimeSpan(), now.Offset);
        var slot = now >= today ? today : today.AddDays(-1);
        return last is null ? now >= today : last < slot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, time);
        do
        {
            try
            {
                var schedule = await backups.GetScheduleAsync(stoppingToken).ConfigureAwait(false);
                var last = (await backups.GetLastAutomaticAsync(stoppingToken).ConfigureAwait(false))?.TimeUtc;
                if (IsDue(schedule, time.GetLocalNow(), last?.ToOffset(time.GetLocalNow().Offset)))
                {
                    await backups.RunAutomaticAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed check is retried at the next one; RunAutomaticAsync records its own errors.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
