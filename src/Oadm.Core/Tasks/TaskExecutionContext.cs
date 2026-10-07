using Microsoft.Extensions.Logging;

using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tasks;

/// <summary>
/// Per-device execution context handed to <see cref="ITaskPlugin.ExecuteAsync"/>. Progress, warnings
/// and log entries go to the engine (<see cref="ITaskExecutionSink"/>), which publishes and persists them.
/// </summary>
internal sealed partial class TaskExecutionContext(
    Guid taskId,
    Guid deviceId,
    IVapixClient vapix,
    ILogger logger,
    ICorePlugin? owner,
    IUploadedFiles files,
    ITaskExecutionSink sink) : ITaskExecutionContext
{
    public Guid TaskId { get; } = taskId;

    public IVapixClient Vapix { get; } = vapix;

    public ILogger Logger { get; } = logger;

    public ICorePlugin? Owner { get; } = owner;

    public IUploadedFiles Files { get; } = files;

    public void ReportProgress(int percent, string? message = null) => sink.ReportProgress(deviceId, percent, message);

    public void ReportWarning(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        LogPluginWarning(Logger, TaskId, deviceId, message);
        sink.ReportWarning(deviceId, message);
    }

    public void Log(TaskLogLevel level, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        LogPluginEntry(Logger, TaskId, deviceId, level, message);
        sink.Log(deviceId, level, message);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task {TaskId}, device {DeviceId}: warning: {Message}")]
    private static partial void LogPluginWarning(ILogger logger, Guid taskId, Guid deviceId, string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Task {TaskId}, device {DeviceId}: {Level}: {Message}")]
    private static partial void LogPluginEntry(ILogger logger, Guid taskId, Guid deviceId, TaskLogLevel level, string message);
}

/// <summary>Where a <see cref="TaskExecutionContext"/> reports to (the engine, for one task).</summary>
internal interface ITaskExecutionSink
{
    void ReportProgress(Guid deviceId, int percent, string? message);

    void ReportWarning(Guid deviceId, string message);

    void Log(Guid deviceId, TaskLogLevel level, string message);
}
