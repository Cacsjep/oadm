using Oadm.Core.Auth;

namespace Oadm.Server.Auth;

/// <summary>Hourly (first run one minute after start): audit log retention and expired sessions.</summary>
public sealed partial class AuthMaintenanceHostedService(AuditLog audit, AuthTokenStore tokens, TimeProvider time, ILogger<AuthMaintenanceHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Server stopping.
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var entries = await audit.ApplyRetentionAsync(ct).ConfigureAwait(false);
            var sessions = await tokens.DeleteExpiredAsync(ct).ConfigureAwait(false);
            if (entries + sessions > 0)
            {
                LogCleaned(entries, sessions);
            }
        }
#pragma warning disable CA1031 // Maintenance must never stop the server.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Entries} old audit entries and {Sessions} expired sessions")]
    private partial void LogCleaned(int entries, int sessions);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit and session cleanup failed")]
    private partial void LogFailed(Exception ex);
}
