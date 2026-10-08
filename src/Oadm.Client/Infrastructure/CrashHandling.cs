using System.Globalization;

using Avalonia.Threading;

using Microsoft.Extensions.Logging;

namespace Oadm.Client.Infrastructure;

/// <summary>
/// The client's last line of defence (production hardening 4): the three global handlers
/// (<see cref="AppDomain.UnhandledException"/>, <see cref="TaskScheduler.UnobservedTaskException"/>,
/// <see cref="Dispatcher.UnhandledException"/>) log every error together with the log file path. An error on the
/// UI thread is shown in the shared message window and the client keeps running; only fatal errors (out of
/// memory, corrupted state) end the app, after logging. Installed by Program for the real app only (tests see
/// their exceptions).
/// </summary>
public sealed partial class CrashHandling(ILogger logger, string logFilePath, Func<string, string, Task>? showMessage = null)
{
    private int _showing;

    /// <summary>The log file errors are written to (shown to the user).</summary>
    public string LogFilePath { get; } = logFilePath;

    /// <summary>Today's file of the rolling client log in <paramref name="logFolder"/> ("client-20261008.log").</summary>
    public static string TodaysLogFile(string logFolder) =>
        Path.Combine(logFolder, "client-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

    /// <summary>Errors that leave the process in an unknown state: logged, then the app ends.</summary>
    public static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or AccessViolationException or InvalidProgramException or AppDomainUnloadedException or BadImageFormatException;

    /// <summary>Subscribes the process-wide handlers (any thread).</summary>
    public void InstallProcessHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnUnhandled(e.ExceptionObject as Exception, e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            OnUnobserved(e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Subscribes the UI thread handler; call once Avalonia is set up.</summary>
    public void InstallUiHandler() => Dispatcher.UIThread.UnhandledException += (_, e) => e.Handled = OnUiException(e.Exception);

    /// <summary>An exception nobody caught on any thread: always logged; the runtime ends the process when terminating.</summary>
    public void OnUnhandled(Exception? ex, bool terminating)
    {
        if (terminating)
        {
            LogFatal(logger, ex, LogFilePath);
        }
        else
        {
            LogUnhandled(logger, ex, LogFilePath);
        }
    }

    /// <summary>A faulted task nobody awaited: logged, the client keeps running.</summary>
    public void OnUnobserved(Exception ex) => LogUnobserved(logger, ex, LogFilePath);

    /// <summary>
    /// An exception on the UI thread (a plugin page's handler, a binding, a command). Returns true when it was
    /// handled (logged and shown, the client keeps running), false for fatal errors (logged, the app ends).
    /// </summary>
    public bool OnUiException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (IsFatal(ex))
        {
            LogFatal(logger, ex, LogFilePath);
            return false;
        }

        LogUiError(logger, ex, LogFilePath);
        if (showMessage is not null && Interlocked.Exchange(ref _showing, 1) == 0)
        {
            _ = ShowAsync(ex);
        }

        return true;
    }

    private async Task ShowAsync(Exception ex)
    {
        try
        {
            await showMessage!("Something went wrong",
                $"{ex.Message}\n\nOADM keeps running. Details are in the client log:\n{LogFilePath}").ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The error window itself must never crash the client.
        catch (Exception showFailed)
#pragma warning restore CA1031
        {
            LogShowFailed(logger, showFailed);
        }
        finally
        {
            Interlocked.Exchange(ref _showing, 0);
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Fatal error, the client ends (log: {LogFile})")]
    private static partial void LogFatal(ILogger logger, Exception? ex, string logFile);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error on a background thread (log: {LogFile})")]
    private static partial void LogUnhandled(ILogger logger, Exception? ex, string logFile);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unobserved task error (log: {LogFile})")]
    private static partial void LogUnobserved(ILogger logger, Exception ex, string logFile);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error on the UI thread, the client keeps running (log: {LogFile})")]
    private static partial void LogUiError(ILogger logger, Exception ex, string logFile);

    [LoggerMessage(Level = LogLevel.Error, Message = "The error message could not be shown")]
    private static partial void LogShowFailed(ILogger logger, Exception ex);
}
