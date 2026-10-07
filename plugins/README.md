# OADM plugins

How to build a task plugin with a server part and an optional client (dialog) part, and a core plugin
with its own page in the navigation rail (see "Core plugins"). The
contracts are in `src/Oadm.Sdk` (server) and `src/Oadm.Sdk.Client` (client); the spec and the
device safety HARD RULE are in `CLAUDE.md` ("Plugin System").

## Project layout

```
plugins/
  Oadm.Plugins.<Name>/              server part
    Oadm.Plugins.<Name>.csproj      AssemblyName Oadm.Plugins.<Name>.Server
    plugin.json                     { "id": "oadm.<name>", "version", "minSdkVersion", "displayName" }
    <Name>TaskPlugin.cs             ITaskPlugin (+ ITaskPluginQuery when the dialog reads state)
  Oadm.Plugins.<Name>.Client/       optional client part (Avalonia)
    Oadm.Plugins.<Name>.Client.csproj   AssemblyName Oadm.Plugins.<Name>.Client
    <Name>TaskDialog.cs             ITaskPluginDialog (PluginId = the server plugin id)
tests/
  Oadm.Plugins.<Name>.Tests/
```

Both projects copy their build output into the same folder `artifacts/plugins/<plugin id>/`
(MSBuild target `OadmDeployPlugin`, see `Oadm.Plugins.Restart.csproj`). From a repository
checkout the server (`PluginPaths.Development`) and the client (`ClientPluginLoader.DefaultRoots`)
both scan `artifacts/plugins/*/`, so `dotnet build` of the plugin is enough to try it with
`dotnet run` of server and client. Installed plugins live in `<datafolder>/plugins/<id>/` or
next to the published exe in `plugins/<id>/`.

The server loads `*.Server.dll`, the client `*.Client.dll`, each in its own load context.
Shared assemblies come from the host and must never be copied into the plugin folder:

```xml
<ProjectReference Include="..\..\src\Oadm.Sdk\Oadm.Sdk.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>
<!-- client part also: Oadm.Sdk.Client the same way, and -->
<PackageReference Include="Avalonia" Version="12.0.4" ExcludeAssets="runtime" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" ExcludeAssets="runtime" />
```

Host-shared on the client: `Oadm.Sdk`, `Oadm.Sdk.Client`, `Avalonia*`, `CommunityToolkit.Mvvm`,
`Microsoft.Extensions.Logging*`, `SkiaSharp`, `HarfBuzzSharp`. The client part may reference
the server project for shared payload types (it then ships in the same folder).

## Server side

- **Name and group.** `DisplayName` is what the context menu and the toolbar show:
  at most `TaskPluginNames.MaxDisplayNameLength` (32) characters and **no trailing "..."**, also not for
  tasks that open a dialog (the host never appends dots and strips them defensively; the server loader
  logs a warning and shortens longer names with an ellipsis). `Group` (default `TaskGroups.General`)
  is the context menu submenu the task appears in: reuse a `TaskGroups` constant (`Applications`,
  `General`, `Maintenance`, `Network`, `Security`, `Users`, `Video`) when one fits, or name a new group
  (it gets its own submenu, sorted by name; groups always show as submenus, even with one task).
- **Task name (rule): the task list says exactly what the task does**, never the menu name.
  Implement `string GetTaskName(string? payloadJson)` (default interface implementation returns
  `DisplayName`): "Add user joe", "Set static IP 10.0.0.60", "Upgrade firmware to 12.11.77 (factory
  default)", "Install AXIS Video Motion Detection 4.5.2", "Restart device". At most
  `TaskPluginNames.MaxTaskNameLength` (48) characters (the engine shortens longer names with "…" and logs
  a warning; a throwing or empty name falls back to `DisplayName`). Called once per Run, so all devices of
  a run share the name; put what the name needs (friendly app name, upgrade/downgrade) into the payload.
  **Never secrets** (passwords) in names: they are persisted and shown to everyone. Bundled names:
  Users "Add user joe" / "Change password joe" / "Change role joe" / "Change user joe" / "Remove user joe"
  / "Remove users joe, ann"; Firmware "Upgrade firmware to X" / "Downgrade firmware to X" / "Install
  firmware X" (mixed) / "Install firmware" (version unknown) + " (factory default)"; ACAP "Install <app>
  <version>" / "Upgrade <app> to <version>" / "Remove|Start|Stop <app>"; Restart "Restart device"; VAPIX
  Commander rollout: the command name or "<first command> +N more"; Network: see its README.
