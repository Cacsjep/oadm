---
title: Devices and VAPIX
description: How a plugin finds devices, reads them and talks to them.
---

Every plugin works on the devices OADM manages. The server keeps the device table and the stored passwords. A plugin
sees each device as a read-only `IDeviceInfo` and talks to it through an `IVapixClient` that is already logged in.

## The device: `IDeviceInfo`

| Member | Content |
|---|---|
| `Id` | OADM's id of the device (`Guid`). Use it for every call and as the key of your own data |
| `Serial` | The MAC address, upper case without separators (`B8A44F631339`) |
| `Address`, `HostName` | The address OADM reaches it at (IP or DNS name) and its host name |
| `Model`, `FirmwareVersion` | Product number (`P3265-V`) and AXIS OS version (`12.11.77`) |
| `Status` | `Ok`, `Unreachable`, `CredentialsRequired`, `PasswordNotSet`, `CertificateChanged`, `Unknown`, `SiteOffline` (its site node is not connected) |
| `Category`, `HasVideo`, `HasLiveVideo` | Camera, encoder, speaker, intercom, I/O module, ...; `HasVideo` for cameras, encoders and intercoms; `HasLiveVideo` adds security radars (live video only) |
| `Apis` | The VAPIX API list of the last full refresh, see [API checks](#api-checks) |
| `Tags` | The tag names the user gave the device, sorted ("Building A", "PTZ"); empty without tags. Compare them without case. Colors are not in the SDK |
| `SiteId`, `SiteName` | The site OADM reaches the device through ("Building B"); null on the server's own network. Two sites may use the same addresses: key devices by `Id` |
| `CredentialUserName` | The account OADM logs in with. Never remove or demote it. Never the password |
| `DhcpEnabled`, `HttpsEnabled`, `Dot1xEnabled` | Network facts from the last full refresh; null while unknown |
| `ConnectsOverHttps` | How OADM connects: false over HTTP, so what you send crosses the network unencrypted; null while unknown |
| `CertNotAfterUtc`, `CertTrustName` | The HTTPS certificate's end date and its rating |

The server polls every device's status, pauses 60 s after each round, and reads everything else in full refresh rounds with a 10 minute pause, after
every task and when a device comes back. `IDeviceInfo` is that cached picture: reading it costs nothing and never
contacts the device.

### Status first

Before a plugin sends anything, it checks the status. OADM never sends requests to a device it cannot log in to:

```csharp
if (DeviceMessages.Refusal(device, refuseUnreachable: true) is { } refusal)
{
    // "Certificate changed ...", "Credentials required - ...", "Site offline: Building B", ...
    return refusal; // show it in the row or fail the task with it; send nothing
}
```

`DeviceMessages` has every text OADM shows for a status or an HTTP error (`StatusText`, `ForHttpStatus`, `TimedOut`,
`Unreachable(detail)`), so your plugin says it the same way as the rest of the app. Never refuse with a switch on
`DeviceStatus` alone: `Refusal(device)` also says "Site offline: Building B" for a device whose site node is not
connected. The host refuses such devices for task runs, queries and actions before it calls your plugin; a task plugin
greys its entry with `TaskSupportReasons.ForStatus(device)`.

## Getting devices

### On the server

| Where | How |
|---|---|
| Task plugin | `ExecuteAsync(ctx, device, payload, ct)` gets its device; `ctx.Vapix` is logged in to it |
| Task query | `QueryAsync(ctx, caller, device, ...)` the same; `caller.Devices` lists the devices of the user who asks |
| Core plugin method | `caller.Devices.ListAsync(ct)` and `caller.Devices.FindAsync(id, ct)`; `caller.Vapix.CreateAsync(id, ct)` for a client. Only the user's devices, see [Device groups](../device-groups/) |
| Core plugin background work | `ctx.Devices` and `ctx.Vapix`: every managed device |
| Action | `ctx.Vapix` for the target device when `NeedsDevice` is true |

A core plugin that reads many devices does it with bounded parallelism, a timeout per device and the status check
first. This background work reads the AXIS OS version of every video device, 16 at a time (in a method, pass
`caller.Devices` and `caller.Vapix` instead):

```csharp
public sealed record FirmwareRow(Guid DeviceId, string Address, string? Version, string? Error);

public static async Task<IReadOnlyList<FirmwareRow>> ReadFirmwareAsync(ICorePluginContext ctx, CancellationToken ct)
{
    var devices = await ctx.Devices.ListAsync(ct);
    var rows = new ConcurrentBag<FirmwareRow>();

    await Parallel.ForEachAsync(
        devices.Where(d => d.HasVideo),
        new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
        async (device, token) =>
        {
            // Never send a request to a device OADM cannot log in to.
            if (DeviceMessages.Refusal(device, refuseUnreachable: true) is { } refusal)
            {
                rows.Add(new FirmwareRow(device.Id, device.Address, null, refusal));
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var vapix = await ctx.Vapix.CreateAsync(device.Id, timeout.Token); // cached by the host: never dispose it
                var info = await vapix.GetBasicDeviceInfoAsync(timeout.Token);
                rows.Add(new FirmwareRow(device.Id, device.Address, info.Version, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                rows.Add(new FirmwareRow(device.Id, device.Address, null,
                    ex is OperationCanceledException ? DeviceMessages.TimedOut : ex.Message));
            }
        });

    return [.. rows];
}
```

For a page that shows one row per device and reads them when the user asks, use the shared `DeviceSweep` and
`SweepRows` in `plugins/Shared/` instead (one sweep at a time, rows pushed in batches, updated in place). The Image
Health Dashboard and Edge storage are built on them.

### In the client

The client never talks to devices, but it knows them all: it keeps a live copy of the device table.

| Where | Devices |
|---|---|
| Task dialog | `ShowAsync(ctx, devices, owner)`: the selected devices; `ctx.AllDevices`: every device |
| Core plugin page | `ctx.Devices` (all), `ctx.SelectedDevices` (the Devices page selection), event `ctx.DevicesChanged` |
| Toolbar plugin | `ctx.Devices`, `ctx.SelectedDevices`, events `DevicesChanged` and `SelectionChanged` |

Never cache the selection: read it when you need it. To let the user pick devices, use the shared
`DevicePickerWindow` (search, tags, Select all, 100,000 devices), see [UI kit](../ui/).

## Talking to a device: `IVapixClient`

| Member | Does |
|---|---|
| `GetBasicDeviceInfoAsync` | `basicdeviceinfo.cgi`: serial, product number, AXIS OS, product type, SoC |
| `ListParametersAsync(groups)` | `param.cgi?action=list`; keys without the `root.` prefix |
| `GetApiListAsync` | A fresh API list (`apidiscovery.cgi`); before AXIS OS 8.50 built from parameters |
| `GetVideoSourcesAsync` | The video channels with their resolutions, as the live view offers them |
| `RestartAsync` | `restart.cgi` |
| `SendAsync(request)` | Any request, relative to the device |
| `ConnectWebSocketAsync(path, keepAlive)` | A WebSocket, logged in with a one-time session token |

The client uses the stored login, scheme and pinned certificate. It answers Digest over HTTP and Basic or Digest over
HTTPS. Your plugin never sees the password.

### Any request with `SendAsync`

```csharp
using var request = new HttpRequestMessage(HttpMethod.Post, "/axis-cgi/io/portmanagement.cgi")
{
    Content = new StringContent("""{"apiVersion":"1.0","method":"getPorts"}""", Encoding.UTF8, "application/json"),
};
using var response = await vapix.SendAsync(request, ct);
if (!response.IsSuccessStatusCode)
{
    throw new InvalidOperationException(
        DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? DeviceMessages.ServerError((int)response.StatusCode));
}

var json = await response.Content.ReadAsStringAsync(ct); // JSON APIs answer errors with HTTP 200: check "error"
```

- **Paths only.** Pass a path relative to the device. A URI with another host, port or scheme, `//host`, or a
  backslash is refused before anything is sent.
- **Values in the body.** Send arguments of writes in the POST body, never in the URL (URLs end up in logs).
- **Timeouts.** 15 s by default. For an upload set `request.Options.Set(VapixRequestOptions.Timeout,
  TimeSpan.FromMinutes(20))` and send the file as `StreamContent`; bodies are streamed, never buffered.
- **Answer size.** At most 16 MB is read; a larger answer throws "The device answer is larger than 16 MB".
- **Large downloads** (server reports, logs, recordings). `request.Options.Set(VapixRequestOptions.StreamResponse, true)`
  returns the answer after its headers, unbuffered. Copy `response.Content.ReadAsStreamAsync(ct)` straight to a file (a
  core plugin: in `ctx.DataDirectory`), stop at your own size limit and bound the read with your own token: the timeout
  covers the headers only. Dispose the response. Through a site such an answer holds one of the site's 2 large transfer
  slots until it is disposed.
- **Streams that run for hours** (an MJPEG stream of pictures). Also set `VapixRequestOptions.LiveStream` (SDK 0.10.0),
  so the stream holds no large transfer slot of a site: AXIS OS upgrades and exports need them. Limit your streams per
  site yourself; Text recognition allows 4.
- **XML.** Parse device XML only with `DeviceXml.Parse` / `DeviceXml.ParseElement` (1 MB, no DTD), never
  `XDocument.Parse`.
- **Errors.** Axis devices often answer errors with HTTP 200 and the error in the body (`# Error`, `"error"`,
  `<GeneralError>`). Check the body.

### API checks

`Apis` lists the VAPIX APIs a device offers, with versions. The rule: a different major version is a different API,
a higher minor version within the same major is compatible.

```csharp
device.Apis.Supports("network-settings", "1.12");          // CanRun: cached list, no request
(await ctx.Vapix.GetApiListAsync(ct)).Require("network-settings", "1.12");
                                                           // before the first write: a fresh list, throws
                                                           // DeviceNotCompatibleException "... Nothing was changed."
device.Apis.FindApi("fwmgr")?.Version;                     // the highest listed version
```

The API list is read in the full refresh. A device that was just added may have none yet: then
`TaskSupportReasons.NeedsApi(...)` tells the user to refresh it.

### WebSockets

```csharp
using var socket = await vapix.ConnectWebSocketAsync("/vapix/ws-data-stream?sources=events", TimeSpan.FromSeconds(15), ct);
```

ws for HTTP devices, wss with the pinned certificate for HTTPS devices. The host logs in with a one-time session token,
so the stored login never reaches your plugin. The socket pings at the interval and aborts when the device stops
answering (a device sends nothing on a quiet stream). Dispose it, and reconnect with a backoff when it breaks. Used by
Device triggers.

### Event streams (RTSP)

```csharp
await using var source = await ctx.EventStreams!.OpenAsync(deviceId, ct);
await foreach (var document in source.ReadAsync(ct))
{
    var xml = DeviceXml.Parse(document.Xml); // one tt:MetadataStream document
}
```

The server opens the device's RTSP event stream with the stored login, which never reaches your plugin, on the
device's RTSP port (554 unless the user set another with **OADM > RTSP port**).

- `ReadAsync` yields one complete `tt:MetadataStream` document per RTP marker, as a `DeviceMetadataDocument` with the
  time the server received it. `LostDocuments` counts documents dropped for packet loss or size (over 1 MB).
- Disposing the source ends the stream on the device. The host sends the keep-alives and notices a silent connection.
- Failures are `DeviceStreamException` with `Error` (`Unreachable`: retry with a backoff; `Unauthorized`,
  `NotSupported`: give up) and a text for the user ("The device has no event stream").
- Check `DeviceMessages.Refusal(device)` first. `ctx.EventStreams` is null on a host without it.

Used by the Metadata Monitor.

### Video

`GetVideoSourcesAsync` returns the channels (view areas, sensors, encoder inputs) as `VideoSource`: camera number,
name, sensor and resolutions. `VideoResolutions.Choose` picks the largest resolution that fits a box with the sensor's
aspect. A page that shows live video uses the host's live video component (`ICorePluginClientContext.LiveVideo`), see
[Core plugins and pages](../core-plugins/#live-video-and-web-interfaces).

### Network checks: `IDeviceNetwork`

Ping, a TCP probe, a DNS lookup or OADM's own address towards a device go through `IDeviceNetwork` on the device's route
(through its site node, or on the server's own network): `ctx.Network` in a task or a task query,
`ctx.Network.ForDevice(device)` in a core plugin. Its methods are `PingAsync`, `ProbeTcpAsync`, `ResolveAsync`,
`LocalAddressTowardsAsync` and `Reaches`. Never open your own sockets, `Ping` or `Dns`: they reach only the server's
network, and the build warns (RS0030). See [Sites](../sites/).

## Changing what OADM knows about a device

A task that changes how OADM reaches the device tells the server, which checks the device first:

| After | Call |
|---|---|
| Changing the password of OADM's own account | `ctx.UpdateCredentialsAsync(user, password, ct)`, then use `ctx.Vapix` again |
| A factory default | `ctx.MarkCredentialsInvalid()` |
| Giving the device a new address | `ctx.CreateClientForAsync(newAddress, ct)` to reach it there, then `ctx.UpdateDeviceAddressAsync(newAddress, ct)` |
| A new HTTPS certificate, or HTTPS off | `ctx.UpdateDeviceTlsAsync("https", fingerprint, ct)` or `("http", null, ct)` |

The server checks the serial number at the new address or with the new certificate. If it does not match, it throws
`DeviceIdentityException` and keeps the record as it was.

- **A new address.** `CreateClientForAsync` gives a client for the device at that address, with the same login, scheme
  and pinned certificate: wait for the device there, compare its serial number and dispose the client. Then
  `UpdateDeviceAddressAsync` moves the record (login and pin kept) and swaps `ctx.Vapix`. It returns false when OADM
  reaches the device by its host name.
- **A new certificate or HTTPS off.** `UpdateDeviceTlsAsync` connects that way and, for HTTPS, checks that the device
  presents exactly that certificate. It then stores scheme and pin, so the device never shows Certificate changed, and
  swaps `ctx.Vapix`. While the device does not answer that way yet it throws `DeviceIdentityException`: retry while the
  web server restarts.
- Another host may throw `NotSupportedException` for both.

## Device safety

A plugin must never break a device or lock OADM out of it. Before you write to devices, read
[Task plugins: device safety](../tasks/#device-safety).

## Reference

Every member with its signature in the [SDK reference](../../api/): [`IDeviceInfo`](../../api/sdk/devices/ideviceinfo/), [`DeviceStatus`](../../api/sdk/devices/devicestatus/), [`DeviceMessages`](../../api/sdk/devices/devicemessages/), [`IDeviceRepository`](../../api/sdk/devices/idevicerepository/), [`IVapixClient`](../../api/sdk/vapix/ivapixclient/), [`IVapixClientFactory`](../../api/sdk/vapix/ivapixclientfactory/), [`DeviceApiExtensions`](../../api/sdk/vapix/deviceapiextensions/), [`VapixRequestOptions`](../../api/sdk/vapix/vapixrequestoptions/), [`DeviceXml`](../../api/sdk/vapix/devicexml/), [`IDeviceEventStreams`](../../api/sdk/devices/ideviceeventstreams/).
