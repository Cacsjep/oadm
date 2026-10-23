---
title: Device groups and the call context
description: How a plugin serves users limited to a device group (SDK 0.4.0, OADM 4.2).
---

An administrator can limit a user to a [device group](../../admin/device-groups/): sites, tags and single devices.
Everything that user sees and does follows the group, plugins too. Since SDK 0.4.0 (OADM 4.2) every plugin method gets
the user's call, `ICallContext`, and reaches devices only through it.

A plugin whose `plugin.json` has `minSdkVersion` below 0.4.0 keeps working for users with all devices. For users with a
group the host refuses its methods, downloads and live events ("Only users with all devices can use this plugin.") and
leaves its pages out. See [SDK versions](../compatibility/).

## `caller.*` and `ctx.*`

| | Who | Devices |
|---|---|---|
| `caller.Devices`, `caller.Vapix`, `caller.Tasks`, `caller.EventStreams` | The user of this call | Only the user's devices |
| `ctx.Devices`, `ctx.Vapix`, `ctx.Tasks`, `ctx.EventStreams`, `ctx.AutoAdd` | Background work | Every device |
| `ctx.System` | Background work, or a method with a `SystemAccessReason` | Every device |

- An id outside the user's devices answers like an unknown id: `caller.Devices.FindAsync` returns null, the others throw
  `DevicesNotAvailableException` (NOT_FOUND "1 device is no longer available."). Nothing is sent and a task batch is
  refused whole. Throw `DevicesNotAvailableException` yourself for an id `FindAsync` did not find, before any request.
