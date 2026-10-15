using Microsoft.Extensions.Logging;

using Oadm.Plugins.EdgeStorage.Device;
using Oadm.Plugins.EdgeStorage.Disks;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.EdgeStorage;

/// <summary>
/// Core plugin "Edge storage": the SD card of every camera in one table (state, size, used, wear, encryption, retention)
/// and the tools of the camera's web interface for many cameras at once: check, repair, format, encryption on or off,
/// retention. Off by default (plugin.json). The table is read only on request (<see cref="DeviceSweep{TRow}"/>: when the
/// page opens and on Refresh, read-only); the tools are tasks (<see cref="StorageTaskPlugin"/>) started only from the page.
/// Rights: "show" (Operator, Viewer), "manage" = check and retention (Operator yes), "format" = format, repair and
/// encryption (deletes recordings, full device control, Operator no).
/// </summary>
public sealed partial class EdgeStoragePlugin : ICorePlugin, IAsyncDisposable
{
    /// <summary>Devices per run (the page's whole table at most).</summary>
    public const int MaxDevicesPerRun = Oadm.Sdk.Devices.DeviceLimits.MaxDevices;

    private readonly DeviceSweepOptions _options;
    private readonly StorageTaskPlugin _task;
    private ICorePluginContext? _ctx;
    private DeviceSweep<StorageRow>? _sweep;

    public EdgeStoragePlugin()
        : this(new DeviceSweepOptions { DeviceTimeout = TimeSpan.FromSeconds(6) })
    {
    }

    internal EdgeStoragePlugin(DeviceSweepOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _task = new StorageTaskPlugin(this, options.Time);
        TaskPlugins = [_task];
    }

    public string Id => EdgeStorageInfo.PluginId;

    public string DisplayName => EdgeStorageInfo.DisplayName;

