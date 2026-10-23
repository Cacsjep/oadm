using System.Collections.Concurrent;
using System.Net;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ImageHealth.Monitoring;

/// <summary>Timing and limits of <see cref="ImageHealthMonitor"/>; tests shorten them.</summary>
public sealed class ImageHealthOptions
{
    /// <summary>Cameras asked at the same time (24; 200 cameras = about 9 rounds).</summary>
    public int CheckParallelism { get; init; } = 24;

    /// <summary>Time for one status request (3 s).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Changed rows and counts are pushed at most this often.</summary>
    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// The status of AXIS Image Health Analytics on every camera, read only on request (no
/// background polling, no open connection). <see cref="StartCheck(string, IReadOnlySet{Guid}?)"/> asks the page's video
/// devices once for the app status (<see cref="ImageHealthPluginInfo.StatusPath"/>, <see cref="ImageHealthOptions.CheckParallelism"/>
/// at a time, stored credentials): the page calls it when it opens, on Refresh and, with Auto refresh on, every 10 s. Rows
/// are updated in place between checks, so a detection change is seen and timed. Read-only for devices: one GET per camera
/// and check. The table machinery (one check at a time, rows by device, batched events) is the shared
/// <see cref="DeviceSweep{TRow}"/>, one per device scope (<see cref="DeviceSweeps{TRow}"/>): the users of a device group
/// share the table of their group's cameras, and its rows and counts reach only them.
/// </summary>
public sealed partial class ImageHealthMonitor : IAsyncDisposable
{
    private static readonly ImageHealthState Empty = new(false, 0, 0, 0, 0, 0, 0, null, []);

    private readonly ICorePluginContext _ctx;
    private readonly ImageHealthOptions _options;
    private readonly DeviceSweeps<ImageHealthRow> _sweeps;
    private readonly ConcurrentDictionary<string, ScopeCounts> _counts = new(StringComparer.OrdinalIgnoreCase);

    public ImageHealthMonitor(ICorePluginContext ctx, ImageHealthOptions options)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var sweepOptions = new DeviceSweepOptions { Parallelism = options.CheckParallelism, DeviceTimeout = options.RequestTimeout, PublishInterval = options.PublishInterval, Time = options.Time };
        _sweeps = new DeviceSweeps<ImageHealthRow>(ctx, (key, devices) => new DeviceSweep<ImageHealthRow>(
            ctx,
            devices,
            sweepOptions,
            d => d.HasVideo,
            ReadRowAsync,
            r => r.Address,
            (rows, removed) => PublishRows(key, rows, removed),
            () => PublishState(key))
        {
            Finished = () => LogFinished(key),
        });
    }

    /// <summary>Whether the check of the table of all devices runs (tests).</summary>
    public bool IsChecking => _sweeps.Existing(ICallContext.AllDevicesScopeKey)?.IsRunning ?? false;

    /// <summary>The running check of the table of all devices (tests wait for it).</summary>
    public Task CurrentCheck => CurrentCheckOf(ICallContext.AllDevicesScopeKey);

    /// <summary>The scopes that have a table (tests).</summary>
    internal IReadOnlyList<string> Scopes => _sweeps.Keys;

    /// <summary>The running check of a scope's table (tests wait for it).</summary>
    public Task CurrentCheckOf(string scopeKey) => _sweeps.Existing(scopeKey)?.Current ?? Task.CompletedTask;

    /// <summary>A check in the table of all devices (tests).</summary>
    public ImageHealthState StartCheck(IReadOnlySet<Guid>? only = null) => StartCheck(ICallContext.AllDevicesScopeKey, only);

    /// <summary>
    /// Starts a check of <paramref name="only"/> (null = every video device of the scope) in the table of
    /// <paramref name="scopeKey"/> unless one runs; returns the state at once (rows and progress follow as events). A deleted
    /// group has no table: an empty state.
    /// </summary>
    public ImageHealthState StartCheck(string scopeKey, IReadOnlySet<Guid>? only)
    {
        if (_sweeps.For(scopeKey) is not { } sweep)
        {
            return Empty;
        }

        var counts = _counts.GetOrAdd(scopeKey, _ => new ScopeCounts());
        lock (counts)
        {
            // A check while another runs is read right after it (DeviceSweep.Start): the counts cover both choices.
            var running = sweep.IsRunning;
            var scope = counts.Scope;
            counts.Scope = !running ? only
                : scope is null || only is null ? null
                : scope.Union(only).ToHashSet();
            sweep.Start(only);
        }

        return Snapshot(scopeKey, withRows: true);
    }

