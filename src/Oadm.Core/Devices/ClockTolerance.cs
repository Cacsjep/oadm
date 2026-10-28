using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Settings;

namespace Oadm.Core.Devices;

/// <summary>
/// Live value of <c>Polling.ClockToleranceSeconds</c> (default 2): devices whose clock differs more count as off
/// (<see cref="ClockHealth.IsOff(Device, int)"/>; the Clock column, the event Any device: clock off). <see cref="LoadAsync"/>
/// reads the stored value at startup; every later write through <see cref="ServerSettingsStore"/> updates it, and the
/// devices whose off state or beyond-tolerance flag changes with it are published once as Updated in one batch (nothing is written; a device with
/// a newer change published meanwhile keeps it), so the watchers and the event rules see them at once.
/// </summary>
public sealed partial class ClockTolerance : IAsyncDisposable
{
    private readonly ServerSettingsStore? _store;
    private readonly DeviceRepository? _devices;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private Task _running = Task.CompletedTask;
    private int _seconds = ServerSettings.DefaultClockToleranceSeconds;
    private int _published;
    private int _disposed;

    public ClockTolerance(ServerSettingsStore store, DeviceRepository devices, ILogger<ClockTolerance>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _store.Changed += OnSettingChanged;
    }

    /// <summary>A fixed value without a store (tests, tools).</summary>
    public ClockTolerance(int seconds = ServerSettings.DefaultClockToleranceSeconds)
    {
        _seconds = seconds;
        _logger = NullLogger.Instance;
    }

    /// <summary>The allowed difference in seconds.</summary>
    public int Seconds => Volatile.Read(ref _seconds);

    /// <summary>Whether the device's clock counts as off with the current tolerance.</summary>
    public bool IsOff(Device device) => ClockHealth.IsOff(device, Seconds);

    /// <summary>Passes that published devices after a change (tests).</summary>
    public int Published => Volatile.Read(ref _published);

    /// <summary>The newest pass (tests wait for it).</summary>
    public Task Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>Reads the stored value (or the default).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return;
        }

        var settings = await _store.GetServerSettingsAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _seconds, settings.ClockToleranceSeconds);
    }

    /// <summary>Changes the value and publishes the devices whose off state changes (the store's writes call it too).</summary>
    public void Set(int seconds)
    {
        if (seconds is < ServerSettings.MinClockToleranceSeconds or > ServerSettings.MaxClockToleranceSeconds)
        {
            return;
        }

        var old = Interlocked.Exchange(ref _seconds, seconds);
        if (old == seconds || _devices is null || _stopping.IsCancellationRequested)
        {
            return;
        }

        lock (_gate)
        {
            var previous = _running;
            _running = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false); // passes run in order: the last one decides
                await PublishAsync(old, seconds).ConfigureAwait(false);
            });
        }
    }

    private async Task PublishAsync(int old, int seconds)
    {
        try
        {
            var feed = _devices!.Changes;
            var readAt = feed.Sequence;
            var devices = await _devices.ListDevicesAsync(_stopping.Token).ConfigureAwait(false);
            var changed = new List<DeviceChange>();
            foreach (var device in devices)
            {
                if (ClockHealth.IsOff(device, old) != ClockHealth.IsOff(device, seconds)
                    || ClockHealth.IsBeyondTolerance(device, old) != ClockHealth.IsBeyondTolerance(device, seconds))
                {
                    changed.Add(new DeviceChange(DeviceChangeKind.Updated, device.Id, device));
                }
            }

            if (changed.Count > 0)
            {
                var published = feed.PublishUnlessNewer(changed, readAt);
                LogPublished(seconds, published);
            }

            Interlocked.Increment(ref _published);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // stopping
        }
#pragma warning disable CA1031 // The rows show the new state with their next change; the reason goes to the server log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(ex);
        }
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key != SettingKeys.PollingClockToleranceSeconds)
        {
            return;
        }

        var seconds = ServerSettings.DefaultClockToleranceSeconds;
        if (e.ValueJson is not null)
        {
            try
            {
                seconds = JsonSerializer.Deserialize<int>(e.ValueJson);
            }
            catch (JsonException)
            {
                return;
            }
        }

        Set(seconds);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_store is not null)
        {
            _store.Changed -= OnSettingChanged;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await Running.ConfigureAwait(false);
        _stopping.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Allowed clock difference changed to {Seconds} s: {Count} devices published")]
    private partial void LogPublished(int seconds, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing the devices for a new allowed clock difference failed")]
    private partial void LogFailed(Exception ex);
}
