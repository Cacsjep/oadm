using System.Collections.Concurrent;
using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Samples.FirmwareOverview;

/// <summary>
/// The server part: keeps the table, reads the devices when the page asks, and pushes every new state to the pages.
/// </summary>
public sealed class FirmwareOverviewPlugin : ICorePlugin, IDisposable
{
    private readonly Lock _gate = new();
    private ICorePluginContext? _ctx;
    private CancellationTokenSource _stop = new();
    private FirmwareState _state = new(0, false, []);

    public string Id => FirmwareOverview.PluginId;
    public string DisplayName => "Firmware overview";
    public string? IconKey => "firmware";
    public CorePluginGroup Group => CorePluginGroup.Maintenance;
    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        _stop = new CancellationTokenSource();
        return Task.CompletedTask; // nothing to start: the page asks for a read
    }

    public async Task StopAsync(CancellationToken ct) => await _stop.CancelAsync();

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        switch (method)
        {
            case FirmwareOverview.GetState:
                return Task.FromResult<string?>(Serialize(Current));

            case FirmwareOverview.Refresh:
                if (!TryBeginRead())
                {
                    throw new InvalidOperationException("The devices are being read already.");
                }

                // The read takes a while: it runs in the background and the page follows it through events.
                _ = Task.Run(() => ReadAllAsync(_stop.Token), CancellationToken.None);
                return Task.FromResult<string?>(null);

            default:
                throw new ArgumentException("Unknown method " + method);
        }
    }

    public void Dispose() => _stop.Dispose();

    /// <summary>Reads every device, 16 at a time, 5 s each; devices OADM cannot log in to are not contacted.</summary>
    private async Task ReadAllAsync(CancellationToken ct)
    {
        var ctx = _ctx!;
        var rows = new ConcurrentBag<FirmwareRow>();
        try
        {
            var devices = await ctx.Devices.ListAsync(ct);
            await Parallel.ForEachAsync(devices, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (device, token) =>
            {
                rows.Add(await ReadAsync(ctx, device, token));
            });
        }
        catch (OperationCanceledException)
        {
            return; // the plugin stopped
        }

        Publish(_state with { Reading = false, Rows = [.. rows.OrderBy(r => r.Address, StringComparer.OrdinalIgnoreCase)] });
    }

    private static async Task<FirmwareRow> ReadAsync(ICorePluginContext ctx, IDeviceInfo device, CancellationToken ct)
    {
        var model = device.Model ?? "";
        if (DeviceMessages.Refusal(device.Status, refuseUnreachable: true) is { } refusal)
        {
            return new FirmwareRow(device.Id, device.Address, model, null, refusal);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var vapix = await ctx.Vapix.CreateAsync(device.Id, timeout.Token);
            var info = await vapix.GetBasicDeviceInfoAsync(timeout.Token);
            return new FirmwareRow(device.Id, device.Address, model, info.Version, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FirmwareRow(device.Id, device.Address, model, null, DeviceMessages.TimedOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FirmwareRow(device.Id, device.Address, model, null, ex.Message);
        }
    }

    private FirmwareState Current
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    private bool TryBeginRead()
    {
        lock (_gate)
        {
            if (_state.Reading)
            {
                return false;
            }
        }

        Publish(_state with { Reading = true });
        return true;
    }

    /// <summary>Stores the new state with the next version and sends it to every page that watches.</summary>
    private void Publish(FirmwareState state)
    {
        string json;
        lock (_gate)
        {
            _state = state with { Version = _state.Version + 1 };
            json = Serialize(_state);
        }

        _ctx?.Events?.Publish(FirmwareOverview.StateTopic, json);
    }

    private static string Serialize(FirmwareState state) => JsonSerializer.Serialize(state, FirmwareOverview.Json);
}