    public ValueTask DisposeAsync() => _sweeps.DisposeAsync();

    /// <summary>The counts and rows of the table of all devices (tests).</summary>
    public ImageHealthState Snapshot(bool withRows) => Snapshot(ICallContext.AllDevicesScopeKey, withRows);

    /// <summary>Current counts and, with <paramref name="withRows"/>, every row of the scope's table sorted by address.</summary>
    public ImageHealthState Snapshot(string scopeKey, bool withRows) =>
        (_ctx.DeviceScopes is { } scopes && !scopes.Exists(scopeKey)) || _sweeps.Existing(scopeKey) is not { } sweep
            ? Empty
            : Snapshot(scopeKey, sweep, sweep.Snapshot(), withRows);

    /// <summary>
    /// The counts and rows of the caller's table, only of cameras the caller sees now (a camera that left the group drops out
    /// at once, before the group's rows are pruned).
    /// </summary>
    public async Task<ImageHealthState> SnapshotAsync(ICallContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var scopeKey = caller.DeviceScopeKey;
        return (_ctx.DeviceScopes is { } scopes && !scopes.Exists(scopeKey)) || _sweeps.Existing(scopeKey) is not { } sweep
            ? Empty
            : Snapshot(scopeKey, sweep, await sweep.SnapshotAsync(caller, ct).ConfigureAwait(false), withRows: true);
    }

    private ImageHealthState Snapshot(string scopeKey, DeviceSweep<ImageHealthRow> sweep, SweepSnapshot<ImageHealthRow> now, bool withRows)
    {
        var counts = _counts.GetOrAdd(scopeKey, _ => new ScopeCounts());
        int withoutApp;
        lock (counts)
        {
            if (!now.Running)
            {
                // The video devices of the last check that are not in the table answered "not installed"; rows of cameras
                // another page checked do not count.
                var scope = counts.Scope;
                var rows = scope is null ? now.Rows.Count : now.Rows.Count(r => scope.Contains(r.DeviceId));
                counts.WithoutApp = Math.Max(0, now.Total - rows);
            }

            withoutApp = counts.WithoutApp;
        }

        int Count(string app) => now.Rows.Count(r => r.App == app);
        return new ImageHealthState(
            now.Running,
            now.Done,
            now.Total,
            Count(AppStates.Running),
            Count(AppStates.NotRunning),
            withoutApp,
            Count(AppStates.Error),
            now.FinishedUtc,
            withRows ? sweep.Sorted(now.Rows) : []);
    }

    /// <summary>Pushes the rows changed since the last call and the counts when they changed, in every table (tests call it directly).</summary>
    internal void Publish()
    {
        foreach (var sweep in _sweeps.All)
        {
            sweep.Publish();
        }
    }

    /// <summary>One status request: the app state and its detections, or the reason the camera could not be read.</summary>
    internal async Task<StatusResult> ReadStatusAsync(IDeviceInfo device, CancellationToken ct)
    {
        if (DeviceMessages.Refusal(device, refuseUnreachable: true) is { } refusal)
        {
            return StatusResult.Failed(refusal);
        }

        try
        {
            // Background work of the sweep (no caller): the sweep's devices are the scope's already.
            var client = await _ctx.Vapix.CreateAsync(device.Id, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, ImageHealthPluginInfo.StatusPath);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return new StatusResult(AihaAppState.NotInstalled, null, null);
                case HttpStatusCode.ServiceUnavailable:
                    return new StatusResult(AihaAppState.Stopped, null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return StatusResult.Failed(DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? DeviceMessages.ServerError((int)response.StatusCode));
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new StatusResult(AihaAppState.Running, AihaParsing.ParseStatus(json), null);
        }
        catch (System.Text.Json.JsonException)
        {
            return StatusResult.Failed("The app sent a status OADM cannot read");
        }
#pragma warning disable CA1031 // One camera's failure must not stop the others.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return StatusResult.Failed(Failure(ex));
        }
    }

