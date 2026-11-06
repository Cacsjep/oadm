---
title: Core plugins and pages
description: Services on the server with their own page in the navigation rail, toolbar buttons, settings and live events.
---

A core plugin runs inside the server for as long as it runs: an NTP server, a CA, a table of every SD card. It usually
has a page in the navigation rail. The page calls the server part with `InvokeAsync` and gets live updates through
events. Every call carries the user's call context; a user limited to a [device group](../device-groups/) reaches only
the devices of that group. A core plugin can also contribute task plugins, actions and event types.

## The server part: `ICorePlugin`

| Member | Purpose |
|---|---|
| `Id`, `DisplayName`, `IconKey` | Identity, the rail entry's name and icon |
| `Group` | The rail group: `Servers`, `Automation`, `Security`, `Monitoring`, `Vision` (SDK 0.10.0, vision plugins), `Reporting`, `Maintenance`, `Integrations`, `Utilities`, `Extensions` (default) |
| `HasPage` | False: no rail entry (a toolbar button or only tasks) |
| `TaskPlugins` | Task plugins it contributes; they reach the owner through `ctx.Owner` |
| `StartAsync(ctx, ct)`, `StopAsync(ct)` | When the server starts or stops, and when an administrator turns the plugin on or off |
| `InvokeAsync(caller, method, payloadJson, ct)` | The backend of the page: JSON in, JSON out. `caller` is the user's call with the devices that user may reach, see [Device groups](../device-groups/) |
| `Permissions`, `RequiredPermission(method)`, `RequiredRole(method)` | Named rights for the role matrix, see [Permissions](#permissions) |
| `IsAudited(method)`, `AuditDetail(method, payloadJson)` | Write the call to the audit log, and what the entry says, see [Permissions](#permissions) |
| `RequiresAllDevices(method)` | Only users with all devices may call it (saves of server-wide settings) |
| `SystemAccessReason(method)` | Why the method needs every device during a group user's call; null for most |
| `SelfCheck(pluginDirectory)` | SDK 0.12.0: proves that what the plugin ships works, see [Self check](#self-check) |

`StartAsync` must return fast. Never wait for a device or the network there: start a background loop instead. A
plugin that throws on start is marked as failed; the others keep running.

### What the server gives it: `ICorePluginContext`

| Member | Use |
|---|---|
| `Devices` | Every managed device (`ListAsync`, `FindAsync`), see [Devices and VAPIX](../devices/). Background work only: in a method use `caller.Devices` |
| `Vapix` | `CreateAsync(deviceId, ct)`: a logged-in client. The host caches it; never dispose it. In a method: `caller.Vapix` |
| `Tasks` | Start, cancel and follow tasks, see [Tasks from a page](#tasks-from-a-page). In a method: `caller.Tasks` |
| `Settings` | `GetAsync` / `SetAsync(key, json)`: the plugin's own settings in the server database |
| `Logger` | The server log |
| `Records` | `IPluginRecords`: the plugin's own records in the server database, one row per record, for data that grows with the devices, see [Host services](../host-services/#settings-and-records) |
| `Secrets` | `Protect` / `Unprotect`: encrypt what you store in the settings with the server's key |
| `Events` | `Publish(topic, json, audience)`: live events to the pages the audience covers (`PluginEventAudience`); `Publish(topic, json)` reaches users with all devices |
| `DataDirectory` | A private folder `<data folder>/plugin-data/<id>` for working files (downloads, archives), created on first use. Clean up what you write; never put device data into a shared temp folder |
| `PluginDirectory` | The plugin's folder, for data files you ship. Never use `Assembly.Location` |
| `EventStreams` | RTSP event streams of devices, see [Devices and VAPIX](../devices/#event-streams-rtsp) |
| `StreamProbes` | SDK 0.6.0, `IDeviceStreamProbes`: a device's video sources (`ListSourcesAsync`) and a video stream that is only measured (`OpenAsync`: RTP over UDP or TCP, counters, nothing decoded). Used by the Load test |
| `EventPublisher` | Events for the event rules, see [Event rules](../event-rules/#publish-events) |
| `EventSubscriptions` | Receive every event the server handles, see [Event rules](../event-rules/#subscribe-to-events) |
| `TrustAnchors` | CA certificates the server trusts when it rates device certificates (PKI), see [Host services](../host-services/#trust-anchors) |
| `AutoAdd` | Add a device the plugin saw on the network, through the add page's checks (DHCP server), see [Host services](../host-services/#add-and-follow-devices) |
| `Firewall` | Open a UDP port in the Windows firewall while the service is on (NTP, DHCP), see [Host services](../host-services/#ports-and-the-firewall) |
| `Network` | `IPluginNetwork`: network checks on a device's route (`ForDevice(device)`, `ForSite(siteId)`) and listeners on the server's own network (`ListenUdpAsync`, `ListenTcpAsync`), see [Sites](../sites/) |
| `Sites` | `ISites`: the bound sites and relays that listen at a site (`OpenUdpRelayAsync`, `OpenTcpRelayAsync`), see [Sites](../sites/) |
| `License` | `IPluginLicense`: the package's license, live (`IsValid`, `Has(feature)`, `Limit(name)`, `Changed`), see [Licenses](../licenses/) |
| `Pro` | `IProLicense`: the server's OADM PRO license, live. OADM only checks; a PRO plugin decides what runs without it |
| `Services` | `IPluginServices`: services core plugins offer each other (`Offer`, `Withdraw`, `Find`), e.g. the CA of PKI, see [Host services](../host-services/#services-between-plugins) |
| `System` | `ISystemAccess`: every device, for background work and methods with a `SystemAccessReason` |
| `Jobs` | `IPluginJobs`: work a method starts for its user (reports, scans, monitors), owned by that user, see [Device groups](../device-groups/#jobs) |
| `DeviceScopes` | `IDeviceScopes`: the devices of a device group, for objects kept per group (dashboards, views) |
| `ProtectedTags` | `IProtectedTags`: tags only users with all devices may add to or remove from devices; null on older hosts, then treat no tag as safe. See [Host services](../host-services/#protected-tags) |

Every member after `Logger` except `System` may be null on an older host or where it does not apply. Check before you
use it.

### `InvokeAsync` errors

Throw the exception that says what went wrong; its message reaches the page:

| Exception | The page gets |
|---|---|
| `ArgumentException` | INVALID_ARGUMENT: bad input |
| `KeyNotFoundException` | NOT_FOUND: an unknown object |
| `InvalidOperationException` | FAILED_PRECONDITION: a wrong state |
| anything else | INTERNAL |

Read and write the JSON of your methods with `PluginJson` (`Oadm.Sdk.Plugins`). A missing or broken payload becomes an
`ArgumentException` with a plain text ("The request is empty.", "The request cannot be read."), so the page gets a
refusal and never the reader's own error. Create the options once and keep them in a static field of your plugin, and
use the same ones in the client part. Never share them with the host or another plugin: the serializer remembers your
types in them, and they must go away when the plugin is unloaded.

```csharp
public static readonly JsonSerializerOptions Json = PluginJson.CreateOptions(PluginJsonEnums.CamelCase);

var request = PluginJson.Parse<SaveRequest>(payloadJson, Json);        // the payload must be there
var filter = PluginJson.ParseOrEmpty<ListRequest>(payloadJson, Json);  // no payload reads like {}
var saved = PluginJson.TryParse<Config>(storedText, Json);             // stored text: null when missing or damaged
return PluginJson.Serialize(reply, Json);
```

`CreateOptions(enums, writeNulls)` writes camelCase names, reads names in any case and numbers also from strings, and
leaves null values out unless `writeNulls` is true. `PluginJsonEnums` chooses how enums travel: `Numbers` (default),
`Names` or `CamelCase`.

Keep each answer small: the client reads at most 32 MB per message. Hand out large results in parts and run long work
as a background job (`ctx.Jobs`, see [Device groups](../device-groups/#jobs)) that the page watches through events or
polls. The Snapshot report and the System report work this way.

Step by step, with every file: the [plugin page tutorial](../tutorial-page/). Create a working copy with
`dotnet new oadm-page -n MyPage` (in a checkout of OADM: `./manage.sh new page --name "My page"`).

## The page: `ICorePluginPage`

The client part implements `ICorePluginPage` with the same `PluginId`. `CreateView` returns an Avalonia control. The
host puts it in a card below the page title. On a client without the client part the page says that it is not installed
there.

| Member | Purpose |
|---|---|
| `PluginId`, `Title` | The server part's id and the page title |
| `CreateView(ctx)` | The page's view, once per view |
| `HasOwnCards` | True: the view lays out its own cards (`Border.card`), e.g. count cards above a table |
| `ShowTasksPane` | True: the shared tasks pane below the page, for pages that start tasks |

The page's context, `ICorePluginClientContext`:

| Member | Use |
|---|---|
| `InvokeAsync(method, payload, ct)` | Call the server part. Failures are `RpcException`; `ServerErrors.Message(ex)` gives the text, `GuardedRun.RunAsync` returns it instead of throwing |
| `WatchEventsAsync(ct)` | The live events of the server part. `ctx.RunAsync(read, handle, options, ct)` reads the state and watches them for a page, again after a drop |
| `OpenDownloadAsync(method, payload, ct)` | A file from the server part, see [Downloads](#downloads) |
| `Devices`, `SelectedDevices`, `DevicesChanged` | The client's copy of the device table and the Devices page selection |
| `Has(permission)`, `HasAllDevices`, `PermissionsChanged` | Hide or lock what the user's role does not allow, or what only users with all devices may do |
| `ShowMessageAsync`, `ConfirmAsync`, `Owner` | The host's message window and the owner for your own dialogs |
| `OpenAsync(hostPage)` | Open a host page, e.g. `HostPages.Devices`, or another plugin's page: `"plugin:" + its id` |
| `OwnerName` | "user@machine", the owner of tasks the page starts |
| `LiveVideo`, `DeviceWeb` | The host's live video and the device web interface, see below |
| `IsPluginOn(packageId)` | Whether another plugin is turned on (null when the host cannot tell), e.g. to say what to turn on first |
| `GetLocalSetting(key)`, `SetLocalSetting(key, value)` | Short texts the page keeps on this computer (at most 64 KB each), e.g. the last device choice |
| `OpenDeviceRequested` | The host asks the page to open a device (the argument is its id); Web View opens devices at a site this way |
| `RunTaskAsync(pluginId, deviceIds, ct)`, `RunTaskAsync(pluginId, deviceIds, startView, ct)` | Run a task plugin like the context menu, its dialog first; with `startView` (SDK 0.7.0) the dialog opens on that part (`ITaskDialogContext.StartView`, a name the dialog defines, e.g. "license" for the Applications dialog) |
| `OpenDevicePage(deviceId, path)` | SDK 0.7.0: open a page of a device's web interface in Web View ("/local/myapp/index.html"); false when nothing opens (Web View off or not allowed, unknown device, a path that leaves the device). Web View gets it as `OpenDevicePageRequested` (`DevicePageRequest`) |
| `LicenseOf(packageId)` | The package's license as the server checked it, e.g. to say "No license" on the page; null when the host cannot tell |
| `IsAdministrator`, `StartProTrialAsync(ct)` | Start the 30-day OADM PRO trial from a page that needs PRO (administrators, once per server); offer it while `ProLicenseInfo.TrialAvailable` |
| `Tabs`, `TabsChanged`, `OpenTabRequested` | Pages of other core plugins shown as tabs of this page, see [Pages as tabs](#pages-as-tabs) |

### Pages as tabs

A core plugin page can be a tab of another plugin's page instead of a rail entry: its `ICorePluginPage` returns the other
plugin's id from `TabOf`. The host creates its view as usual, with its own context, and hands it to the other page, whose
context lists it in `Tabs` (plugin id, title, view; only pages the user's role may show) and raises `TabsChanged` when the
list changes. The other page shows a fixed `ui:PageTab` per tab and puts the chosen tab's view into its content.
`OpenAsync("plugin:" + id)` of a tab opens the page on that tab (`OpenTabRequested` with the tab's id); the page's own id
means its first tab. While the other page is not there for the user, the tab page gets its rail entry back. PKI shows the
802.1X and EST servers this way.

### Live events

The server part pushes changes to its pages with `ctx.Events?.Publish(topic, json, audience)`. The audience says who gets
the event (`PluginEventAudience`, see [Device groups](../device-groups/#events)); without one only users with all devices
get it.

- `Publish` never blocks and never throws for a slow or missing page. Every watching page has its own queue of 256
  events; a page that falls behind loses the oldest.
- A payload has at most 1 M characters; a larger event is dropped and logged.
- Batch sources with a high rate: the NTP server publishes its request log at most every 500 ms.

### Read, then watch

Events can be lost when the connection drops, so a page always works like this: subscribe, read the full state, apply
events (ignoring older ones by a version number in the state), and when the stream ends, wait and do it again. The loop
also covers a server restart. A page never writes it itself: `ctx.RunAsync` of `PluginEventWatch` runs it.

```csharp
// A property of the view model, never a static: a test passes a short wait or a fake time.
public PluginEventWatchOptions Watch { get; init; } = new();

public void Start() => _run ??= _ctx.RunAsync(ReadAsync, Apply, Watch, _stop.Token);   // when the view is attached

private async Task ReadAsync(CancellationToken ct) =>
    Show(PluginJson.Parse<State>(await _ctx.InvokeAsync("getState", null, ct), Json));

private void Apply(PluginEvent e)
{
    if (e.Topic == "state")
    {
        Show(PluginJson.Parse<State>(e.PayloadJson, Json));
    }
}
```

- `RunAsync(read, handle, options, ct)` subscribes first, then runs `read`, then hands every event to `handle`. Nothing
  published during the read is lost. Pass null as `read` for a page that only applies events.
- `handle` is an `Action<PluginEvent>`, or a `Func<PluginEvent, Task>` whose event must end before the next one.
- After a failed read, an event that could not be applied or a stream that ended, it waits and starts over. A host
  without events is read at that pace. It never throws and ends with `ct`: cancel it when the view is detached.
- `PluginEventWatchOptions`: `RetryDelay` (2 s), `MaxRetryDelay` (set: the wait doubles up to it and starts again after
  a read that worked; Live View and Dashboards use 1 s to 15 s), `Failed` (the reason, for a page that shows it) and
  `Time` (a `TimeProvider`; tests pass a fake one).
- `ctx.RunAsync(handle, retryDelay, ct)` is the short form without a read.

The [plugin page tutorial](../tutorial-page/#6-the-page) shows a complete page.

### A guarded call

A button's server call must never crash the page. `GuardedRun.RunAsync` runs it and returns null, or the text to show
when it failed (the server's reason, else the exception's message):

```csharp
ErrorText = await GuardedRun.RunAsync(() => _ctx.InvokeAsync("save", PluginJson.Serialize(config, Json), ct));

// A read the page cancels itself: a cancellation of that token is no failure.
ErrorText = await GuardedRun.RunAsync(() => ReadAsync(ct), GuardedCancel.Ignore, ct);
```

`GuardedCancel` says what a cancellation is: `Report` (default, a failure with a text), `Ignore` (no text) or `Throw`
(the `OperationCanceledException` leaves the run). With a token, only a cancellation of that token counts. The page
decides where the text goes: a property, a field's `Validation.SetServerError`, the host's message window. A catch that
does more (several fields, a row's state, a fallback value) stays a `try` in the view model.

### Choose devices first

A page that reads devices never contacts every device on its own. A server can manage 100,000 devices, and the user may
want one building. Put the shared choice button first in the page's toolbar and read only what the user chose:

```csharp
Devices = new PageDeviceChoice(ctx, noun: "cameras", fits: d => d.HasVideo);

// The button: Devices.ButtonText, and on click
if (await Devices.PickAsync(owner, "Choose cameras")) await ReadAsync(ct);

// Nothing chosen yet: read nothing.
if (Devices.IsEmpty) return;
var ids = Devices.Resolve().Select(d => d.Id).ToList();
```

`PageDeviceChoice` starts with the Devices page selection, else the last choice on this computer (kept with
`SetLocalSetting`), and offers All devices, tags and single devices. For All devices, send "all" to the server instead
of every id, so the request does not grow with the device count. A page for one device opens
`DevicePickerWindow.ShowOneAsync`. Work that takes long per device, such as a scan, gets its own start button. Show a
ring or a progress row for every wait longer than a second. Edge storage, the Image Health Dashboard, the Hardening scan,
the Snapshot report and the Action Rule Editor start this way.

### Several views at once

The rail entry's context menu has **Open in new window**, so `CreateView` can be called several times, each with its own
context. Keep all state in the view model of that view, never in static fields. Start server work when the view is
attached and stop it when it is detached. The windows close on logout, with the main window and when the plugin is
turned off. A logout also removes the rail page; the next login gets a new view from `CreateView`, maybe for another
server.

A server resource that one view starts (a stream, a job) gets its own id: the start method returns it, events carry it,
and the view ignores events of other ids. Give such resources a lease: the view sends a keep-alive, and the server ends
the resource when the keep-alives stop (a closed client). The Metadata Monitor and the I/O Monitor work like this.

Build pages like dialogs: shared controls and theme classes only ([UI kit](../ui/)), and view models without Avalonia
platform calls. Decode images and open windows through a small interface the view implements, so the view model is
testable.

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
page so the user watches the tasks below it. Start them through the call, so they run only on the user's devices and
show who started them.

```csharp
public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }   // e.g. [new RolloutTask(this)]

var ids = await caller.Tasks.RunAsync(RolloutTask.PluginId, deviceIds, payloadJson, caller.UserName, ct);
```

- **Page tasks.** Give such a task no public constructor, so the loader never registers it on its own. A task that is
  hidden from the menus and declares no `Permissions` of its own is a page task: only `caller.Tasks.RunAsync` of a page
  method of its own package (or that method's job) starts it, and the method's `RequiredPermission` is the role check.
  Nothing else can start it.
- **Follow a task.** `Cancel(taskId)`; `IsActive(taskId)` is true while it is queued or running, null when the host
  cannot tell. A task cancelled while queued never runs, so ask instead of waiting for it. `AnyActiveAsync(ids)` answers
  for several. `GetOutcomeAsync(taskId, ct)` (SDK 0.7.0) says how an ended task ended: a `TaskOutcome` with its device,
  `TaskEndState` (`Done`, `DoneWithWarnings`, `Failed`, `Cancelled`) and the message of the tasks pane; null while it
  runs or for an unknown task.
- **In a user's call** ask `caller.Tasks`. For a user with a device group, `IsActive` answers only for the user's own
  tasks (another user's active task: null), `AnyActiveAsync` and `GetOutcomeAsync` for the tasks of the user's devices
  whoever started them.
- **The owner.** Tasks started through a call belong to that user; the `owner` argument is ignored there. They check the
  user's devices again when they leave the queue.
- **Ids in a payload.** Bind the ids a task payload carries (a run, a rollout) to `ITaskExecutionContext.Caller` in the
  task: a task can also be started with a payload your page never wrote.
- **A file for the tasks.** `ICorePluginClientContext.UploadAsync(localPath, progress, ct)` (SDK 0.7.0) uploads a local
  file like a task dialog does. Put the returned id into the payload; the task reads it with `ctx.Files`.

## Downloads

A core plugin that also implements `ICorePluginDownloads` sends files to its page without storing them on the server:

```csharp
public Task<PluginDownload> OpenDownloadAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct);
```

Return a `PluginDownload` with the stream, the file name and the length when known. The host checks
`RequiredPermission(method)` like a call, copies the stream to the client in 256 KB parts and disposes the download at
the end or when the client goes away. The page reads it with
`await using var file = await ctx.OpenDownloadAsync(method, payload, ct)` and copies `file.Content` to disk with its own
token; disposing the file ends the call.

- **Errors** travel like those of `InvokeAsync`: `ArgumentException` is bad input, `InvalidOperationException` a wrong
  state, each with its message.
- **Audit.** When `IsAudited(method)` is true the host writes the entry when the download opens, with
  `PluginDownload.AuditDetail` as its detail (null: the method name; never a secret), and a second entry with
  " (failed)" or " (cancelled)" when the file did not arrive whole.
- **A device answer.** Open the request with `VapixRequestOptions.StreamResponse` and pass the response as the
  download's `owner`: it is disposed after the stream. Edge Recording Export passes recordings from the camera straight
  to the client this way.

## Live video and web interfaces

Both are null on older hosts.

**Live video.** `ctx.LiveVideo.Create(new LiveVideoPlace { ... })` returns the host's live video component, an
`ILiveVideo`:

- Put its `View` into the page. `Start(deviceId, camera)` shows a channel of a device (1 is the first channel; 0 also
  shows the first), `Close()` stops the stream and clears the picture, `CameraChosen` reports the channel buttons.
- `LiveVideoPlace` turns parts on or off: the channel buttons (`ShowSourceSwitch`), fullscreen (`AllowFullscreen`), the
  quality switch (`AllowQualitySwitch`), the controls button for zoom, focus and PTZ (`ShowControlsButton`) and whether
  the controls start open (`ControlsOpen`, `FullscreenControlsOpen`).
- The server streams with the stored login; the role needs "Live view". The video stops by itself while it is hidden.
  Dispose it when its place goes away; the host disposes the rest when the page closes. Used by Live View.

**Device web interfaces.** `ctx.DeviceWeb.OpenAsync(deviceId, ct)` opens the web interface of a managed device for an
embedded browser (`Avalonia.Controls.WebView`, which the client has: reference it with `ExcludeAssets="runtime"`):

```csharp
using var session = await ctx.DeviceWeb!.OpenAsync(deviceId, ct);   // DeviceWebRefusedException: the status text
var start = session.StartAddress("/");                              // open it once; later paths under session.Origin

// When the page asks for another address: one that leaves the device goes to the system browser.
if (EmbeddedBrowserLinks.Leaves(address, session.Origin))
{
    // cancel the navigation
}
```

- The server writes "Opened web interface" to the audit log, adds the stored login to every request and WebSocket and
  checks the pinned certificate. The password never reaches the client.
- Each session is a relay on its own loopback address, so the login cookies of two devices never mix between tabs.
  `StartAddress(path)` hands the browser the session cookie once; other programs get 403.
- `EmbeddedBrowserLinks.Leaves(address, origin)` (SDK 0.12.0) is true for a web address of another scheme, host or port
  than the origin, and for mail links.
- Dispose the session when its tab closes; the host stops every relay of a page when the page closes. Used by Web View.

Tabs are `ui:PageTab`: `Count` above 0 shows a badge after the title; a tab without `CloseCommand` is fixed (no X).

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
            button.Text = $"Sample ({ctx.SelectedCount})";   // the count without a copy of the list
            button.IsEnabled = ctx.SelectedCount > 0;
        }

        ctx.SelectionChanged += (_, _) => Update();
        button.Click += async (_, _) => await ctx.RunTaskAsync("acme.sample.task", CancellationToken.None);
        Update();
        return button;
    }
}
```

| `IToolbarPlugin` | Purpose |
|---|---|
| `Id` | Unique; ids starting with `oadm.toolbar.` are the built-in entries. An id that duplicates one is ignored |
| `Group`, `Order` | `ToolbarGroup.Add`, `Manage`, `Tasks`, `Plugins` (left to right), then ascending by `Order`, then by id |
| `CreateControl(ctx)` | Any Avalonia control, created once on the UI thread. One that fails to create is logged and left out |
| `RequiredPermission` | A permission key (`"devices.addRemove"`): the entry hides while the role lacks it; null = every user |

| `IToolbarContext` | Use |
|---|---|
| `SelectedDevices`, `SelectedCount`, `SelectionChanged` | The Devices page selection. `SelectedDevices` is a new list per call; enable buttons with `SelectedCount` |
| `Devices`, `DevicesChanged` | Every device of the client |
| `TaskPlugins`, `TaskPluginsChanged` | The task plugins the user can run (`ToolbarTaskPlugin`) |
| `CanRunTask(pluginId)`, `CannotRunTaskReason(pluginId)` | Whether a task can run on the whole selection, and the reason as the context menu shows it |
| `RunTaskAsync(pluginId, ct)` | Run a task on the selection, its dialog first; returns the task ids, null when the user cancelled |
| `InvokePluginAsync(pluginId, method, payload, ct)` | A core plugin's `InvokeAsync`. Failures are `RpcException`; show `Status.Detail` |
| `QueryAsync`, `UploadAsync` | A task plugin's query and a file upload, like a task dialog |
| `RemoveDevicesAsync(ids, ct)`, `RefreshDevicesAsync(ids, ct)` | Remove devices; read them again now |
| `OpenAsync(hostPage)` | A page of the host: a `HostPages` constant (`Devices`, `AddScan`, `AddIpRange`, `AddManually`, `AddImport`, `AddImportAdm`, `ExportDevices`, `Compliance`, `Sites`, `EventRules`, `Logs`, `Settings`, `Users`, `Credentials`, `Plugins`, `About`) |
| `Has(permission)`, `HasAllDevices`, `PermissionsChanged` | What the user's role allows |
| `ShowMessageAsync`, `ConfirmAsync`, `Owner` | The host's message window and the owner for your own dialogs |

Everything runs on the UI thread. Never cache the selection: enable and disable the button from `SelectionChanged` and
`TaskPluginsChanged`.

- The host puts a separator between groups that show something; a hidden control does not count.
- Build the button with `ui:ToolbarButton`. The toolbar has one main button (Add); yours is a plain one.
- The whole toolbar fits on one line at a window width of 1800 px with the rail expanded; on a smaller window the
  entries that do not fit move into the More menu. Use one short label.
- A button with server work behind it pairs with a core plugin that sets `HasPage => false` (no rail entry) and calls
  it with `InvokePluginAsync`. Long work runs as a job the button's dialog polls; large results come back in parts. The
  System report works this way: a save dialog first, then a progress dialog. Its platform parts sit behind a small
  interface, so the flow is testable without windows.

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

The server checks every call; the client only hides what the role lacks. A method without an answer falls back to
`RequiredRole(method)`: Operator methods need "show", Admin methods "manage". A plugin that declares nothing but answers
`RequiredRole` gets "manage" by itself. An unknown key or an exception means administrators only.

- **A permission** is a `PermissionDefinition(key, name, description)` with `OperatorDefault` (default true),
  `ViewerDefault` and `FullDeviceControl` (a right that acts with the stored device login). Declare "show" only to
  change its defaults (Web View: off for Operator).
- **Keys** are letters and digits, start with a letter, have at most 32 characters and are unique within the plugin.
  They are stored in roles: never rename one. A new permission reaches the built-in Operator and Viewer by its defaults;
  custom roles get it only when an administrator ticks it.
- **In the client** ask `ctx.Has(PermissionKeys.Of(PluginId, "format"))` and follow `PermissionsChanged`. Hide actions
  the role lacks and lock inputs that only lead to them (the NTP server page: inputs read-only, no Save). Task dialogs
  use `PermissionKeys.Task(taskId, key)`, toolbar entries `IToolbarContext.Has` and `IToolbarPlugin.RequiredPermission`;
  the host's own keys are `HostPermissionKeys`.
- **Audit.** `IsAudited(method)` (default: the Admin methods) writes a "Plugin action" entry when the call ends. Its
  detail is the method, or the text of `AuditDetail(method, payloadJson)` (SDK 0.10.0): "Pressed Open gate on Gate
  house", never a secret. A call that threw gets " (failed)" after the detail, one the client gave up " (cancelled)".
  `AuditDetail` is read in both cases too, so it must not rely on the call having worked.

## Self check

`ICorePlugin.SelfCheck(pluginDirectory)` (SDK 0.12.0) proves that what a plugin ships really works on this computer,
such as a native library or a model. It returns null when all is well, else the problem in plain words:

```csharp
public string? SelfCheck(string pluginDirectory) =>
    File.Exists(Path.Combine(pluginDirectory, "models", "reader.onnx")) ? null : "The reader model is missing.";
```

The published server runs every plugin's check with `--self-check` and exits; OADM's release does so on every platform.
No device, no network, no change.

## Look at the built-in plugins

| Plugin | Shows how to |
|---|---|
| Image Health Dashboard, Edge storage | A table read on request, count cards, `DeviceSweep` / `SweepRows` |
| Snapshot report | A page with a background job the page polls, results read in parts |
| NTP server | A network service (a UDP responder, a background loop), saved settings, a live request log, a contributed task that configures devices, the firewall |
| DHCP server | A socket layer the tests replace with an in-memory network, a lease table, live lease changes with versions, a virtualized grid and a dialog |
| VAPIX Commander | A command library from data files, a three-card page, saved commands with encrypted secrets, rollouts as hidden tasks with one named step per command |
| Metadata Monitor, I/O Monitor | One stream per view with a lease, XML parsed on the server, batched messages, a live list of the newest 10,000 items |
| System report | No page, a toolbar button, background jobs with status changes by version, files in the data folder, streamed device downloads, a ZIP read in parts |
| Hardening scan | A read-only scan over thousands of devices with bounded parallelism, results as events and kept in the settings, grid columns built from a catalog |
| PKI | Secrets, trust anchors, several core plugins in one package with pages as tabs, nine contributed tasks that share the CAs, OS tools behind an interface the tests fake |
| Live View, Web View | The host's live video and device web interface, tabs, the device tree |

Each has a `README.md` in its folder with its decisions and details.

## Reference

Every member with its signature in the [SDK reference](../../api/): [`ICorePlugin`](../../api/sdk/plugins/icoreplugin/), [`ICorePluginContext`](../../api/sdk/plugins/icoreplugincontext/), [`ICallContext`](../../api/sdk/plugins/icallcontext/), [`IPluginSettings`](../../api/sdk/plugins/ipluginsettings/), [`ISecretProtector`](../../api/sdk/plugins/isecretprotector/), [`IPluginEvents`](../../api/sdk/plugins/ipluginevents/), [`ICorePluginDownloads`](../../api/sdk/plugins/icoreplugindownloads/), [`ICorePluginPage`](../../api/sdk/client/icorepluginpage/), [`ICorePluginClientContext`](../../api/sdk/client/icorepluginclientcontext/), [`IToolbarPlugin`](../../api/sdk/client/itoolbarplugin/), [`IToolbarContext`](../../api/sdk/client/itoolbarcontext/).
