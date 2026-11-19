using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Devices;

namespace Oadm.Core.Devices;

/// <summary>
/// Managed devices that need their first password (<see cref="IDeviceAutoAdd.WatchFactoryNew"/>): the device adder reports a
/// device it stored as Password not set, the polling a managed device whose status turned Password not set (reset to factory
/// default). Watchers (Provisioning) are called on the reporting thread, so each only queues; a watcher that throws is logged
/// and never stops the report or the others. Thread-safe.
/// </summary>
public sealed partial class FactoryNewDeviceNotices(ILogger<FactoryNewDeviceNotices>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly Lock _gate = new();
    private Action<FactoryNewDevice>[] _watchers = [];

    /// <summary>Calls <paramref name="found"/> for every report from now on until the answer is disposed.</summary>
    public IDisposable Watch(Action<FactoryNewDevice> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        lock (_gate)
        {
            _watchers = [.. _watchers, found];
        }

        return new Subscription(this, found);
    }

    /// <summary>The device needs its first password now.</summary>
    public void Report(Device device, bool added)
    {
        ArgumentNullException.ThrowIfNull(device);
        var notice = new FactoryNewDevice(device.Id, device.Serial, device.Address, device.SiteId is not null, added);
        foreach (var watcher in Volatile.Read(ref _watchers))
        {
            try
            {
                watcher(notice);
            }
#pragma warning disable CA1031 // One watcher's failure never stops the add, the poll or the other watchers.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogWatcherFailed(ex, device.Serial);
            }
        }
    }

    private void Remove(Action<FactoryNewDevice> found)
    {
        lock (_gate)
        {
            _watchers = [.. _watchers.Where(w => !ReferenceEquals(w, found))];
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A watcher of factory-new devices failed for {Serial}")]
    private partial void LogWatcherFailed(Exception ex, string serial);

    private sealed class Subscription(FactoryNewDeviceNotices owner, Action<FactoryNewDevice> found) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(found);
            }
        }
    }
}
