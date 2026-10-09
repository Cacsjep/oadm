using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ImageHealth.Monitoring;

/// <summary>Timing and limits of <see cref="ImageHealthMonitor"/>; tests shorten them.</summary>
public sealed class ImageHealthOptions
{
    /// <summary>Cameras asked at the same time (user decision 2026-10-09: 24; 200 cameras = about 9 rounds).</summary>
    public int CheckParallelism { get; init; } = 24;

    /// <summary>Time for one status request (user decision 2026-10-09: 3 s).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Changed rows and counts are pushed at most this often.</summary>
    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// The status of AXIS Image Health Analytics on every camera, read only on request (user decision 2026-10-09: no
/// background polling, no open connection). <see cref="StartCheck"/> asks every video device once for the app status
/// (<see cref="ImageHealthPluginInfo.StatusPath"/>, <see cref="ImageHealthOptions.CheckParallelism"/> at a time, stored
/// credentials): the page calls it when it opens, on Refresh and, with Auto refresh on, every 10 s. Rows are updated in
/// place between checks, so a detection change is seen and timed. Read-only for devices: one GET per camera and check.
/// </summary>
public sealed partial class ImageHealthMonitor : IAsyncDisposable
{
    private readonly ICorePluginContext _ctx;
    private readonly ImageHealthOptions _options;
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, RowState> _rows = [];
    private readonly HashSet<Guid> _dirty = [];
    private readonly HashSet<Guid> _dirtyRemoved = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _publishLoop;
    private Task _check = Task.CompletedTask;
    private bool _checking;
    private int _checked;
    private int _total;
    private int _withoutApp;
    private DateTimeOffset? _checkedUtc;
    private bool _stateDirty;

    public ImageHealthMonitor(ICorePluginContext ctx, ImageHealthOptions options)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var token = _stopping.Token;
        _publishLoop = Task.Run(() => PublishLoopAsync(token), CancellationToken.None);
    }

    /// <summary>Whether a check runs (tests).</summary>
    public bool IsChecking
    {
        get
        {
            lock (_sync)
            {
                return _checking;
            }
        }
    }

    /// <summary>The running check (tests wait for it).</summary>
    public Task CurrentCheck
    {
        get
        {
            lock (_sync)
            {
                return _check;
            }
        }
    }