- `CanRun(device)`: cheap, synchronous; check `device.Apis.Supports(apiId, minVersion)`.
- `ExecuteAsync(ctx, device, payloadJson, ct)` runs once per device; every device is its own task.
  Re-check `(await ctx.Vapix.GetApiListAsync(ct)).Require(...)` before the first write.
- **Steps (rule): every device request and every wait is its own named step**, so the user always
  sees what the task does. `ctx.PlanSteps("Check compatibility", "Upload firmware", ...)` up front,
  then per step `using var step = ctx.BeginStep("Upload firmware")` (or
  `await ctx.StepAsync("Read users", async step => ...)`), `step.ReportProgress(45, "37 of 82 MB")`,
  end with `step.Complete(detail)`, `step.Warn(message)`, `step.Skip(reason)` or `step.Fail(message)`;
  Dispose alone = Done, an exception escaping the step = Failed. Steps that do not apply:
  `ctx.SkipStep(name, "Keep unchanged")`. Planned steps never reached end Skipped. Names are short
  imperatives ("Set DNS", "Wait for the device to come back"); never secrets in names or details.
  When the task succeeds (Done or Done with warnings) the engine appends a final step **Completed**
  (Done), so the tasks pane shows "Step 8/8 · Completed"; failed and cancelled tasks get none (the
  failed step stays current). Plugins never add it themselves.
  Test with `TaskStepList` + `tests/Shared/StepRun.cs` (same end rules as the server, including
  "Completed: Done" at the end of a successful run).
- `ctx.ReportProgress(percent, message)` (optional: progress is derived from the steps),
  `ctx.ReportWarning(message)` (Done with warnings), `ctx.Log(level, message)` (task log shown in
  the task details). Never put secrets in them.
- `ctx.Files.FindAsync / OpenReadAsync(fileId)` for files the dialog uploaded.
- `ctx.UpdateCredentialsAsync(user, password, ct)` after changing the password of the account
  OADM uses (re-read `ctx.Vapix` afterwards); `ctx.MarkCredentialsInvalid()` after a factory
  default. `device.CredentialUserName` is that account: never remove or demote it.
- After re-addressing a device: `ctx.CreateClientForAsync(newAddress, ct)` (same credentials, scheme and
  certificate pin; dispose it) to wait for the device there and compare its serial number, then
  `ctx.UpdateDeviceAddressAsync(newAddress, ct)`: the server verifies the serial again
  (`DeviceIdentityException` otherwise), moves the record (credentials and pin kept, change published,
  full refresh queued) and swaps `ctx.Vapix`; returns false when OADM uses the host name. Third-party
  hosts may throw `NotSupportedException` (SDK default).
- `MaxParallelDevices` limits how many tasks of the plugin run at once (default 8).
- Long requests: `request.Options.Set(VapixRequestOptions.Timeout, TimeSpan.FromMinutes(20))`
  before `ctx.Vapix.SendAsync(request, ct)`; use `StreamContent` for large bodies.
- `ITaskPluginQuery.QueryAsync` serves the dialog (read-only, 30 s timeout on the server).
  `ITaskQueryContext.Devices` (may be null on other hosts) lists all managed devices, e.g. to flag an
  address another managed device has.

## Client side

`ITaskPluginDialog.ShowAsync(ctx, devices, owner)` shows a window and returns the payload JSON
(null = cancelled). `ctx.QueryAsync(deviceId, method, payload, ct)` calls the plugin's query,
`ctx.UploadAsync(path, progress, ct)` uploads a file (progress 0.0 to 1.0) and returns its id for
the payload. Failures are `Grpc.Core.RpcException`; show `Status.Detail` to the user.

