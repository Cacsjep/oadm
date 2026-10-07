using System.Net;

using Oadm.Core.Devices;
using Oadm.Core.Tasks;

using CoreDiscoveryService = Oadm.Core.Discovery.DiscoveryService;

namespace Oadm.Server.Devices;

/// <summary>Timing of the periodic re-find of moved devices.</summary>
public sealed class DeviceRelocationOptions
{
    /// <summary>First run after server start. Default 30 s (the first status poll has run by then).</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Pause between runs. Default 5 minutes.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long one run listens to mDNS. Default 15 s.</summary>
    public TimeSpan BrowseDuration { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Periodic mDNS re-find: when a managed device is unreachable at its stored address (status Unreachable, address
/// is an IP literal), a short zero-conf browse looks for its serial number; when the device announces another
/// address there, <see cref="DeviceAddressService.TryRelocateAsync"/> verifies it and moves the record (logged,
/// published as a device change). Nothing is browsed while no device is unreachable.
/// </summary>
public sealed partial class DeviceRelocationHostedService(
    CoreDiscoveryService discovery,
    DeviceRepository devices,
    DeviceAddressService addresses,
    DeviceRelocationOptions options,
    ILogger<DeviceRelocationHostedService> logger) : BackgroundService
{
    /// <summary>One run: browse for the unreachable devices and move the ones found elsewhere. Returns how many moved.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var candidates = (await devices.ListDevicesAsync(ct).ConfigureAwait(false))
            .Where(d => d.Status == Sdk.Devices.DeviceStatus.Unreachable && !DeviceAddressService.UsesHostName(d))
            .Select(d => d.Serial)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var seen = await BrowseAsync(candidates, ct).ConfigureAwait(false);
        var moved = 0;
        foreach (var (serial, address) in seen)
        {
            var result = await addresses.TryRelocateAsync(serial, address, ct).ConfigureAwait(false);
            if (result == DeviceAddressChangeResult.Updated)
            {
                moved++;
                LogRelocated(serial, address);
            }
        }

        return moved;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.InitialDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRunFailed(ex);
                }

                await Task.Delay(options.Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<Dictionary<string, string>> BrowseAsync(HashSet<string> serials, CancellationToken ct)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var session = discovery.StartZeroConf();
        try
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(options.BrowseDuration);
            try
            {
                await foreach (var e in discovery.WatchAsync(session.Id, window.Token).ConfigureAwait(false))
                {
                    if (e.Device is { } device && serials.Contains(device.Serial) && !Equals(device.Address, IPAddress.None))
                    {
                        found[device.Serial] = device.Address.ToString();
                        if (found.Count == serials.Count)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Browse window over.
            }
        }
        finally
        {
            await discovery.StopAsync(session.Id).ConfigureAwait(false);
        }

        return found;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {Serial} found again by mDNS at {Address}; its address was updated")]
    private partial void LogRelocated(string serial, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-finding moved devices failed")]
    private partial void LogRunFailed(Exception ex);
}
