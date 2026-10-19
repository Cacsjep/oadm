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
/// background polling, no open connection). <see cref="StartCheck"/> asks the page's video devices once for the app status
/// (<see cref="ImageHealthPluginInfo.StatusPath"/>, <see cref="ImageHealthOptions.CheckParallelism"/> at a time, stored
/// credentials): the page calls it when it opens, on Refresh and, with Auto refresh on, every 10 s. Rows are updated in
/// place between checks, so a detection change is seen and timed. Read-only for devices: one GET per camera and check.
/// The table machinery (one check at a time, rows by device, batched events) is the shared <see cref="DeviceSweep{TRow}"/>.
/// </summary>
public sealed partial class ImageHealthMonitor : IAsyncDisposable
{
    private readonly ICorePluginContext _ctx;
    private readonly ImageHealthOptions _options;
    private readonly DeviceSweep<ImageHealthRow> _sweep;
    private int _withoutApp;
    private IReadOnlySet<Guid>? _scope;
    private readonly Lock _scopeSync = new();

    public ImageHealthMonitor(ICorePluginContext ctx, ImageHealthOptions options)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sweep = new DeviceSweep<ImageHealthRow>(
            ctx,
            new DeviceSweepOptions { Parallelism = options.CheckParallelism, DeviceTimeout = options.RequestTimeout, PublishInterval = options.PublishInterval, Time = options.Time },
            d => d.HasVideo,
            ReadRowAsync,
            r => r.Address,
            PublishRows,
            PublishState)
        {
            Finished = LogFinished,
        };
    }

    /// <summary>Whether a check runs (tests).</summary>
    public bool IsChecking => _sweep.IsRunning;

    /// <summary>The running check (tests wait for it).</summary>
    public Task CurrentCheck => _sweep.Current;

    /// <summary>
    /// Starts a check of <paramref name="only"/> (null = every video device) unless one runs; returns the state at once
    /// (rows and progress follow as events).
    /// </summary>
    public ImageHealthState StartCheck(IReadOnlySet<Guid>? only = null)
    {
        lock (_scopeSync)
        {
            // A check while another runs is read right after it (DeviceSweep.Start): the counts cover both choices.
            var running = _sweep.IsRunning;
            var scope = Volatile.Read(ref _scope);
            Volatile.Write(ref _scope, !running ? only
                : scope is null || only is null ? null
                : scope.Union(only).ToHashSet());
            _sweep.Start(only);
        }

        return Snapshot(withRows: true);
    }

    public ValueTask DisposeAsync() => _sweep.DisposeAsync();

    /// <summary>Current counts and, with <paramref name="withRows"/>, every row sorted by address.</summary>
    public ImageHealthState Snapshot(bool withRows)
    {
        var now = _sweep.Snapshot();
        if (!now.Running)
        {
            // The video devices of the last check that are not in the table answered "not installed"; rows of cameras
            // another page checked do not count.
            var scope = Volatile.Read(ref _scope);
            var rows = scope is null ? now.Rows.Count : now.Rows.Count(r => scope.Contains(r.DeviceId));
            Volatile.Write(ref _withoutApp, Math.Max(0, now.Total - rows));
        }

        int Count(string app) => now.Rows.Count(r => r.App == app);
        return new ImageHealthState(
            now.Running,
            now.Done,
            now.Total,
            Count(AppStates.Running),
            Count(AppStates.NotRunning),
            Volatile.Read(ref _withoutApp),
            Count(AppStates.Error),
            now.FinishedUtc,
            withRows ? _sweep.Sorted(now.Rows) : []);
    }

    /// <summary>Pushes the rows changed since the last call and the counts when they changed (tests call it directly).</summary>
    internal void Publish() => _sweep.Publish();

    /// <summary>One status request: the app state and its detections, or the reason the camera could not be read.</summary>
    internal async Task<StatusResult> ReadStatusAsync(IDeviceInfo device, CancellationToken ct)
    {
        if (DeviceMessages.Refusal(device, refuseUnreachable: true) is { } refusal)
        {
            return StatusResult.Failed(refusal);
        }

        try
        {
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
            return StatusResult.Failed(_sweep.Failure(ex));
        }
    }

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

    private void PublishRows(IReadOnlyList<ImageHealthRow> rows, IReadOnlyList<Guid> removed)
    {
        if (_ctx.Events is not { } events)
        {
            return;
        }

        foreach (var chunk in rows.Chunk(ImageHealthPluginInfo.MaxRowsPerEvent))
        {
            events.Publish(ImageHealthMethods.RowsTopic, ImageHealthJson.Serialize(new RowsEvent(chunk, [])));
        }

        if (removed.Count > 0)
        {
            events.Publish(ImageHealthMethods.RowsTopic, ImageHealthJson.Serialize(new RowsEvent([], removed)));
        }
    }

    private void PublishState()
    {
        var state = Snapshot(withRows: false);
        _ctx.Events?.Publish(ImageHealthMethods.StateTopic, ImageHealthJson.Serialize(state));
    }

    private void LogFinished()
    {
        var state = Snapshot(withRows: false);
        LogChecked(_ctx.Logger, state.Total, state.Running, state.NotRunning, state.Failed);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Image Health Dashboard: {Total} video devices checked, {Running} with the app running, {NotRunning} with the app stopped, {Failed} could not be read")]
    private static partial void LogChecked(ILogger logger, int total, int running, int notRunning, int failed);
}

/// <summary>One status request: the app state, its detections while it runs, or why the camera could not be read.</summary>
internal sealed record StatusResult(AihaAppState State, IReadOnlyDictionary<string, string>? Detections, string? Error)
{
    public static StatusResult Failed(string error) => new(AihaAppState.NotInstalled, null, error);
}
