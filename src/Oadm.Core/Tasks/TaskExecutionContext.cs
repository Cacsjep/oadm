using Microsoft.Extensions.Logging;

using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tasks;

/// <summary>Per-device execution context handed to <see cref="ITaskPlugin.ExecuteAsync"/>.</summary>
internal sealed class TaskExecutionContext(
    Guid taskId,
    IVapixClient vapix,
    ILogger logger,
    ICorePlugin? owner,
    Action<int, string?> reportProgress) : ITaskExecutionContext
{
    public Guid TaskId { get; } = taskId;

    public IVapixClient Vapix { get; } = vapix;

    public ILogger Logger { get; } = logger;

    public ICorePlugin? Owner { get; } = owner;

    public void ReportProgress(int percent, string? message = null) => reportProgress(percent, message);

    // Placeholders until the task log, warnings and uploads are implemented.
#pragma warning disable CA1848, CA1873 // temporary placeholder logging
    public IUploadedFiles Files { get; } = NoUploadedFiles.Instance;

    public void ReportWarning(string message) => Logger.LogWarning("Task {TaskId}: {Message}", TaskId, message);

    public void Log(TaskLogLevel level, string message) =>
        Logger.Log(level switch { TaskLogLevel.Error => LogLevel.Error, TaskLogLevel.Warning => LogLevel.Warning, _ => LogLevel.Information }, "Task {TaskId}: {Message}", TaskId, message);

#pragma warning restore CA1848, CA1873

    private sealed class NoUploadedFiles : IUploadedFiles
    {
        public static readonly NoUploadedFiles Instance = new();

        public Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct) => Task.FromResult<UploadedFile?>(null);

        public Task<Stream> OpenReadAsync(string fileId, CancellationToken ct) =>
            throw new FileNotFoundException("Uploaded files are not available yet.", fileId);
    }
}