Build dialogs from the host look (HARD RULE: reuse controls, no style differences):

- Window and layout like the add devices page: `Width`/`Height` explicit (e.g. 1040 x 700),
  `ExtendClientAreaToDecorationsHint="True"`,
  `ExtendClientAreaTitleBarHeightHint="{DynamicResource Oadm.TitleBarHeight}"`, a root grid of
  title bar, cards with `Margin="16,0"` (`16,16,16,0` for each further card) and the footer.
- Controls from `Oadm.Sdk.Client.Controls` (`xmlns:ui="using:Oadm.Sdk.Client.Controls"`), never
  hand-built copies:

  | Control | Use for |
  |---|---|
  | `ui:DialogTitleBar Text="..."` | first row of every dialog window (title, draggable, caption buttons) |
  | `ui:CardHeader Title Description` | heading of every card (card title, secondary description); child controls go right of the title (e.g. a device picker). Class `flush` when the content below is optional and adds `Margin="{DynamicResource Oadm.GapTop}"` itself |
  | `ui:DialogFooter CancelCommand` | last row: Cancel left; children are the right-hand buttons (`Button.secondary` for Back, one `Button.primary`) |
  | `ui:IconLabel Icon Text` | every icon + text row, e.g. button content |
  | `ui:ToolbarButton Text IconKey (or Icon) IsPrimary` | every toolbar button (Devices page toolbar, button rows in dialogs): `Button.toolbar` (or `Button.primary`) with an `IconLabel` |
  | `ui:ToolbarSeparator` | vertical line between toolbar groups |
  | `ui:SearchBox Text` | every search field |
  | `ui:PasswordBox Text` | **every password field** (a `TextBox` with the bullet mask and an eye button to show / hide the password, tooltip "Show password" / "Hide password"); never a `TextBox` with `PasswordChar` |
  | `MessageWindow.ConfirmAsync(owner, title, message, confirmText)` / `ShowMessageAsync` | **every confirmation or message popup** (the host uses the same window); e.g. the Network dialogs confirm risky changes on Apply / Finish instead of an inline warning with a check box |
  | `ui:StatusChip Text IsOk IsWarning IsError IsAccent` | every status value (border-only chip), in grid cells with `Margin="10,0"` |
  | `ui:FileRow FileName Details Error Command` | a chosen local file with its "Choose file..." button; format sizes with `FileSizeText.Format` |
  | `ui:ProgressRow Value Text IsActive` | upload or scan progress (0 to 100) with its status text |
  | `ui:OadmIcon Data` | a single icon |
  | `ui:CodeView Text Language ContentType IsFormatted` | **every read-only code or response display** (response bodies, headers, JSON/XML previews): monospace, selectable (Ctrl+C and context menu copy), no wrapping with horizontal scroll. `Language` = `Auto` (default: from `ContentType`, then by sniffing the text), `Json`, `Xml` (also SOAP), `KeyValue` (param.cgi `key=value`, `# Error` lines in red) or `Plain`. JSON and XML are pretty-printed with 2 spaces (also when minified); `IsFormatted="False"` shows the exact text (still highlighted), e.g. for a Raw / Pretty switch. Text that does not parse is shown as it is without colors; above `HighlightLimit` (512 K characters) without colors. Colors are the theme brushes `Oadm.Code.KeyBrush`, `StringBrush`, `NumberBrush`, `LiteralBrush`, `PunctuationBrush`, `TagBrush`, `AttributeBrush`, `CommentBrush`, `ErrorBrush`. The pure part `CodeText` (`Detect`, `Prepare`, `FormatJson`, `FormatXml`, `Tokenize`) works without UI. Editable bodies stay a `TextBox Classes="code"` |