- "Empty means all" means `caller.Devices.ListAsync`, never every device.
- `caller.UserName`, `caller.Has(permission)`, `caller.HasAllDevices`, `caller.Contains(device)` (a few hash lookups, no
  request), `caller.SessionId` (opaque, for events and jobs), `caller.DeviceScopeKey` ("all" or the group's id).
- **Fail closed.** In the call of a user with a group, `ctx.Devices`, `ctx.Vapix`, `ctx.Tasks`, `ctx.EventStreams`,
  `ctx.AutoAdd` and `ctx.System` throw `DeviceAccessRefusedException` (PERMISSION_DENIED "Only users with all devices
  can do this."). This also holds in a `Task.Run` or timer started from the call. So a method nobody moved to the call
  cannot reach a device outside the group. For users with all devices `ctx.*` works as before.
- Background work (sweeps, servers, timers started in `StartAsync`) never carries a user and keeps the full `ctx.*`.

## Implement the overloads with the call

The host calls the overloads with the call; their defaults forward to the old members. Implement them and keep the old
ones (they may throw).

| Interface | Overload |
|---|---|
| `ICorePlugin` | `InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)` |
| `ICorePluginDownloads` | `OpenDownloadAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)` |
| `ITaskPluginQuery` | `QueryAsync(ITaskQueryContext ctx, ICallContext caller, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)` |
| `ITaskExecutionContext` | `Caller`: the user who started the task; null for tasks the system started |

A task touches only its own device through `ctx.Vapix`. A task or query that reads other devices (a source device) goes
through the call. For an address-in-use check, ask `ITaskQueryContext.ManagedAddressesAsync(candidates)`: it checks
every device on the route and never names one. Name only devices the user sees. A task that sets a static address asks
`ITaskExecutionContext.ManagedAddressesAsync(addresses)` again before its first write and refuses an address in use
(test with `FakeTaskContext.OtherDeviceAddresses`). Both take at most 256 addresses per call.

## Methods for users with all devices

Saves of server-wide services (NTP, DHCP, PKI and EST settings, a shared command library) stay with users who have all
devices:

```csharp
public bool RequiresAllDevices(string method) => method is "saveSettings" or "deleteCommand";
```

The host refuses these for users with a group before your plugin runs, downloads too. Users with a group may still read
the page; filter its rows to `caller`.

A method that must count or check every device without showing any declares why and uses `ctx.System` there:

```csharp
public string? SystemAccessReason(string method) =>
    method == "licenseUsage" ? "counts every device, shows no device data" : null;
```

Keep that list short: the canary prints it.

## Jobs

Work that outlives the call (a report, a scan, an export, a monitor) runs as a job of `ctx.Jobs`:

- `ctx.Jobs.Start(caller, "report", async (job, ct) => ...)` runs the work detached. It reaches devices through
  `job.Owner`, the owner's call, also after the call ended.
- A job ends with its work, on `job.Cancel()`, when the plugin stops, and when the owner's devices, role or session
  change. At most 50 per user and plugin.
- Every method and download that takes a job id looks it up with `ctx.Jobs.Find(caller, id)` and throws the same
  `KeyNotFoundException` for null as for an unknown id. Another user's id then answers NOT_FOUND. Never keep your own id
  table.

## Events

`ctx.Events.Publish(topic, json, audience)` delivers an event only to the page watchers the audience covers:

| `PluginEventAudience` | Who gets it |
|---|---|
| `Device(id)` | Users who see that device |
| `DeviceRows(ids)` | The JSON is an array with one element per id; each watcher gets the elements of its devices, in order |
| `User(name)` | Every session of one user |
| `Session(caller.SessionId)`, `Of(caller)` | The page that started the work |
| `DeviceScope(key)` | Watchers of one group (state kept per group) |
| `AllDevices` | Users with all devices (server-wide state) |

The old `Publish(topic, json)` reaches only users with all devices.

## Objects kept per group

Dashboards, saved views, triggers and sweeps belong to a group:

- Store `caller.DeviceScopeKey` with the object and run it on `ctx.DeviceScopes.Devices(key)`.
- `ctx.DeviceScopes.Exists(key)` is false for a deleted group: its objects are off until an administrator gives them
  another owner. `Changed` tells about changed and deleted groups.
- A table page uses `DeviceSweeps<TRow>` (`plugins/Shared/DeviceSweep.cs` in the OADM repository): `sweeps.For(caller)`
  is the sweep of the caller's group (null for a deleted group); publish its rows with `PluginEventAudience.DeviceScope(key)`.

## A converted method

```csharp
public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
{
    switch (method)
    {
        case "read":
            var request = Json.Read<ReadRequest>(payloadJson);
            var devices = request.DeviceIds.Count == 0
                ? await caller.Devices.ListAsync(ct) // "all" = the user's devices
                : [.. await Task.WhenAll(request.DeviceIds.Select(async id =>
                    await caller.Devices.FindAsync(id, ct) ?? throw new DevicesNotAvailableException()))];
            var job = _ctx.Jobs!.Start(caller, "read", async (job, token) =>
            {
                foreach (var device in devices)
                {
                    var client = await job.Owner.Vapix.CreateAsync(device.Id, token);
                    _ctx.Events!.Publish("row", Json.Write(await ReadRowAsync(client, token)), PluginEventAudience.Of(job.Owner));
                }
            });
            return Json.Write(new { id = job.Id });
        case "status":
            var found = _ctx.Jobs!.Find(caller, Json.Read<JobRef>(payloadJson).Id)
                ?? throw new KeyNotFoundException("The report is no longer available.");
            return Json.Write(new { done = found.Completion.IsCompleted });
        default:
            throw new ArgumentException($"Unknown method {method}.");
    }
}
```

## Test it: the canary

`FakeCoreContext` (`Oadm.Sdk.Testing`) applies the host's rules when you call through it:
`ctx.InvokeAsync(plugin, call, method, payload)`, `ctx.OpenDownloadAsync(...)` and `ctx.QueryAsync(query, call, device,
...)`, with `ctx.CallAsGroup([ids])` or `ctx.CallAsAllDevices()` as the call. `ctx.RefusedAccesses` lists the `ctx.*`
accesses a group call tried, `ctx.EventsSeenBy(call)` the events as that user's page gets them.

`PluginCanary` runs every method of a plugin as a user whose group holds one device, with an id outside the group in
every id field:

```csharp
[Fact]
public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
{
    var ctx = new FakeCoreContext();
    var inside = ctx.Add(new FakeDevice());
    var outside = ctx.Add(new FakeDevice());
    var plugin = new ReportPlugin();
    await plugin.StartAsync(ctx, CancellationToken.None);
    await new PluginCanary(ctx, plugin, inside, outside)
        .Method("read", """{"deviceIds":["{inside}","{device}"]}""")   // {device}: outside, unknown, inside in turn
        .Download("export", """{"deviceId":"{device}"}""")
        .Job("status", """{"id":"{job}"}""", async owner => JobId(await ctx.InvokeAsync(plugin, owner, "read", "{}")))
        .Query(plugin.TaskPlugins.OfType<ITaskPluginQuery>().Single(), "list", """{"sourceId":"{device}"}""")
        .WithoutDevices("settings", "server settings, no device data")
        .AllDevicesOnly("saveSettings")
        .RunAsync();
}
```

With the outside device and with an unknown id each case must answer `DevicesNotAvailableException` with the same text,
create no VAPIX client for the outside device, start no task on it and change no setting or record. With the inside
device it must not be refused. Job cases compare another user's job id with an unknown one. Any use of `ctx.*` in a
group call fails the case and names the access.

## Reference

[`ICallContext`](../../api/sdk/plugins/icallcontext/), [`ISystemAccess`](../../api/sdk/plugins/isystemaccess/),
[`IPluginJobs`](../../api/sdk/plugins/ipluginjobs/), [`IDeviceScopes`](../../api/sdk/plugins/idevicescopes/),
[`PluginEventAudience`](../../api/sdk/plugins/plugineventaudience/), [`DevicesNotAvailableException`](../../api/sdk/devices/devicesnotavailableexception/),
[`DeviceAccessRefusedException`](../../api/sdk/plugins/deviceaccessrefusedexception/).
