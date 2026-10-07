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
}