    /// <summary>Starts a check unless one runs; returns the state at once (rows and progress follow as events).</summary>
    public ImageHealthState StartCheck()
    {
        lock (_sync)
        {
            if (!_checking && !_stopping.IsCancellationRequested)
            {
                _checking = true;
                _checked = 0;
                _stateDirty = true;
                var token = _stopping.Token;
                _check = Task.Run(() => CheckAsync(token), CancellationToken.None);
            }
        }

        return Snapshot(withRows: true);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(CurrentCheck, _publishLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stopping.Dispose();
    }

    /// <summary>Current counts and, with <paramref name="withRows"/>, every row sorted by address.</summary>
    public ImageHealthState Snapshot(bool withRows)
    {
        lock (_sync)
        {
            IReadOnlyList<ImageHealthRow> rows = withRows ? [.. _rows.Values.Select(r => r.ToRow()).OrderBy(r => r.Address, AddressComparer.Instance)] : [];
            return new ImageHealthState(
                _checking,
                _checked,
                _total,
                _rows.Values.Count(r => r.App == AppStates.Running),
                _rows.Values.Count(r => r.App == AppStates.NotRunning),
                _withoutApp,
                _rows.Values.Count(r => r.App == AppStates.Error),
                _checkedUtc,
                rows);
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            var devices = (await _ctx.Devices.ListAsync(ct).ConfigureAwait(false)).Where(d => d.HasVideo).ToList();
            var ids = devices.Select(d => d.Id).ToHashSet();
            var withoutApp = 0;
            lock (_sync)
            {
                _total = devices.Count;
                foreach (var gone in _rows.Keys.Where(id => !ids.Contains(id)).ToList())
                {
                    Remove(gone); // removed from OADM or no longer a video device
                }

                _stateDirty = true;
            }

            await Parallel.ForEachAsync(devices, new ParallelOptions { MaxDegreeOfParallelism = _options.CheckParallelism, CancellationToken = ct }, async (device, token) =>
            {
                var result = await ReadStatusAsync(device, token).ConfigureAwait(false);
                lock (_sync)
                {
                    _checked++;
                    _stateDirty = true;
                    if (result.Error is null && result.State == AihaAppState.NotInstalled)
                    {
                        withoutApp++;
                        if (_rows.ContainsKey(device.Id))
                        {
                            Remove(device.Id); // the app was removed
                        }

                        return;
                    }

                    if (!_rows.TryGetValue(device.Id, out var row))
                    {
                        row = new RowState(device);
                        _rows[device.Id] = row;
                    }

                    row.Apply(result, _options.Time.GetUtcNow());
                    _dirty.Add(device.Id);
                }
            }).ConfigureAwait(false);

            lock (_sync)
            {
                _withoutApp = withoutApp;
                _checkedUtc = _options.Time.GetUtcNow();
            }

            var state = Snapshot(withRows: false);
            LogChecked(_ctx.Logger, state.Total, state.Running, state.NotRunning, state.Failed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // A failed check must not stop the plugin; the page can check again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCheckFailed(_ctx.Logger, ex);
        }
        finally
        {
            lock (_sync)
            {
                _checking = false;
                _stateDirty = true;
            }

            Publish();
        }
    }

    /// <summary>One status request: the app state and its detections, or the reason the camera could not be read.</summary>
    internal async Task<StatusResult> ReadStatusAsync(IDeviceInfo device, CancellationToken ct)
    {
        if (RefusalFor(device.Status) is { } refusal)
        {
            return StatusResult.Failed(refusal);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            var client = await _ctx.Vapix.CreateAsync(device.Id, timeout.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, ImageHealthPluginInfo.StatusPath);
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
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

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return new StatusResult(AihaAppState.Running, AihaParsing.ParseStatus(json), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusResult.Failed(DeviceMessages.Timeout(_options.RequestTimeout));
        }
        catch (HttpRequestException ex)
        {
            return StatusResult.Failed(ex.InnerException is SocketException socket ? DeviceMessages.Unreachable(socket.Message) : DeviceMessages.Unreachable(ex.Message));
        }
        catch (System.Text.Json.JsonException)
        {
            return StatusResult.Failed("The app sent a status OADM cannot read");
        }
#pragma warning disable CA1031 // One camera's failure must not stop the others.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return StatusResult.Failed(ex.Message);
        }
    }

    /// <summary>The refusal text for a device status, or null when the camera may be contacted.</summary>
    internal static string? RefusalFor(DeviceStatus status) => status switch
    {
        DeviceStatus.CertificateChanged => DeviceMessages.CertificateChanged,
        DeviceStatus.CredentialsRequired => "Credentials required - the device rejects the stored credentials",
        DeviceStatus.PasswordNotSet => "Password not set - the device is in factory default",
        DeviceStatus.Unreachable => DeviceMessages.Unreachable("the device does not answer"),
        _ => null,
    };

    private void Remove(Guid id)
    {
        _rows.Remove(id);
        _dirty.Remove(id);
        _dirtyRemoved.Add(id);
    }

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.PublishInterval, _options.Time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Publish();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Pushes the rows changed since the last call and the counts when they changed (tests call it directly).</summary>
    internal void Publish()
    {
        if (_ctx.Events is not { } events)
        {
            return;
        }

        List<ImageHealthRow> rows;
        List<Guid> removed;
        bool state;
        lock (_sync)
        {
            rows = [.. _dirty.Where(_rows.ContainsKey).Select(id => _rows[id].ToRow())];
            _dirty.Clear();
            removed = [.. _dirtyRemoved];
            _dirtyRemoved.Clear();
            state = _stateDirty;
            _stateDirty = false;
        }

        foreach (var chunk in rows.Chunk(ImageHealthPluginInfo.MaxRowsPerEvent))
        {
            events.Publish(ImageHealthMethods.RowsTopic, ImageHealthJson.Serialize(new RowsEvent(chunk, [])));
        }

        if (removed.Count > 0)
        {
            events.Publish(ImageHealthMethods.RowsTopic, ImageHealthJson.Serialize(new RowsEvent([], removed)));
        }

        if (state)
        {
            events.Publish(ImageHealthMethods.StateTopic, ImageHealthJson.Serialize(Snapshot(withRows: false)));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Image Health Dashboard: {Total} video devices checked, {Running} with the app running, {NotRunning} with the app stopped, {Failed} could not be read")]
    private static partial void LogChecked(ILogger logger, int total, int running, int notRunning, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image Health Dashboard: the check failed")]
    private static partial void LogCheckFailed(ILogger logger, Exception ex);

    private sealed class RowState(IDeviceInfo device)
    {
        private readonly string?[] _detections = new string?[AihaDetections.All.Count];

        public Guid DeviceId { get; } = device.Id;

        public string Address { get; } = device.Address;

        public string? Model { get; } = device.Model;

        public string App { get; private set; } = AppStates.Checking;

        public string? Text { get; private set; }

        public DateTimeOffset? ChangedUtc { get; private set; }

        /// <summary>
        /// The result of a status request. A failed request keeps the detections as they were; a stopped app clears them. A
        /// detection that changes between two answers of the running app sets <see cref="ChangedUtc"/>.
        /// </summary>
        public void Apply(StatusResult result, DateTimeOffset at)
        {
            if (result.Error is not null)
            {
                App = AppStates.Error;
                Text = result.Error;
                return;
            }

            Text = null;
            if (result.State != AihaAppState.Running)
            {
                App = AppStates.NotRunning;
                Array.Clear(_detections);
                return;
            }

            App = AppStates.Running;
            for (var i = 0; i < AihaDetections.All.Count; i++)
            {
                var value = result.Detections?.GetValueOrDefault(AihaDetections.All[i]);
                if (value != _detections[i])
                {
                    if (_detections[i] is not null)
                    {
                        ChangedUtc = at;
                    }

                    _detections[i] = value;
                }
            }
        }

        public ImageHealthRow ToRow() => new(
            DeviceId,
            Address,
            Model,
            App,
            Text,
            _detections[0],
            _detections[1],
            _detections[2],
            _detections[3],
            _detections[4],
            ChangedUtc);
    }
}

/// <summary>One status request: the app state, its detections while it runs, or why the camera could not be read.</summary>
internal sealed record StatusResult(AihaAppState State, IReadOnlyDictionary<string, string>? Detections, string? Error)
{
    public static StatusResult Failed(string error) => new(AihaAppState.NotInstalled, null, error);
}

/// <summary>Sorts IPv4 addresses numerically, everything else after them by text.</summary>
public sealed class AddressComparer : IComparer<string>
{
    public static readonly AddressComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = Key(x);
        var b = Key(y);
        var byNumber = a.CompareTo(b);
        return byNumber != 0 ? byNumber : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }

    private static long Key(string? address)
    {
        var host = address ?? string.Empty;
        var colon = host.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && host.IndexOf(':', colon + 1) < 0)
        {
            host = host[..colon];
        }

        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
        }

        return long.MaxValue;
    }
}
