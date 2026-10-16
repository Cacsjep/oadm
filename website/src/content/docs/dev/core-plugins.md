---
title: Core plugins and pages
description: Services on the server with their own page in the navigation rail, toolbar buttons, settings and live events.
---

A core plugin runs inside the server for as long as it runs: an NTP server, a CA, a table of every SD card. It usually
has a page in the navigation rail. The page calls the server part with `InvokeAsync` and gets live updates through
events. A core plugin can also contribute task plugins, actions and event types.

## The server part: `ICorePlugin`

| Member | Purpose |
|---|---|
| `Id`, `DisplayName`, `IconKey` | Identity, the rail entry's name and icon |
| `Group` | The rail group: `Servers`, `Automation`, `Security`, `Monitoring`, `Reporting`, `Maintenance`, `Integrations`, `Utilities`, `Extensions` (default) |
| `HasPage` | False: no rail entry (a toolbar button or only tasks) |
| `TaskPlugins` | Task plugins it contributes; they reach the owner through `ctx.Owner` |
| `StartAsync(ctx, ct)`, `StopAsync(ct)` | When the server starts or stops, and when an administrator turns the plugin on or off |
| `InvokeAsync(method, payloadJson, ct)` | The backend of the page: JSON in, JSON out |
| `Permissions`, `RequiredPermission(method)` | Named rights for the role matrix, see [Permissions](#permissions) |
| `IsAudited(method)` | Write the call to the audit log |

`StartAsync` must return fast. Never wait for a device or the network there: start a background loop instead. A
plugin that throws on start is marked as failed; the others keep running.

### What the server gives it: `ICorePluginContext`

| Member | Use |
|---|---|
| `Devices` | Every managed device (`ListAsync`, `FindAsync`), see [Devices and VAPIX](../devices/) |
| `Vapix` | `CreateAsync(deviceId, ct)`: a logged-in client. The host caches it; never dispose it |
| `Tasks` | `RunAsync(pluginId, deviceIds, payload, owner, ct)` starts tasks, `Cancel(taskId)`, `IsActive(taskId)` (queued or running) |
| `Settings` | `GetAsync` / `SetAsync(key, json)`: the plugin's own settings in the server database |
| `Logger` | The server log |
| `Secrets` | `Protect` / `Unprotect`: encrypt what you store in the settings with the server's key |
| `Events` | `Publish(topic, json)`: live events to every page that watches |
| `DataDirectory` | A private folder for working files, inside the protected data folder |
| `PluginDirectory` | The plugin's folder, for data files you ship. Never use `Assembly.Location` |
| `EventStreams` | RTSP event streams of devices |
| `EventPublisher` | Events for the event rules, see [Event rules](../event-rules/#publish-events) |
| `EventSubscriptions` | Receive every event the server handles, see [Event rules](../event-rules/#subscribe-to-events) |
| `TrustAnchors` | CA certificates the server trusts when it rates device certificates (PKI) |
| `AutoAdd` | Add a device the plugin saw on the network, through the add page's checks (DHCP server) |
| `Firewall` | Open a UDP port in the Windows firewall while the service is on (NTP, DHCP) |

Every member after `Logger` may be null on an older host or where it does not apply. Check before you use it.

### `InvokeAsync` errors

Throw the exception that says what went wrong; its message reaches the page:

| Exception | The page gets |
|---|---|
| `ArgumentException` | INVALID_ARGUMENT: bad input |
| `KeyNotFoundException` | NOT_FOUND: an unknown object |
| `InvalidOperationException` | FAILED_PRECONDITION: a wrong state |
| anything else | INTERNAL |

Keep each answer small. Hand out large results in parts and run long work as a background job that the page watches
through events or polls.

Step by step, with every file: the [plugin page tutorial](../tutorial-page/). Create a working copy with
`./manage.sh new page --name "My page"`.

## The page: `ICorePluginPage`

The client part implements `ICorePluginPage` with the same `PluginId`. `CreateView` returns an Avalonia control. The
host puts it in a card below the page title.

| Member | Purpose |
|---|---|
| `PluginId`, `Title` | The server part's id and the page title |
| `CreateView(ctx)` | The page's view, once per view |
| `HasOwnCards` | True: the view lays out its own cards (`Border.card`), e.g. count cards above a table |
| `ShowTasksPane` | True: the shared tasks pane below the page, for pages that start tasks |

The page's context, `ICorePluginClientContext`:

| Member | Use |
|---|---|
| `InvokeAsync(method, payload, ct)` | Call the server part. Failures are `RpcException`; `ServerErrors.Message(ex)` gives the text |
| `WatchEventsAsync(ct)` | The live events of the server part |
| `OpenDownloadAsync(method, payload, ct)` | A file from the server part, see [Downloads](#downloads) |
| `Devices`, `SelectedDevices`, `DevicesChanged` | The client's copy of the device table and the Devices page selection |
| `Has(permission)`, `PermissionsChanged` | Hide or lock what the user's role does not allow |
| `ShowMessageAsync`, `ConfirmAsync`, `Owner` | The host's message window and the owner for your own dialogs |
| `OpenAsync(hostPage)` | Open a host page, e.g. `HostPages.Devices`, or another plugin's page: `"plugin:" + its id` |
| `OwnerName` | "user@machine", the owner of tasks the page starts |
| `LiveVideo`, `DeviceWeb` | The host's live video and the device web interface, see below |
| `IsPluginOn(packageId)` | Whether another plugin is turned on (null when the host cannot tell), e.g. to say what to turn on first |
| `GetLocalSetting(key)`, `SetLocalSetting(key, value)` | Short texts the page keeps on this computer (at most 64 KB each), e.g. the last device choice |

### Read, then watch

Events can be lost when the connection drops, so a page always works like this: subscribe, read the full state, apply
events (ignoring older ones by a version number in the state), and when the stream ends, wait about two seconds and do it
again. The loop also covers a server restart. The [plugin page tutorial](../tutorial-page/#7-the-page) shows it in full.

### Choose devices first

A page that reads devices never contacts every device on its own. A server can manage 30,000 devices, and the user may
want one building. Put the shared choice button first in the page's toolbar and read only what the user chose:

```csharp
Devices = new PageDeviceChoice(ctx, noun: "cameras", fits: d => d.HasVideo);

// The button: Devices.ButtonText, and on click
if (await Devices.PickAsync(owner, "Choose cameras")) await ReadAsync(ct);

// Nothing chosen yet: read nothing.
if (Devices.IsEmpty) return;
var ids = Devices.Resolve().Select(d => d.Id).ToList();
```

`PageDeviceChoice` starts with the Devices page selection, else the last choice on this computer, and offers All devices,
tags and single devices. For All devices, send "all" to the server instead of every id. A page for one device opens
`DevicePickerWindow.ShowOneAsync`. Work that takes long per device, such as a scan, gets its own start button. Show a
ring or a progress row for every wait longer than a second.

### Several views at once

The rail entry's context menu has **Open in new window**, so `CreateView` can be called several times, each with its own
context. Keep all state in the view model of that view, never in static fields. A server resource that one view
starts (a stream, a job) gets its own id: the start method returns it, events carry it, and the view ignores events of
other ids. Give such resources a lease: the view sends a keep-alive, and the server ends the resource when the
keep-alives stop (a closed client). The Metadata Monitor and the I/O Monitor work like this.

## Settings and secrets

```csharp
// Store one JSON document on Save; read it in StartAsync so the service comes back after a restart.
await ctx.Settings.SetAsync("config", JsonSerializer.Serialize(config), ct);
var json = await ctx.Settings.GetAsync("config", ct);

// A password in the settings: encrypted with the server's key, the purpose binds it to its place.
var stored = ctx.Secrets?.Protect(password, "acme.mail:smtp")
    ?? throw new InvalidOperationException("This server cannot store passwords.");
var plain = ctx.Secrets!.Unprotect(stored, "acme.mail:smtp");
```

Never send a stored password back to the page. Show that one is set, and keep it when the page sends an empty field.

## Tasks from a page

A core plugin can run its own tasks on many devices, like VAPIX Commander's rollouts. Contribute a task plugin with
`ShowInMenus => false` (not in the context menu), start it with `ctx.Tasks.RunAsync`, and set `ShowTasksPane` on the
page so the user watches the tasks below it. Pass `ctx.OwnerName` from the page in the payload so the tasks show who
started them.

```csharp
public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }   // e.g. [new RolloutTask(this)]

var ids = await ctx.Tasks.RunAsync(RolloutTask.PluginId, deviceIds, payloadJson, owner, ct);
```

## Downloads

A core plugin that also implements `ICorePluginDownloads` sends files to its page without storing them on the server:

```csharp
public Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct);
```

Return a `PluginDownload` with the stream, the file name and the length when known. The host checks the permission,
writes the audit entry and copies the stream to the client in 256 KB parts. The page reads it with
`await using var file = await ctx.OpenDownloadAsync(method, payload, ct)` and copies `file.Content` to disk. Edge
Recording Export passes recordings from the camera straight to the client this way.

## Live video and web interfaces

- `ctx.LiveVideo.Create(new LiveVideoPlace { ... })` returns the host's live video component: put its `View` into the
  page and call `Start(deviceId, camera)`. It streams through the server with the stored login, stops while hidden and
  handles fullscreen, quality and zoom and focus. Used by Live View.
- `ctx.DeviceWeb.OpenAsync(deviceId, ct)` opens a device's web interface for an embedded browser through the server,
  logged in, without the password reaching the client. Used by Web View.

## Toolbar buttons

A toolbar plugin adds a control to the Devices toolbar. It lives in a `*.Client.dll`:

```csharp
public sealed class SampleToolbarPlugin : IToolbarPlugin
{
    public string Id => "acme.sample.toolbar";
    public int Order => 0;
    public ToolbarGroup Group => ToolbarGroup.Plugins;

    public Control CreateControl(IToolbarContext ctx)
    {
        var button = new ToolbarButton { IconKey = "plugin" };
        void Update()
        {
            button.Text = $"Sample ({ctx.SelectedDevices.Count})";
            button.IsEnabled = ctx.SelectedDevices.Count > 0;
        }

        ctx.SelectionChanged += (_, _) => Update();
        button.Click += async (_, _) => await ctx.RunTaskAsync("acme.sample.task", CancellationToken.None);
        Update();
        return button;
    }
}
```

`IToolbarContext` gives the selection and the devices with their change events, `RunTaskAsync` (with the task's dialog),
`InvokePluginAsync` (a core plugin's `InvokeAsync`), `QueryAsync`, `UploadAsync`, `RefreshDevicesAsync`, the host's
messages and `Owner`. A button with server work behind it pairs with a core plugin that sets `HasPage => false`, like
the System report. The toolbar must stay on one line at the minimum window width: one short label.

## Permissions

Every page gets the permission "show" (see the page, read its state, watch its events) by itself. Declare named rights
for what changes something, and map methods to them:

```csharp
public IReadOnlyList<PermissionDefinition> Permissions { get; } =
[
    new("manage", "Change storage settings", "Set retention and run checks on the SD cards."),
    new("format", "Format and encrypt storage", "Erase SD cards.") { OperatorDefault = false, FullDeviceControl = true },
];

public string? RequiredPermission(string method) => method switch
{
    "getState" or "refresh" => PermissionKeys.Show,
    "change" => "manage",
    "format" => "format",
    _ => null, // falls back to RequiredRole(method)
};
```

The server checks every call; the page asks `ctx.Has(PermissionKeys.Of(PluginId, "format"))` to hide the buttons. A
method without an answer falls back to `RequiredRole(method)`: Operator methods need "show", Admin methods "manage".
An unknown key or an exception means administrators only.

## Look at the built-in plugins

| Plugin | Shows how to |
|---|---|
| Image Health Dashboard, Edge storage | A table read on request, count cards, `DeviceSweep` / `SweepRows` |
| NTP server, DHCP server | A network service, saved settings, a live log, a contributed task, the firewall |
| VAPIX Commander | A three-card page, saved items with encrypted secrets, rollouts as hidden tasks |
| Metadata Monitor, I/O Monitor | One stream per view with a lease, a live list of 10,000 items |
| System report | No page, a toolbar button, a background job, files in the data folder |
| PKI | Secrets, trust anchors, nine contributed tasks, several dialogs |
| Live View, Web View | The host's live video and device web interface, tabs, the device tree |

Each has a `README.md` in its folder with its decisions and details.

## Reference

Every member with its signature in the [SDK reference](../../api/): [`ICorePlugin`](../../api/sdk/plugins/icoreplugin/), [`ICorePluginContext`](../../api/sdk/plugins/icoreplugincontext/), [`IPluginSettings`](../../api/sdk/plugins/ipluginsettings/), [`ISecretProtector`](../../api/sdk/plugins/isecretprotector/), [`IPluginEvents`](../../api/sdk/plugins/ipluginevents/), [`ICorePluginDownloads`](../../api/sdk/plugins/icoreplugindownloads/), [`ICorePluginPage`](../../api/sdk/client/icorepluginpage/), [`ICorePluginClientContext`](../../api/sdk/client/icorepluginclientcontext/), [`IToolbarPlugin`](../../api/sdk/client/itoolbarplugin/), [`IToolbarContext`](../../api/sdk/client/itoolbarcontext/).