- Styles and classes from the host theme (`Themes/OadmTheme.axaml`), e.g. `Border.card`,
  `TextBlock.secondary`, `TextBlock.fieldLabel`, `TextBlock.warning`, `TextBlock.error`,
  `Button.primary`, `Button.secondary`, `Button.toolbar`, `Border.vseparator`, `TextBox.multiline` (plain multi-line
  input, one entry per line, e.g. NTP servers), `Border.tile` (+ class
  `selected`: a picture tile), `Border.liveViewSurface` (dark picture surface), `Button.picture` (a
  clickable picture without button chrome). Form fields are
  label left, input right like the add page editors: `Grid ColumnDefinitions="150,280"` with a
  `TextBlock.fieldLabel`; stack rows in `StackPanel Classes="form"` (or `Grid Classes="form"`),
  which sets the row spacing. Tables are `DataGrid`s (the theme styles them). No local colors,
  font sizes, font weights or paddings.
- Icons are application resources of the client, available to plugin windows at runtime
  (`Icon="{DynamicResource Icon.key}"`; a plugin project cannot resolve them at compile time,
  so use `DynamicResource`). Available keys:

  `Icon.devices`, `Icon.tasks`, `Icon.settings`, `Icon.plugin`, `Icon.add`, `Icon.range`,
  `Icon.remove`, `Icon.refresh`, `Icon.restart`, `Icon.identify`, `Icon.columns`, `Icon.search`,
  `Icon.details`, `Icon.cancel`, `Icon.chevronDown`, `Icon.chevronUp`, `Icon.close`, `Icon.check`,
  `Icon.server`, `Icon.externalLink`, `Icon.key`, `Icon.eye`, `Icon.eyeOff`, `Icon.log`, `Icon.logs`, `Icon.panelOpen`,
  `Icon.panelClose`, `Icon.deleteAll`, `Icon.video`, `Icon.network`, `Icon.firmware`, `Icon.users`,
  `Icon.user`, `Icon.app`, `Icon.upload`, `Icon.file`, `Icon.folder`, `Icon.start`, `Icon.stop`,
  `Icon.snapshot`, `Icon.export`, `Icon.clock`,
  `Icon.device.camera`, `Icon.device.encoder`,
  `Icon.device.speaker`, `Icon.device.audio`, `Icon.device.intercom`, `Icon.device.radar`,
  `Icon.device.io`, `Icon.device.door`, `Icon.device.generic`.

  A plugin's `IconKey` (context menu, toolbar) is the key without the `Icon.` prefix, e.g.
  `restart`. A new icon is added to `src/Oadm.Client/Themes/Icons.axaml` (24x24 Lucide stroke
  geometry) and listed here.

## Toolbar plugins

The buttons on top of the Devices page are toolbar plugins: Scan, Scan IP range, Add manually
(group `Add`), Remove (`Manage`) and one generic plugin that shows a button for every task plugin
with `ShowInToolbar` (`Tasks`) are built into the client and registered exactly like plugin ones.
The Columns button and the search box stay host parts on the right.

```csharp
public interface IToolbarPlugin            // Oadm.Sdk.Client
{
    string Id { get; }                     // unique; built-in ids ("oadm.toolbar.*") are reserved
    int Order { get; }                     // ascending within the group
    ToolbarGroup Group { get; }            // Add, Manage, Tasks, Plugins (left to right)
    Control CreateControl(IToolbarContext ctx);   // any Avalonia control, created once on the UI thread
}

public interface IToolbarContext           // UI thread only; events are raised on the UI thread
{
    IReadOnlyList<IDeviceInfo> SelectedDevices { get; }   event EventHandler? SelectionChanged;
    IReadOnlyList<IDeviceInfo> Devices { get; }           event EventHandler? DevicesChanged;
    IReadOnlyList<ToolbarTaskPlugin> TaskPlugins { get; } event EventHandler? TaskPluginsChanged;
    bool CanRunTask(string pluginId);                     // CanRun for the whole selection
    Task<IReadOnlyList<string>?> RunTaskAsync(string pluginId, CancellationToken ct); // dialog first when needed
    Task OpenAsync(string hostPage);                      // HostPages.AddScan / AddIpRange / AddManually / Devices / Logs / Settings
    Task RemoveDevicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
    Task<string?> QueryAsync(string pluginId, Guid deviceId, string method, string? payloadJson, CancellationToken ct);
    Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct);
    Window? Owner { get; }                                // owner for the plugin's own dialogs
}
```

