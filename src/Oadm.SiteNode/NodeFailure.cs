using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Oadm.SiteNode;

/// <summary>
/// Notices when the node stops because one of its parts failed (a background loop ended with an error) and turns that
/// into a non-zero exit code: <see cref="Failed"/> for Program and, as a Windows service, the service's own exit code,
/// which the SCM reports with the stop.
/// </summary>
public sealed partial class NodeFailure(IServiceProvider services, IHostApplicationLifetime lifetime, ILogger<NodeFailure> logger) : IHostedService
{
    private CancellationTokenRegistration _registration;
    private int _failed;

    /// <summary>True once the node stopped (or is stopping) because a part failed.</summary>
    public bool Failed => Volatile.Read(ref _failed) != 0;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registration = lifetime.ApplicationStopping.Register(Check);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Check();
        _registration.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Looks for a part that ended with an error; marks the failure once.</summary>
    public void Check()
    {
        var failed = services.GetServices<IHostedService>().OfType<BackgroundService>().FirstOrDefault(b => b.ExecuteTask is { IsFaulted: true });
        if (failed is null || Interlocked.Exchange(ref _failed, 1) != 0)
            return;
        var error = failed.ExecuteTask!.Exception?.GetBaseException();
        var part = failed.GetType().Name;
        LogFailed(logger, error, part);
        if (OperatingSystem.IsWindows() && services.GetService<IHostLifetime>() is System.ServiceProcess.ServiceBase service)
            service.ExitCode = 1;
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Part} failed; the site node stops so that the service manager starts it again")]
    private static partial void LogFailed(ILogger logger, Exception? error, string part);
}