    public string? IconKey => EdgeStorageInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Maintenance;

    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }

    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Show, "Show the page", "See the SD cards of the cameras.") { ViewerDefault = true },
        new(EdgeStorageMethods.ManagePermission, "Change storage settings", "Check the SD cards and set how long recordings are kept."),
        new(EdgeStorageMethods.FormatPermission, "Format and encrypt storage", "Format and repair SD cards and turn encryption on or off. Deletes the recordings.")
        {
            OperatorDefault = false,
            FullDeviceControl = true,
        },
    ];

    public string? RequiredPermission(string method) => method switch
    {
        EdgeStorageMethods.Read or EdgeStorageMethods.GetState => PermissionKeys.Show,
        EdgeStorageMethods.Change => EdgeStorageMethods.ManagePermission,
        EdgeStorageMethods.Format => EdgeStorageMethods.FormatPermission,
        _ => null,
    };

    /// <summary>Every run is in the audit log (the method: change or format).</summary>
    public bool IsAudited(string method) => method is EdgeStorageMethods.Change or EdgeStorageMethods.Format;

    /// <summary>The table while the plugin runs (tests).</summary>
    internal DeviceSweep<StorageRow>? Sweep => _sweep;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        _ctx = ctx;
        // Every read replaces the row (the previous one is not needed).
        _sweep = new DeviceSweep<StorageRow>(ctx, _options, HasSlot, async (d, _, t) => await ReadAsync(d, t).ConfigureAwait(false), r => r.Address, PublishRows, PublishState);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_sweep is { } sweep)
        {
            _sweep = null;
            await sweep.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        var sweep = _sweep ?? throw new InvalidOperationException("The Edge storage plugin is not running.");
        switch (method)
        {
            case EdgeStorageMethods.Read:
                sweep.Start(ReadIds(payloadJson));
                return EdgeStorageJson.Serialize(State(withRows: true));
            case EdgeStorageMethods.GetState:
                return EdgeStorageJson.Serialize(State(withRows: true));
            case EdgeStorageMethods.Change or EdgeStorageMethods.Format:
                return EdgeStorageJson.Serialize(await RunAsync(method, EdgeStorageJson.Deserialize<RunRequest>(payloadJson), ct).ConfigureAwait(false));
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>Reads one camera's card again (after a task on it).</summary>
    internal async Task RefreshRowAsync(Guid deviceId)
    {
        if (_sweep is { } sweep)
        {
            try
            {
                await sweep.ReadOneAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The task's own result counts; the table reads it again on Refresh.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (_ctx is { } ctx)
                {
                    LogRefreshFailed(ctx.Logger, ex);
                }
            }
        }
    }

    /// <summary>Starts one task per device; the rights of the method cover the action (checked before any task starts).</summary>
    private async Task<IReadOnlyList<Guid>> RunAsync(string method, RunRequest request, CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("The Edge storage plugin is not running.");
        var payload = StorageTaskPlugin.Validate(new TaskPayload(request.Action, request.RetentionDays, request.Passphrase));
        if (StorageActions.IsChange(request.Action) != (method == EdgeStorageMethods.Change))
        {
            throw new ArgumentException("This action is not allowed through this method. Nothing was changed.", nameof(request));
        }

        if (request.DeviceIds is not { Count: > 0 and <= MaxDevicesPerRun })
        {
            throw new ArgumentException(Oadm.Sdk.Devices.DeviceLimits.ChooseRangeText("cameras"), nameof(request));
        }

        var owner = string.IsNullOrWhiteSpace(request.Owner) ? EdgeStorageInfo.DisplayName : request.Owner;
        LogRun(ctx.Logger, request.Action, request.DeviceIds.Count); // the task engine records the real caller as owner
        return await ctx.Tasks.RunAsync(EdgeStorageInfo.TaskPluginId, [.. request.DeviceIds.Distinct()], EdgeStorageJson.Serialize(payload), owner, ct).ConfigureAwait(false);
    }

    /// <summary>The cameras a read covers: the page's choice (1 to 30,000), or every camera for an older page without one.</summary>
    private static HashSet<Guid>? ReadIds(string? payloadJson)
    {
        var ids = string.IsNullOrWhiteSpace(payloadJson) ? null : EdgeStorageJson.Deserialize<ReadRequest>(payloadJson).DeviceIds;
        if (ids is null)
        {
            return null;
        }

        return ids.Count is 0 or > MaxDevicesPerRun ? throw new ArgumentException(Oadm.Sdk.Devices.DeviceLimits.ChooseRangeText("cameras"), nameof(payloadJson)) : ids.ToHashSet();
    }

    /// <summary>Devices that list the disk management API (an SD card slot); others are not in the table.</summary>
    private static bool HasSlot(IDeviceInfo device) => device.Apis.Supports(StorageRequests.DiskManagementApi, "1.0");

    /// <summary>One camera: its SD card with health and wear alert level, or why it could not be read.</summary>
    internal async Task<StorageRow> ReadAsync(IDeviceInfo device, CancellationToken ct)
    {
        var row = new StorageRow(device.Id, device.Address, device.Model, CardStates.Error, null, ReadUtc: _options.Time.GetUtcNow());
        if (DeviceMessages.Refusal(device.Status, refuseUnreachable: true) is { } refusal)
        {
            return row with { Text = refusal };
        }

        try
        {
            var vapix = await _ctx!.Vapix.CreateAsync(device.Id, ct).ConfigureAwait(false);
            var client = new StorageClient(vapix, _options.Time);
            var card = (await client.ListDisksAsync(ct).ConfigureAwait(false)).FirstOrDefault(d => d.IsSdCard);
            if (card is not { IsPresent: true })
            {
                return row with { State = CardStates.NoCard };
            }

            var wear = await Optional(() => client.GetHealthAsync(ct), ct).ConfigureAwait(false) is { } health
                ? health.FirstOrDefault(h => string.Equals(h.DiskId, card.Id, StringComparison.OrdinalIgnoreCase))?.WearPercent
                : null;
            var alert = wear is null ? null : await Optional(() => client.GetWearAlertAsync(card.Id, ct), ct).ConfigureAwait(false);
            return Classify(row, card, wear, alert ?? EdgeStorageInfo.DefaultWearAlert);
        }
#pragma warning disable CA1031 // One camera's failure must not stop the others; its row says why.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return row with { Text = ex is FormatException ? StorageTexts.Unreadable : _sweep?.Failure(ex) ?? ex.Message };
        }
    }

    /// <summary>The row of a present card: Problem when the camera cannot use it, Warning when worn past its alert level.</summary>
    internal static StorageRow Classify(StorageRow row, EdgeDisk card, int? wear, int wearAlert)
    {
        var filled = row with
        {
            TotalKb = card.IsConnected ? card.TotalKb : null,
            FreeKb = card.IsConnected ? card.FreeKb : null,
            WearPercent = wear,
            WearAlertPercent = wearAlert,
            Encrypted = card.Encrypted,
            RetentionDays = card.CleanupMaxAgeDays,
            Mounted = card.IsConnected,
        };
        return card.ProblemText is { } problem ? filled with { State = CardStates.Problem, Text = problem }
            : wear >= wearAlert ? filled with { State = CardStates.Warning, Text = StorageTexts.Worn(wear.Value, wearAlert) }
            : filled with { State = CardStates.Ok, Text = null };
    }

    private StorageState State(bool withRows)
    {
        if (_sweep is not { } sweep)
        {
            return new StorageState(false, 0, 0, 0, 0, 0, 0, 0, null, []);
        }

        var now = sweep.Snapshot();
        int Count(string state) => now.Rows.Count(r => r.State == state);
        return new StorageState(
            now.Running,
            now.Done,
            now.Total,
            Count(CardStates.Ok),
            Count(CardStates.Warning),
            Count(CardStates.Problem),
            Count(CardStates.NoCard),
            Count(CardStates.Error),
            now.FinishedUtc,
            withRows ? sweep.Sorted(now.Rows) : []);
    }

    private void PublishRows(IReadOnlyList<StorageRow> rows, IReadOnlyList<Guid> removed)
    {
        if (_ctx?.Events is not { } events)
        {
            return;
        }

        foreach (var chunk in rows.Chunk(EdgeStorageInfo.MaxRowsPerEvent))
        {
            events.Publish(EdgeStorageMethods.RowsTopic, EdgeStorageJson.Serialize(new RowsEvent(chunk, [])));
        }

        if (removed.Count > 0)
        {
            events.Publish(EdgeStorageMethods.RowsTopic, EdgeStorageJson.Serialize(new RowsEvent([], removed)));
        }
    }

    private void PublishState() => _ctx?.Events?.Publish(EdgeStorageMethods.StateTopic, EdgeStorageJson.Serialize(State(withRows: false)));

    /// <summary>A read that may fail without failing the row (health and alert levels are extras).</summary>
    private static async Task<T?> Optional<T>(Func<Task<T>> read, CancellationToken ct)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return default;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Edge storage: {Action} started on {Count} devices")]
    private static partial void LogRun(ILogger logger, string action, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Edge storage: reading a camera after its task failed")]
    private static partial void LogRefreshFailed(ILogger logger, Exception ex);
}