    /// <summary>The usual text when a camera cannot be read (as <see cref="DeviceSweep{TRow}.Failure"/>).</summary>
    private string Failure(Exception ex) => ex switch
    {
        OperationCanceledException => DeviceMessages.Timeout(_options.RequestTimeout),
        HttpRequestException { InnerException: System.Net.Sockets.SocketException socket } => DeviceMessages.Unreachable(socket.Message),
        HttpRequestException http => DeviceMessages.Unreachable(http.Message),
        _ => ex.Message,
    };

    /// <summary>
    /// The row of one camera: null without the app (it leaves the table). A failed request keeps the detections as they
    /// were; a stopped app clears them. A detection that changes between two answers of the running app sets
    /// <see cref="ImageHealthRow.ChangedUtc"/>.
    /// </summary>
    private async Task<ImageHealthRow?> ReadRowAsync(IDeviceInfo device, ImageHealthRow? previous, CancellationToken ct)
    {
        var result = await ReadStatusAsync(device, ct).ConfigureAwait(false);
        if (result.Error is null && result.State == AihaAppState.NotInstalled)
        {
            return null;
        }

        var row = previous ?? new ImageHealthRow(device.Id, device.Address, device.Model, AppStates.Checking, null, null, null, null, null, null, null);
        if (result.Error is not null)
        {
            return row with { App = AppStates.Error, Text = result.Error };
        }

        if (result.State != AihaAppState.Running)
        {
            return row with { App = AppStates.NotRunning, Text = null, Blur = null, Block = null, Redirect = null, UnderExposure = null, Unsuitability = null };
        }

        var values = AihaDetections.All.Select(d => result.Detections?.GetValueOrDefault(d)).ToArray();
        var changed = AihaDetections.All.Select((d, i) => row.Detection(d) is { } before && before != values[i]).Any(c => c);
        return row with
        {
            App = AppStates.Running,
            Text = null,
            Blur = values[0],
            Block = values[1],
            Redirect = values[2],
            UnderExposure = values[3],
            Unsuitability = values[4],
            ChangedUtc = changed ? _options.Time.GetUtcNow() : row.ChangedUtc,
        };
    }

    /// <summary>Rows of a scope's table go only to the users of that scope.</summary>
    private void PublishRows(string scopeKey, IReadOnlyList<ImageHealthRow> rows, IReadOnlyList<Guid> removed)
    {
        if (_ctx.Events is not { } events)
        {
            return;
        }

        var audience = PluginEventAudience.DeviceScope(scopeKey);
        foreach (var chunk in rows.Chunk(ImageHealthPluginInfo.MaxRowsPerEvent))
        {
            events.Publish(ImageHealthMethods.RowsTopic, ImageHealthJson.Serialize(new RowsEvent(chunk, [])), audience);
        }

        if (removed.Count > 0)
        {
            events.Publish(ImageHealthMethods.RowsTopic, ImageHealthJson.Serialize(new RowsEvent([], removed)), audience);
        }
    }

    private void PublishState(string scopeKey)
    {
        var state = Snapshot(scopeKey, withRows: false);
        _ctx.Events?.Publish(ImageHealthMethods.StateTopic, ImageHealthJson.Serialize(state), PluginEventAudience.DeviceScope(scopeKey));
    }

    private void LogFinished(string scopeKey)
    {
        var state = Snapshot(scopeKey, withRows: false);
        LogChecked(_ctx.Logger, state.Total, state.Running, state.NotRunning, state.Failed);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Image Health Dashboard: {Total} video devices checked, {Running} with the app running, {NotRunning} with the app stopped, {Failed} could not be read")]
    private static partial void LogChecked(ILogger logger, int total, int running, int notRunning, int failed);

    /// <summary>The choice of a scope's last check and its cameras without the app (under its own lock).</summary>
    private sealed class ScopeCounts
    {
        public int WithoutApp { get; set; }

        public IReadOnlySet<Guid>? Scope { get; set; }
    }
}

/// <summary>One status request: the app state, its detections while it runs, or why the camera could not be read.</summary>
internal sealed record StatusResult(AihaAppState State, IReadOnlyDictionary<string, string>? Detections, string? Error)
{
    public static StatusResult Failed(string error) => new(AihaAppState.NotInstalled, null, error);
}
