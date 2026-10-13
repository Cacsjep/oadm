using Oadm.Server.Hosting;

namespace Oadm.Server.Backup;

/// <summary>
/// Restarts the server after a staged restore, so the next start applies it before the database is opened. As the installed
/// service the process ends with exit code <see cref="ExitCode"/> a moment after the reply went out: the Windows service
/// manager (restart on failure), systemd (<c>Restart=on-failure</c>) and launchd (<c>KeepAlive</c>, <c>SuccessfulExit</c>
/// false) start it again. On a console the host stops and <c>Program</c> builds a new one in the same process
/// (<see cref="Pending"/>).
/// </summary>
public sealed partial class ServerRestart(IHostApplicationLifetime lifetime, ServerRunMode mode, ILogger<ServerRestart> logger) : IServerRestart
{
    /// <summary>The process exit code of a restart (non-zero: the service managers restart on failure).</summary>
    public const int ExitCode = 75;

    /// <summary>Time for the gRPC reply to reach the client before the server goes away.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    private static int _pending;

    /// <summary>Set when a console server should build a new host after the current one stopped (read by Program).</summary>
    public static bool Pending => Volatile.Read(ref _pending) == 1;

    /// <summary>Program: the new host is being built.</summary>
    public static void Clear() => Interlocked.Exchange(ref _pending, 0);

    public void Request()
    {
        LogRestart(mode.IsService ? "the service manager starts it again" : "a new server starts in this process");
        _ = Task.Run(async () =>
        {
            await Task.Delay(Delay).ConfigureAwait(false);
            if (mode.IsService)
            {
                // Ends without reporting a stop: every service manager counts it as a failure and starts the server again
                // (a clean stop would be a successful one for the Windows service manager). What the running parts had
                // not written yet (a few seconds of task progress, event log) belongs to the database the restore replaces.
                Environment.ExitCode = ExitCode;
                Environment.Exit(ExitCode);
            }

            Interlocked.Exchange(ref _pending, 1);
            lifetime.StopApplication();
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server restarts to apply a restore: {How}")]
    private partial void LogRestart(string how);
}