- Put the class in the plugin's `*.Client.dll` with a public parameterless constructor; the client
  loader finds it next to `ITaskPluginDialog` and `ICorePluginPage`. A duplicate id of a built-in
  entry is ignored, a control that fails to create is logged and left out.
- The host orders entries by group, order and id and draws a `ToolbarSeparator` between groups
  that show something (a hidden control does not count, so an empty group leaves no stray line).
- Build buttons with `ui:ToolbarButton` (one `IsPrimary` button on the toolbar: Scan). Enable and
  disable them from `SelectionChanged` / `TaskPluginsChanged`; never cache the selection.
- Sample: `tests/TestPlugins/Oadm.TestPlugins.Sample.Client/SampleToolbarPlugin.cs` ("Sample (n)"
  with the selection count, runs the sample task), loaded by `ToolbarPluginTests`.

```csharp
public sealed class SampleToolbarPlugin : IToolbarPlugin
{
    public string Id => "oadm.sample.toolbar";
    public int Order => 0;
    public ToolbarGroup Group => ToolbarGroup.Plugins;

    public Control CreateControl(IToolbarContext ctx)
    {
        var button = new ToolbarButton { IconKey = "plugin" };
        void Update() { button.Text = $"Sample ({ctx.SelectedDevices.Count})"; button.IsEnabled = ctx.SelectedDevices.Count > 0; }
        ctx.SelectionChanged += (_, _) => Update();
        button.Click += async (_, _) => await ctx.RunTaskAsync("oadm.sample.standalone", CancellationToken.None);
        Update();
        return button;
    }
}
```

## Core plugins

A core plugin runs inside the server for the server's lifetime and may have a page in the client's
navigation rail. Sample: the Snapshot report plugin (`plugins/Oadm.Plugins.SnapshotReport` + `.Client`,
id `oadm.snapshot-report`, spec in `CLAUDE.md` "Snapshot report plugin").

- Server part: a public class implementing `ICorePlugin` (`Id`, `DisplayName`, `IconKey` = rail icon,
  `TaskPlugins` it contributes, `StartAsync(ctx)`, `StopAsync`, `InvokeAsync(method, payloadJson, ct)`).
  `StartAsync` gets `ICorePluginContext`: `Devices` (all managed devices as `IDeviceInfo`, incl.
  `CertNotAfterUtc` / `CertTrustName`), `Vapix.CreateAsync(deviceId)` (authenticated client with the
  stored credentials; never dispose it), `Tasks`, `Settings` (namespaced key/value), `Logger`.
  A plugin that throws on start is Faulted; the others keep running.
- `InvokeAsync` is the page backend (gRPC `PluginService.Invoke`): route by method name, JSON in and out.
  Throw `ArgumentException` for bad input (INVALID_ARGUMENT), `KeyNotFoundException` for an unknown object
  (NOT_FOUND), `InvalidOperationException` for a wrong state (FAILED_PRECONDITION); the message reaches
  the page. Live updates go the other way through `ctx.Events` (see "Host support for service plugins" below).
  Keep replies below the client's 32 MB message limit: hand out large results in chunks (see
  `readReport`) and run long work as a background job the page polls (see `generateReport` /
  `reportStatus`).
- More context: `ctx.PluginDirectory` (the plugin folder, for data files such as the VAPIX Commander
  `Library/*.json`), `ctx.Secrets` (`ISecretProtector`, encrypt secrets you store in `Settings`; null =
  do not store them), `ctx.Tasks.Cancel(taskId)`. A contributed task plugin that only the page starts
  sets `ShowInMenus => false` and has no public constructor (the loader then never registers it alone).
  Never dispose clients from `Vapix.CreateAsync`: the factory caches them.
- Video sources: `IVapixClient.GetVideoSourcesAsync()` returns the same sources the live view offers
  (`VideoSource` with camera number, name, sensor and resolutions); `VideoResolutions.Choose` picks the
  largest resolution that fits a box with the sensor aspect.
