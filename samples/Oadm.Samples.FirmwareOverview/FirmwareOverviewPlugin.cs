using System.Collections.Concurrent;
using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Samples.FirmwareOverview;

/// <summary>
/// The server part: keeps one table per device group (users with all devices share one), reads the devices the page
/// chose when it asks, and pushes every new state to the pages of that group.
/// </summary>
public sealed class FirmwareOverviewPlugin : ICorePlugin
{
    private readonly Lock _gate = new(); // reentrant
    private readonly Dictionary<string, FirmwareState> _states = []; // by caller.DeviceScopeKey
    private ICorePluginContext? _ctx;
    private int _version;

    public string Id => FirmwareOverview.PluginId;
    public string DisplayName => "Firmware overview";
    public string? IconKey => "firmware";
    public CorePluginGroup Group => CorePluginGroup.Maintenance;
    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        ctx.DeviceScopes!.Changed += OnGroupChanged;
        return Task.CompletedTask; // nothing to start: the page asks for a read
    }

    public Task StopAsync(CancellationToken ct)
    {
        _ctx!.DeviceScopes!.Changed -= OnGroupChanged;
        return Task.CompletedTask; // running reads are jobs: OADM ends them
    }

    // OADM calls the overload with the user's call (below).
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("The page needs the user's call.");

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        var scope = caller.DeviceScopeKey;
        switch (method)
        {
            case FirmwareOverview.GetState:
                return Serialize(InGroup(scope, StateOf(scope)));

            case FirmwareOverview.Refresh:
                // Check the request before anything starts: the page sends "all" or the chosen ids, never nothing.
                var request = JsonSerializer.Deserialize<RefreshRequest>(payloadJson ?? "null", FirmwareOverview.Json);
                if (request is null || (!request.All && (request.DeviceIds is null || request.DeviceIds.Count == 0)))
                {
                    throw new ArgumentException(FirmwareOverview.NothingChosen);
                }

                // Only the user's devices: "all" are the devices the user sees, any other id is refused.
                var devices = request.All ? await caller.Devices.ListAsync(ct) : await FindAsync(caller, request.DeviceIds, ct);
                if (!TryBeginRead(scope))
                {
                    throw new InvalidOperationException("The devices are being read already.");
                }

                // The read takes a while: a job runs it after the call, and the page follows it through events.
                _ctx!.Jobs!.Start(caller, "read", (job, token) => ReadAsync(job.Owner, scope, devices, token));
                return null;

            default:
                throw new ArgumentException("Unknown method " + method);
        }
    }

    /// <summary>The chosen devices; an id the user cannot see is refused like an unknown one, before any request.</summary>
    private static async Task<List<IDeviceInfo>> FindAsync(ICallContext caller, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var devices = new List<IDeviceInfo>(ids.Count);
        foreach (var id in ids.Distinct())
        {
            devices.Add(await caller.Devices.FindAsync(id, ct) ?? throw new DevicesNotAvailableException());
        }

        return devices;
    }

    /// <summary>Reads the devices, 16 at a time, 5 s each, through the owner's call; devices OADM cannot log in to are not contacted.</summary>
    private async Task ReadAsync(ICallContext owner, string scope, IReadOnlyList<IDeviceInfo> devices, CancellationToken ct)
    {
        var rows = new ConcurrentBag<FirmwareRow>();
        try
        {
            await Parallel.ForEachAsync(devices, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (device, token) =>
            {
                if (InGroup(scope, device.Id)) // a device that left the group meanwhile is not read
                {
                    rows.Add(await ReadAsync(owner, device, token));
                }
            });
        }
        catch (OperationCanceledException)
        {
            Publish(scope, StateOf(scope) with { Reading = false }); // the job ended early: keep the old table
            return;
        }

        Publish(scope, StateOf(scope) with { Reading = false, Rows = [.. rows.OrderBy(r => r.Address, StringComparer.OrdinalIgnoreCase)] });
    }

    private static async Task<FirmwareRow> ReadAsync(ICallContext owner, IDeviceInfo device, CancellationToken ct)
    {
        var model = device.Model ?? "";
        if (DeviceMessages.Refusal(device, refuseUnreachable: true) is { } refusal)
        {
            return new FirmwareRow(device.Id, device.Address, model, null, refusal);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var vapix = await owner.Vapix.CreateAsync(device.Id, timeout.Token);
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

    /// <summary>Whether the device is in the group now (a few lookups, no request): a tag or site change can move it out.</summary>
    private bool InGroup(string scope, Guid deviceId) => _ctx!.DeviceScopes!.Contains(scope, deviceId);

    /// <summary>The state with only the rows of devices the group has now.</summary>
    private FirmwareState InGroup(string scope, FirmwareState state) =>
        state with { Rows = [.. state.Rows.Where(r => InGroup(scope, r.DeviceId))] };

    /// <summary>
    /// A group's devices changed or the group was deleted: its table may show devices it no longer has. A read that runs
    /// keeps running (and the page keeps showing it), so only the rows go.
    /// </summary>
    private void OnGroupChanged(object? sender, string scope) => Publish(scope, StateOf(scope) with { Rows = [] });

    private FirmwareState StateOf(string scope)
    {
        lock (_gate)
        {
            return _states.GetValueOrDefault(scope) ?? new FirmwareState(_version, false, []);
        }
    }

    private bool TryBeginRead(string scope)
    {
        lock (_gate)
        {
            var state = StateOf(scope);
            if (state.Reading)
            {
                return false;
            }

            Publish(scope, state with { Reading = true });
            return true;
        }
    }

    /// <summary>Stores the new state with the next version and sends it to the pages of the group (only its devices).</summary>
    private void Publish(string scope, FirmwareState state)
    {
        string json;
        lock (_gate)
        {
            state = InGroup(scope, state) with { Version = ++_version };
            _states[scope] = state;
            json = Serialize(state);
        }

        _ctx?.Events?.Publish(FirmwareOverview.StateTopic, json, PluginEventAudience.DeviceScope(scope));
    }

    private static string Serialize(FirmwareState state) => JsonSerializer.Serialize(state, FirmwareOverview.Json);
}
