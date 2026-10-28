using System.Globalization;

using Oadm.Client.Api;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Client.Plugins;

/// <summary>
/// Server access handed to a task plugin dialog: queries go to <c>TaskService.Query</c> for the
/// dialog's plugin, uploads to <c>FileService.Upload</c>, a run it starts itself to <c>TaskService.Run</c> and is followed
/// in the client's task mirror (<see cref="TaskStore"/>, the tasks pane's). gRPC errors surface as RpcException whose
/// Status.Detail is the message for the user.
/// </summary>
public sealed class TaskDialogContext : ITaskDialogContext
{
    private readonly IOadmApi _api;
    private readonly Func<string, bool>? _has;
    private readonly Func<IReadOnlyList<IDeviceInfo>>? _allDevices;
    private readonly TaskStore? _tasks;
    private readonly Func<bool>? _hasAllDevices;

    /// <param name="has">The user's permissions (<see cref="Shell.UserSession.Has"/>); null: everything.</param>
    /// <param name="allDevices">Every device of the client's device list; null: none.</param>
    /// <param name="tasks">The client's task mirror: with it the dialog can run its task itself (<see cref="CanRunTasks"/>).</param>
    /// <param name="hasAllDevices">Whether the user may reach every device (<see cref="Shell.UserSession.HasAllDevices"/>); null: true.</param>
    /// <param name="startView">The part of the dialog a page asked to open on (<see cref="StartView"/>); null: its usual start.</param>
    public TaskDialogContext(IOadmApi api, string pluginId, Func<string, bool>? has = null, Func<IReadOnlyList<IDeviceInfo>>? allDevices = null, TaskStore? tasks = null,
        Func<bool>? hasAllDevices = null, string? startView = null)
    {
        StartView = string.IsNullOrWhiteSpace(startView) ? null : startView;
        _hasAllDevices = hasAllDevices;
        _tasks = tasks;
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        _api = api;
        _has = has;
        _allDevices = allDevices;
        PluginId = pluginId;
    }

    public bool Has(string permission) => _has?.Invoke(permission) ?? true;

    public bool HasAllDevices => _hasAllDevices?.Invoke() ?? true;

    public IReadOnlyList<IDeviceInfo> AllDevices => _allDevices?.Invoke() ?? [];

    public string PluginId { get; }

    public string? StartView { get; }

    public bool CanRunTasks => _tasks is not null;

    /// <summary>
    /// Runs the plugin on the devices and follows the tasks in the task mirror, on the UI thread (the mirror changes
    /// there). A task the mirror has not received yet counts as queued.
    /// </summary>
    public async Task<IReadOnlyList<TaskRunState>> RunTaskAsync(IReadOnlyList<Guid> deviceIds, string? payloadJson, IProgress<IReadOnlyList<TaskRunState>>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var tasks = _tasks ?? throw new NotSupportedException("This dialog cannot run tasks.");
        var taskIds = await _api.RunTaskAsync(PluginId, [.. deviceIds.Select(d => d.ToString())], payloadJson, TaskPluginRunner.OwnerName, ct).ConfigureAwait(true);
        var watched = taskIds.ToHashSet(StringComparer.Ordinal);
        var done = new TaskCompletionSource<IReadOnlyList<TaskRunState>>(TaskCreationOptions.RunContinuationsAsynchronously);

        IReadOnlyList<TaskRunState> States() => [.. taskIds.Select((id, i) => StateOf(tasks.Find(id), id, i < deviceIds.Count ? deviceIds[i] : Guid.Empty))];

        void Report()
        {
            var states = States();
            progress?.Report(states);
            if (states.All(s => !s.IsActive))
            {
                done.TrySetResult(states);
            }
        }

        // A change of one watched task (or a batch / reset: null) reports again; other tasks are ignored.
        void OnChanged(object? sender, TaskRowViewModel? row)
        {
            if (row is null || watched.Contains(row.Id))
            {
                Report();
            }
        }

        tasks.Changed += OnChanged;
        try
        {
            using var registration = ct.Register(() => done.TrySetCanceled(ct));
            Report();
            return await done.Task.ConfigureAwait(true);
        }
        finally
        {
            tasks.Changed -= OnChanged;
        }
    }

    /// <summary>A task row as the SDK reports it; no row yet: queued.</summary>
    internal static TaskRunState StateOf(TaskRowViewModel? row, string taskId, Guid deviceId)
    {
        if (row is null)
        {
            return new TaskRunState(deviceId, taskId, TaskRunStatus.Queued, string.Empty, string.Empty);
        }

        var status = row.State switch
        {
            TaskState.Running => TaskRunStatus.Running,
            TaskState.Done => TaskRunStatus.Done,
            TaskState.DoneWithWarnings => TaskRunStatus.DoneWithWarnings,
            TaskState.Failed => TaskRunStatus.Failed,
            TaskState.Cancelled => TaskRunStatus.Cancelled,
            _ => TaskRunStatus.Queued,
        };
        var device = Guid.TryParse(row.DeviceId, out var id) ? id : deviceId;
        return new TaskRunState(device, taskId, status, row.CurrentStepText, row.StatusMessage);
    }

    public Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return _api.QueryTaskPluginAsync(PluginId, deviceId.ToString(), method, payloadJson, ct);
    }

    public async Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var info = await _api.UploadFileAsync(localPath, progress, ct).ConfigureAwait(false);
        return new UploadedFile(info.Id, info.Name, info.Size, info.Sha256.ToUpper(CultureInfo.InvariantCulture));
    }
}
