using Oadm.Core.Uploads;

namespace Oadm.Server.Files;

/// <summary>Deletes uploads older than <c>Uploads.RetentionHours</c>: once at start, then every <see cref="Period"/>.</summary>
public sealed partial class UploadCleanupHostedService(UploadStore store, TimeProvider time, ILogger<UploadCleanupHostedService> logger)
    : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, time);
        do
        {
            try
            {
                var removed = await store.DeleteExpiredAsync(stoppingToken).ConfigureAwait(false);
                if (removed > 0)
                {
                    LogRemoved(removed);
                }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} expired upload(s)")]
    private partial void LogRemoved(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cleaning up expired uploads failed")]
    private partial void LogFailed(Exception ex);
}
