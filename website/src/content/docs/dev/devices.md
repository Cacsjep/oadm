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
| `Status` | `Ok`, `Unreachable`, `CredentialsRequired`, `PasswordNotSet`, `CertificateChanged`, `Unknown` |
| `Category`, `HasVideo` | Camera, encoder, speaker, intercom, I/O module, ...; `HasVideo` for cameras, encoders and intercoms |
| `Apis` | The VAPIX API list of the last full refresh, see [API checks](#api-checks) |
| `Tags` | The tags the user gave the device ("Building A") |
| `CredentialUserName` | The account OADM logs in with. Never remove or demote it. Never the password |
| `DhcpEnabled`, `HttpsEnabled`, `Dot1xEnabled` | Network facts from the last full refresh; null while unknown |
| `CertNotAfterUtc`, `CertTrustName` | The HTTPS certificate's end date and its rating |

The server polls every device's status every 60 s and reads everything else in a full refresh every 10 minutes, after
every task and when a device comes back. `IDeviceInfo` is that cached picture: reading it costs nothing and never
contacts the device.

### Status first

Before a plugin sends anything, it checks the status. OADM never sends requests to a device it cannot log in to:

```csharp
if (DeviceMessages.Refusal(device.Status, refuseUnreachable: true) is { } refusal)
{
    // "Certificate changed. Remove the device and add it again ...", "Credentials required - ...", ...
    return refusal; // show it in the row or fail the task with it; send nothing
}
```

`DeviceMessages` has every text OADM shows for a status or an HTTP error (`StatusText`, `ForHttpStatus`, `TimedOut`,
`Unreachable(detail)`), so your plugin says it the same way as the rest of the app.

## Getting devices

### On the server

| Where | How |
|---|---|
| Task plugin | `ExecuteAsync(ctx, device, payload, ct)` gets its device; `ctx.Vapix` is logged in to it |
| Task query | `QueryAsync(ctx, device, ...)` the same; `ctx.Devices` lists all managed devices |
| Core plugin | `ctx.Devices.ListAsync(ct)` and `ctx.Devices.FindAsync(id, ct)`; `ctx.Vapix.CreateAsync(id, ct)` for a client |
| Action | `ctx.Vapix` for the target device when `NeedsDevice` is true |

A core plugin that reads many devices does it with bounded parallelism, a timeout per device and the status check
first. This reads the AXIS OS version of every video device, 16 at a time:

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
            if (DeviceMessages.Refusal(device.Status, refuseUnreachable: true) is { } refusal)
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
`DevicePickerWindow` (search, tags, Select all, 30,000 devices), see [UI kit](../ui/).

## Talking to a device: `IVapixClient`

| Member | Does |
|---|---|
| `GetBasicDeviceInfoAsync` | `basicdeviceinfo.cgi`: serial, product number, AXIS OS, product type, SoC |
| `ListParametersAsync(groups)` | `param.cgi?action=list`; keys without the `root.` prefix |
| `GetApiListAsync` | A fresh API list (`apidiscovery.cgi`); on AXIS OS 8.40 built from parameters |
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
- **Answer size.** At most 16 MB is read. For large downloads set `VapixRequestOptions.StreamResponse` and copy
  `response.Content.ReadAsStreamAsync(ct)` to a file with your own size limit and token.
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

ws for HTTP devices, wss with the pinned certificate for HTTPS devices. The socket pings at the interval and aborts
when the device stops answering. Dispose it, and reconnect with a backoff when it breaks. Used by Device triggers.

### Event streams (RTSP)

```csharp
await using var source = await ctx.EventStreams!.OpenAsync(deviceId, ct);
await foreach (var document in source.ReadAsync(ct))
{
    var xml = DeviceXml.Parse(document.Xml); // one tt:MetadataStream document
}
```

The server opens the RTSP event stream with the stored login and the device's RTSP port. Failures are
`DeviceStreamException` with `Error` (unreachable: retry; unauthorized, not supported: give up). Used by the Metadata
Monitor.

### Video

`GetVideoSourcesAsync` returns the channels (view areas, sensors, encoder inputs) with their resolutions. A page that
shows live video uses the host's live video component (`ICorePluginClientContext.LiveVideo`), see
[Core plugins and pages](../core-plugins/#live-video-and-web-interfaces).

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

## Device safety

A plugin must never break a device or lock OADM out of it. Before you write to devices, read
[Task plugins: device safety](../tasks/#device-safety).

## Reference

Every member with its signature in the [SDK reference](../../api/): [`IDeviceInfo`](../../api/sdk/devices/ideviceinfo/), [`DeviceStatus`](../../api/sdk/devices/devicestatus/), [`DeviceMessages`](../../api/sdk/devices/devicemessages/), [`IDeviceRepository`](../../api/sdk/devices/idevicerepository/), [`IVapixClient`](../../api/sdk/vapix/ivapixclient/), [`IVapixClientFactory`](../../api/sdk/vapix/ivapixclientfactory/), [`DeviceApiExtensions`](../../api/sdk/vapix/deviceapiextensions/), [`VapixRequestOptions`](../../api/sdk/vapix/vapixrequestoptions/), [`DeviceXml`](../../api/sdk/vapix/devicexml/), [`IDeviceEventStreams`](../../api/sdk/devices/ideviceeventstreams/).