- Client part: a public class implementing `ICorePluginPage` (`PluginId` = the server id, `Title`,
  `CreateView(ctx)`) in the plugin's `*.Client.dll`. The host lists the running core plugins
  (`PluginService.ListCorePlugins`), adds one rail entry per plugin below Devices and shows the view
  inside the page card under the title; `ctx.InvokeAsync(method, payload, ct)` calls the server part.
  Without the client part the page says that it is not installed on this client. A page with several
  panels sets `HasOwnCards => true` and lays out its own `Border.card`s. The context also offers the
  managed devices and the Devices page selection (`Devices`, `SelectedDevices`, `DevicesChanged`),
  `OwnerName`, `ConfirmAsync`, `ShowMessageAsync`, `OpenAsync(HostPages.Devices)` and `Owner`.
- Second sample: the VAPIX Commander (`plugins/Oadm.Plugins.VapixCommander` + `.Client`, id
  `oadm.vapix-commander`, spec in `CLAUDE.md` "VAPIX Commander"): command library from data files, saved
  commands with encrypted secrets, a three-card page and rollouts as a hidden contributed task plugin
  with one named step per command.
- Third sample: the NTP server (`plugins/Oadm.Plugins.NtpServer` + `.Client`, id `oadm.ntp-server`, spec in
  `CLAUDE.md` "NTP server plugin" and `docs/specs/ntp-server.md`): a network service in a core plugin (UDP responder,
  background upstream loop), persisted settings, live request log on the page, a contributed task that configures
  devices. The DHCP server plugin follows the same pattern.

### Host support for service plugins (NTP, DHCP, ...)

- **Persisted settings**: `ctx.Settings.GetAsync/SetAsync(key, json)` (namespaced per plugin, server database). Store
  one JSON document (the NTP server uses key `config`: enabled, interface, upstream) on Save and read it in `StartAsync`,
  so the service comes back after a server restart. Never block `StartAsync` on the network: start background loops.
- **Live events to the page**: `ctx.Events?.Publish(topic, payloadJson)` (SDK `IPluginEvents`, null on hosts without
  it). Fire and forget, never blocks: every watching page has its own queue of 256 events (oldest dropped). Batch
  high-rate sources (the NTP request log publishes the new entries at most every 500 ms) and keep payloads small (max
  1 M characters). On the page: `await foreach (var e in ctx.WatchEventsAsync(ct))` (gRPC `PluginService.Watch`) while
  the view is attached; the sequence ends or throws when the connection drops and is empty on older hosts and in fake
  mode, so always: subscribe, read the full state with `InvokeAsync`, apply events (deduplicate by a sequence number),
  and after the sequence ended wait ~2 s, re-read and watch again (this doubles as polling). Pattern:
  `NtpServerViewModel.RunAsync`.
- **Server network interfaces**: `Oadm.Sdk.Network.SystemNetworkInterfaces.Instance.List()` returns
  `ServerNetworkInterface` (id, name, description, addresses IPv4 first, up, loopback; `PrimaryAddress`) on Windows,
  Linux and macOS. Depend on `IServerNetworkInterfaces` so tests inject fixed interfaces; return the list from a page
  method (it lives on the server, the client machine has other interfaces).
- **Privileged ports**: make the port injectable (tests bind 0 on 127.0.0.1, never the real port) and map bind errors
  per OS: Windows has no privileged ports (AccessDenied = another program holds the port exclusively), Linux needs root
  or `cap_net_bind_service`, macOS root for a single address. See `NtpStatusTexts.ForBindError`.

- Build pages like dialogs: shared controls (`ui:ToolbarButton`, `ui:SearchBox`, `ui:ProgressRow`,
  `ui:StatusChip`, ...), theme classes only, view models without Avalonia platform calls (decode images
  and open windows through a small interface the view implements, so the view model is testable).
- Fake mode: add the plugin's backend to `FakeOadmApi` (`FakeCorePlugins`, `InvokeCorePluginAsync`) so
  `--fake` shows the page.
