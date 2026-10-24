using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Vapix;

namespace Oadm.Core.Devices;

/// <summary>
/// The device grid's "Certificate expires" chip follows PKI's Expiry warning policies (docs/spec/pki.md "Policies"): when PKI
/// hands the host new days (<see cref="TrustAnchorRegistry.ExpiryWarningChanged"/>), every device is published once as
/// Updated in one batch (nothing is written; a device with a newer change published meanwhile keeps it), so the watchers
/// send each row again with its days. Changes in a burst run as one pass.
/// </summary>
public sealed partial class ExpiryWarningPublisher : IAsyncDisposable
{
    private readonly TrustAnchorRegistry _anchors;
    private readonly DeviceRepository _devices;
    private readonly IDeviceChangeFeed _feed;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private int _pending;
    private Task _running = Task.CompletedTask;
    private int _started;
    private int _disposed;

    public ExpiryWarningPublisher(TrustAnchorRegistry anchors, DeviceRepository devices, IDeviceChangeFeed feed, ILogger<ExpiryWarningPublisher>? logger = null)
    {
        _anchors = anchors ?? throw new ArgumentNullException(nameof(anchors));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Passes so far (tests).</summary>
    public int Published => Volatile.Read(ref _published);

    private int _published;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _anchors.ExpiryWarningChanged += OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1 || _stopping.IsCancellationRequested)
        {
            return; // the pass that runs reads the newest days
        }

        lock (_gate)
        {
            _running = Task.Run(PublishAsync);
        }
    }

    private async Task PublishAsync()
    {
        try
        {
            Volatile.Write(ref _pending, 0);
            var readAt = _feed.Sequence;
            var devices = await _devices.ListDevicesAsync(_stopping.Token).ConfigureAwait(false);
            if (devices.Count > 0)
            {
                var published = _feed.PublishUnlessNewer([.. devices.Select(d => new DeviceChange(DeviceChangeKind.Updated, d.Id, d))], readAt);
                LogPublished(published);
            }

            Interlocked.Increment(ref _published);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // stopping
        }
#pragma warning disable CA1031 // The rows show the new days with their next change; the reason goes to the server log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(ex);
        }
    }

    /// <summary>Stops following the days (the server's stop, then the container's dispose: once).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _anchors.ExpiryWarningChanged -= OnChanged;
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task running;
        lock (_gate)
        {
            running = _running;
        }

        await running.ConfigureAwait(false);
        _stopping.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Certificate warning days changed: {Count} devices published")]
    private partial void LogPublished(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing the devices for new certificate warning days failed")]
    private partial void LogFailed(Exception ex);
}
