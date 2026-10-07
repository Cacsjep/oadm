using Microsoft.Extensions.Logging;

using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tasks;

/// <summary>
/// Per-device execution context handed to <see cref="ITaskPlugin.ExecuteAsync"/>. Progress, steps,
/// warnings, log entries and credential changes go to the engine (<see cref="ITaskExecutionSink"/>).
/// </summary>
internal sealed partial class TaskExecutionContext : ITaskExecutionContext
{
    private readonly Guid _deviceId;
    private readonly ITaskExecutionSink _sink;
    private IVapixClient _vapix;

    public TaskExecutionContext(
        Guid taskId,
        Guid deviceId,
        IVapixClient vapix,
        ILogger logger,
        ICorePlugin? owner,
        IUploadedFiles files,
        ITaskExecutionSink sink,
        TimeProvider? timeProvider = null)
    {
        TaskId = taskId;
        _deviceId = deviceId;
        _vapix = vapix;
        Logger = logger;
        Owner = owner;
        Files = files;
        _sink = sink;
        Steps = new TaskStepList(timeProvider, ReportWarning);
        Steps.Changed += (_, _) => _sink.StepsChanged(_deviceId, Steps);
    }

    public Guid TaskId { get; }

    /// <summary>Replaced by <see cref="UpdateCredentialsAsync"/> with a client using the new credentials.</summary>
    public IVapixClient Vapix => Volatile.Read(ref _vapix);

    public ILogger Logger { get; }

    public ICorePlugin? Owner { get; }

    public IUploadedFiles Files { get; }

    /// <summary>The task's steps. The engine snapshots them and closes them when the plugin returns or throws.</summary>
    public TaskStepList Steps { get; }

    public void ReportProgress(int percent, string? message = null) => _sink.ReportProgress(_deviceId, percent, message);

    public void PlanSteps(params string[] names) => Steps.Plan(names);

    public ITaskStep BeginStep(string name) => Steps.Begin(name);

    public void ReportWarning(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        LogPluginWarning(Logger, TaskId, _deviceId, message);
        _sink.ReportWarning(_deviceId, message);
    }

    public void Log(TaskLogLevel level, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        LogPluginEntry(Logger, TaskId, _deviceId, level, message);
        _sink.Log(_deviceId, level, message);
    }

    public void MarkCredentialsInvalid() => _sink.MarkCredentialsInvalid(_deviceId);

    public async Task UpdateCredentialsAsync(string userName, string password, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrEmpty(password);
        var client = await _sink.UpdateCredentialsAsync(_deviceId, userName.Trim(), password, ct).ConfigureAwait(false);
        Volatile.Write(ref _vapix, client);
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

    /// <summary>A step was planned, started, progressed or ended.</summary>
    void StepsChanged(Guid deviceId, TaskStepList steps);

    void MarkCredentialsInvalid(Guid deviceId);

    /// <summary>Stores the credentials and returns a VAPIX client that uses them.</summary>
    Task<IVapixClient> UpdateCredentialsAsync(Guid deviceId, string userName, string password, CancellationToken ct);
}
