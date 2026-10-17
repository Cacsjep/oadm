# OADM plugins

How to build a task plugin (server part, optional client dialog), a toolbar plugin and a core plugin with its own
rail page. Contracts: `src/Oadm.Sdk` (server) and `src/Oadm.Sdk.Client` (client); every type and member is in the SDK
reference of the website (https://oadm.app/api/, `manage build docs` locally). The spec and the device safety HARD
RULE are in `CLAUDE.md` ("Plugin System").

## Start a plugin

Outside this repository (spec `docs/plugin-sdk.md`, guide on the website: Developers > Develop with the .NET SDK):
`dotnet new install Oadm.Plugin.Templates`, `dotnet new oadm-task|oadm-page|oadm-action -n <Name>`, then
`dotnet oadm dev start` (a portable server with the plugin, restarted after every build), `dotnet oadm deploy --installed`,
`dotnet oadm pack`. The templates are the samples (`samples/`) on the NuGet packages of one release.

In this repository: `./manage.sh new task|page|action --name "My plugin"` (PowerShell `.\manage.ps1 new ...`) copies the
plugin of the matching developer tutorial under your name into `plugins/` and `tests/` and adds it to `Oadm.sln`. It
builds, its tests pass and it runs; then change it. The tutorials on the website explain every file.

## Project layout

```
plugins/
  Oadm.Plugins.<Name>/              server part
    Oadm.Plugins.<Name>.csproj      AssemblyName Oadm.Plugins.<Name>.Server
    plugin.json                     { "id": "oadm.<name>", "version", "minSdkVersion", "displayName",
                                      "description": "One short sentence for the Plugins page.",
                                      "enabledByDefault": true,   (optional; false = off until an admin turns it on)
                                      "alwaysOn": false }         (optional; true = core functionality, cannot be turned off)
    <Name>TaskPlugin.cs             ITaskPlugin (+ ITaskPluginQuery when the dialog reads state)
  Oadm.Plugins.<Name>.Client/       optional client part (Avalonia)
    Oadm.Plugins.<Name>.Client.csproj   AssemblyName Oadm.Plugins.<Name>.Client
    <Name>TaskDialog.cs             ITaskPluginDialog (PluginId = the server plugin id)
tests/
  Oadm.Plugins.<Name>.Tests/
```

Both projects copy their output to `artifacts/plugins/<plugin id>/` (MSBuild target `OadmDeployPlugin`, see
`Oadm.Plugins.Restart.csproj`; the samples and the plugins of `manage new` use the plugin project SDK from source,
`src/Oadm.Plugin.Sdk/targets` via their `Directory.Build.props`: plugin.json from `OadmPluginId`, `Version`,
`OadmDisplayName`, `OadmDescription`, `OadmEnabledByDefault`, `OadmLicense*`). In a repository checkout the server (`PluginPaths.Development`) and the client
(`ClientPluginLoader.DefaultRoots`) scan `artifacts/plugins/*/`, so `dotnet build` of the plugin is enough to try it
with `dotnet run` of server and client. Installed plugins live in `<datafolder>/plugins/<id>/` or in `plugins/<id>/`
next to the published exe.

The server loads `*.Server.dll`, the client `*.Client.dll`, each in its own load context. Shared assemblies come from
the host and are never copied into the plugin folder:

```xml
<ProjectReference Include="..\..\src\Oadm.Sdk\Oadm.Sdk.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>
<!-- client part also: Oadm.Sdk.Client the same way, and -->
<PackageReference Include="Avalonia" Version="12.0.4" ExcludeAssets="runtime" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" ExcludeAssets="runtime" />
```

Shared by the client host: `Oadm.Sdk`, `Oadm.Sdk.Client`, `Avalonia*`, `CommunityToolkit.Mvvm`,
`Microsoft.Extensions.Logging*`, `SkiaSharp`, `HarfBuzzSharp`. The client part may reference the server project for
shared payload types; it then ships in the same folder.

## Server side

- **Name and group.** `DisplayName` is the context menu and toolbar name: at most
  `TaskPluginNames.MaxDisplayNameLength` (32) characters, **no trailing "..."**, also for tasks that open a dialog. The
  host never appends dots and strips them; the server loader shortens longer names with "…" and logs a warning.
  `Group` (default `TaskGroups.General`) is the context menu submenu. Reuse a `TaskGroups` constant (`Applications`,
  `General`, `Maintenance`, `Network`, `Security`, `Users`, `Video`) or name a new group. Groups are always submenus,
  sorted by name, even with one task.
- **Task name (rule): the task list says exactly what the task does**, never the menu name. Implement
  `string GetTaskName(string? payloadJson)` (the default returns `DisplayName`): "Add user joe", "Set static IP
  10.0.0.60", "Upgrade firmware to 12.11.77 (factory default)", "Install AXIS Video Motion Detection 4.5.2", "Restart
  device". At most `TaskPluginNames.MaxTaskNameLength` (48) characters; the engine shortens longer names with "…" and
  logs a warning. An empty name or an exception falls back to `DisplayName`. It is called once per Run, so all devices
  of a run share the name: put what the name needs (app name, upgrade or downgrade) into the payload. **No secrets**:
  names are stored and shown to everyone. Bundled names:
  - Users: "Add user joe", "Change password joe", "Change role joe", "Change user joe", "Remove user joe", "Remove
    users joe, ann".
  - Firmware: "Upgrade firmware to X", "Downgrade firmware to X", "Install firmware X" (mixed), "Install firmware"
    (version unknown), each + " (factory default)" when it applies.
  - ACAP: "Install <app> <version>", "Upgrade <app> to <version>", "Remove|Start|Stop <app>".
  - Restart: "Restart device". VAPIX Commander rollout: the command name or "<first command> +N more". Network: see its
    README.
- `CanRun(device)`: cheap and synchronous; check `device.Apis.Supports(apiId, minVersion)`.
- `NotSupportedReason(device)` (optional, default null): why `CanRun` is false. Called only then, on cached data only.
  The context menu always lists the task; when it cannot run on the selection it is greyed out with this text as
  tooltip (the toolbar button too). Say what is missing or what to do, no trailing period: "Needs AXIS OS 11.11 or
  later (this device has 11.9.65)", "Needs the Time API", "The device does not answer". Name a firmware version only
  when your check really is a version check (or you verified the API's first firmware); otherwise name the missing
  function. Details about this one device go last as " (this device ...)": the host drops them when it sums up several
  devices ("Needs AXIS OS 11.11 or later: 3 of 5 selected devices"). Helpers in `TaskSupportReasons`:
  `ForStatus(status, requireOk)` (unreachable, no login, no password, certificate changed),
  `NeedsFirmware("11.11", device)`, `NeedsApi("the network settings API", device)` (says "refresh the device" while
  the API list was not read yet). Null, empty or an exception = "Not supported on this device" (the host logs the
  exception once). Bundled: Restart (status); Users, Applications, Firmware, Date and time, Network settings, Assign IP
  address (status, then the API); "Use OADM as NTP server" (NTP API); PKI tasks (AXIS OS 11.11; 802.1X also the
  network settings API).
- `device.Tags`: the device's tag names, sorted ("Building A", "PTZ"; empty when none), set in the Tags dialog of the
  Devices page. Use them to filter or label, e.g. per building; compare case-insensitive. Colors are not in the SDK.
  Filled on the server and in the client.
- `ExecuteAsync(ctx, device, payloadJson, ct)` runs once per device; every device is its own task. Re-check
  `(await ctx.Vapix.GetApiListAsync(ct)).Require(...)` before the first write.
- **Steps (rule): every device request and every wait is its own named step.** Plan them up front with
  `ctx.PlanSteps("Check compatibility", "Upload firmware", ...)`, then per step
  `using var step = ctx.BeginStep("Upload firmware")` (or `await ctx.StepAsync("Read users", async step => ...)`),
  `step.ReportProgress(45, "37 of 82 MB")`, and end with `step.Complete(detail)`, `step.Warn(message)`,
  `step.Skip(reason)` or `step.Fail(message)`. Dispose alone = Done; an exception escaping the step = Failed. A step
  that does not apply: `ctx.SkipStep(name, "Keep unchanged")`. Planned steps never reached end Skipped. Names are
  short imperatives with the article ("Set DNS", "Wait for the device to come back"); no secrets in names or details.
  When the task succeeds (Done or Done with warnings) the engine appends the step **Completed**; failed and cancelled
  tasks get none (the failed step stays current). Plugins never add it. Test with `TaskStepList` +
  `Oadm.Sdk.Testing.StepRun` (`src/Oadm.Sdk.Testing`: the server's end rules, including "Completed: Done"; also
  `FakeCamera`, `FakeDevice`, `FakeTaskContext`, `FakeActionContext`, `FakeCoreContext`).
- `ctx.ReportProgress(percent, message)` (optional: progress comes from the steps), `ctx.ReportWarning(message)`
  (Done with warnings), `ctx.Log(level, message)` (task log in the task details). No secrets.
- `ctx.Files.FindAsync / OpenReadAsync(fileId)`: files the dialog uploaded.
- `ctx.UpdateCredentialsAsync(user, password, ct)` after changing the password of OADM's account (read `ctx.Vapix`
  again afterwards); `ctx.MarkCredentialsInvalid()` after a factory default. `device.CredentialUserName` is that
  account: never remove or demote it.
- After giving a device a new address: `ctx.CreateClientForAsync(newAddress, ct)` (same credentials, scheme and
  certificate pin; dispose it) to wait for the device there and compare its serial number, then
  `ctx.UpdateDeviceAddressAsync(newAddress, ct)`. The server verifies the serial again (`DeviceIdentityException`
  otherwise), moves the record (credentials and pin kept, change published, full refresh queued) and swaps
  `ctx.Vapix`. Returns false when OADM uses the host name. Other hosts may throw `NotSupportedException` (SDK default).
- After changing the device's web server (new HTTPS certificate, or HTTPS off):
  `ctx.UpdateDeviceTlsAsync("https", newCertificateSha256, ct)` or `ctx.UpdateDeviceTlsAsync("http", null, ct)`. The
  server connects that way, verifies the serial number and (https) that the device presents exactly that certificate,
  then stores scheme, pin and certificate details, so the device never shows CertificateChanged; `ctx.Vapix` is
  swapped. While the device does not answer that way yet it throws `DeviceIdentityException` and the record stays
  unchanged; retry while the web server restarts. Other hosts may throw `NotSupportedException` (SDK default). Used by
  the PKI tasks.
- `MaxParallelDevices` limits how many tasks of the plugin run at once. It can only lower the server setting
  `Tasks.MaxParallelPerPlugin` (default 16, Settings page); the engine uses the smaller value.
- Long requests: `request.Options.Set(VapixRequestOptions.Timeout, TimeSpan.FromMinutes(20))` before
  `ctx.Vapix.SendAsync(request, ct)`; `StreamContent` for large bodies.
- Limits: `SendAsync` reads at most 16 MB of an answer (larger ones throw "The device answer is larger than 16 MB")
  and refuses a URI that leaves the device (another host, port or scheme, `//host/...`, a backslash): always pass
  paths relative to the device. Parse device XML only with `DeviceXml.Parse` / `DeviceXml.ParseElement`
  (`Oadm.Sdk.Vapix`; at most 1 MB, no DTDs), never `XDocument.Parse` / `XElement.Parse`.
- Large read-only downloads (server reports, logs): `request.Options.Set(VapixRequestOptions.StreamResponse, true)`
  returns the answer after its headers, unbuffered. Read `response.Content.ReadAsStreamAsync(ct)` straight into a file
  (core plugins: `ctx.DataDirectory`), stop at your own size limit and bound the read with your own token (the timeout
  covers the headers only). Dispose the response. Sample: `ServerReportDownloader` of the System report.
- `ITaskPluginQuery.QueryAsync` serves the dialog (read-only, 30 s timeout on the server).
  `ITaskQueryContext.Devices` (may be null on other hosts) lists all managed devices, e.g. to flag an address another
  managed device has.

## Client side

`ITaskPluginDialog.ShowAsync(ctx, devices, owner)` shows a window and returns the payload JSON (null = cancelled).
`ctx.QueryAsync(deviceId, method, payload, ct)` calls the plugin's query; `ctx.UploadAsync(path, progress, ct)`
uploads a file (progress 0.0 to 1.0) and returns its id for the payload. Failures are `Grpc.Core.RpcException`; show
`Status.Detail` to the user.

Dialogs use the host look (HARD RULE: reuse controls, no style differences):

- Window and layout like the add devices dialog: explicit `Width`/`Height` (e.g. 1040 x 700),
  `WindowDecorations="BorderOnly"` and `CanResize="False"` (no system title bar; dialogs are modal, fixed size and do not move), a root grid of title bar, cards with
  `Margin="16,0"` (`16,16,16,0` for each further card) and the footer. A dialog with a single form has no card: the form
  sits in `Border.dialogBody`, the window has a fixed width and `SizeToContent="Height"`.
- Controls from `Oadm.Sdk.Client.Controls` (`xmlns:ui="using:Oadm.Sdk.Client.Controls"`), never hand-built copies:

  | Control | Use for |
  |---|---|
  | `ui:DialogTitleBar Text="..."` | first row of every dialog window (the title as a heading, an X that closes the window) |
  | `ui:CardHeader Title Description` | heading of every card; child controls go right of the title (e.g. a device picker). Class `flush` when the content below is optional and adds `Margin="{DynamicResource Oadm.GapTop}"` itself |
  | `ui:DialogFooter CancelCommand` | last row: Cancel left; children are the right-hand buttons (`Button.secondary` for Back, one `Button.primary`) |
  | `ui:IconLabel Icon Text` | every icon + text row, e.g. button content |
  | `ui:ToolbarButton Text IconKey (or Icon) IsPrimary IsIconOnly` | every toolbar button: `Button.toolbar` (or `Button.primary`) with an `IconLabel`; `IsIconOnly` shows only the icon with `Text` as tooltip |
  | `ui:ToolbarSeparator` | vertical line between toolbar groups |
  | `ui:ToolbarOverflowPanel Spacing` | every row of toolbar buttons on a page: entries that do not fit (small or scaled screens) go behind its own More button; keep the device choice button and the search box outside it |
  | `ui:SearchBox Text` | every search field |
  | `ui:FormField Label Hint Error` | **every labeled form row**: label left, level with the input; below the input its validation error, else the `Hint` (small, secondary). Widths from the theme (`Oadm.FormLabelWidth` 180, `Oadm.FormInputWidth` 280; a narrower row shrinks the input). Class `wide`: the input fills the row; class `inline`: the label sizes to its text (fields in one line, e.g. From / To). No label: the label column stays empty, so a check box or button row lines up with the inputs. `Error` only for content without data validation (a group of check boxes, a text value) |
  | `ui:PasswordBox Text` | **every password field**: bullet mask and an eye button ("Show password" / "Hide password"); never a `TextBox` with `PasswordChar` |
  | `MessageWindow.ConfirmAsync(owner, title, message, confirmText)` / `ShowMessageAsync` | **every confirmation or message popup** (the host uses the same window); e.g. the Network dialogs confirm risky changes on Apply / Finish, no inline warning with a check box |
  | `ui:StatusChip Text IsOk IsWarning IsError IsAccent` | every status value (colored icon + text), in grid cells with `Margin="10,0"` |
  | `ui:StatCard Title Value Detail IsOk IsWarning IsError` | monitoring count card (large count, detail line); set the status only while the count needs attention |
  | `DevicePickerWindow.ShowAsync(owner, devices, selected, unavailableReason)` | every "choose devices" dialog: search, Select all / none, count, unchoose; returns the chosen ids or null (Cancel); `unavailableReason` greys devices with the text |
  | `DevicePickerWindow.ShowChoiceAsync(owner, devices, initial, DevicePickerOptions.Full(eventDeviceText))` | the same picker with All devices, tags and (optional) the device of the event; returns a `DeviceChoice` (All, EventDevice, or Chosen with `DeviceIds` and `Tags`: a tag means every device with it) |
  | `ui:FormField Info="..."` / `ui:InfoIcon Text="..."` | the explanation of a field as an info icon right of the input (hover and click), instead of a long hint |
  | `ui:WizardSteps ItemsSource="{Binding Steps}"` | the step row of a wizard (`WizardStepItem`: index, name, current step, can open, open) |
  | `ComboBox Classes="grouped"` + `GroupedChoice.Build(items, group, text)` | a select field with grey group headers and indented choices (bind `SelectedItem` to a `GroupedChoice`, its `Value` is the item) |
  | `ui:FileRow FileName Details Error Command` | a chosen local file with its "Choose file" button; sizes with `FileSizeText.Format` |
  | `ui:ProgressRow Value Text IsActive` | upload or scan progress (0 to 100) with its status text |
  | `ui:LoadingOverlay Text` | hides the dialog content while it reads (ring and text in the middle); bind `IsVisible`, keep the footer free |
  | `ui:OadmIcon Data` | a single icon |
  | `ui:ProgressRing` | a spinning ring in place while a list or card loads |
  | `ui:DeviceIcon Category Status ShowStatusToolTip` | every device icon in a list: the category icon, amber or red by status, the status as tooltip |
  | `DevicePickerWindow.ShowOneAsync(...)` | the picker for one device (checking one unchecks the other); returns its id or null |
  | `PageDeviceChoice` | the device choice of a page that reads many devices (the purple "Selected devices" button), see "Choose devices first" |
  | `ui:TileGrid Source` | a grid of tiles that fills the page (Live View), see "Shared lists and tabs" |
  | `ui:CodeView Text Language ContentType IsFormatted` | **every read-only code or response display** (bodies, headers, JSON/XML previews): monospace, selectable (Ctrl+C and context menu copy), no wrapping, horizontal scroll. `Language`: `Auto` (default: from `ContentType`, then from the text), `Json`, `Xml` (also SOAP), `KeyValue` (param.cgi `key=value`, `# Error` lines red) or `Plain`. JSON and XML are pretty-printed with 2 spaces, also when minified; `IsFormatted="False"` shows the exact text, still highlighted (for a Pretty / Raw switch). Text that does not parse, or is longer than `HighlightLimit` (512 K characters), shows without colors. Colors: theme brushes `Oadm.Code.KeyBrush`, `StringBrush`, `NumberBrush`, `LiteralBrush`, `PunctuationBrush`, `TagBrush`, `AttributeBrush`, `CommentBrush`, `ErrorBrush`. `CodeText` (`Detect`, `Prepare`, `FormatJson`, `FormatXml`, `Tokenize`) works without UI. Editable code is `ui:CodeEditor` |
  | `ui:CodeEditor Text Language PlaceholderText` | **every editable code field** (request bodies): a monospace text box (caret, selection, undo) with the text colored like `ui:CodeView` while typing; `Language` `Json`, `Xml`, `KeyValue` or `Plain`; the text stays exactly as typed |

- Styles and classes from the host theme (`Themes/OadmTheme.axaml`), e.g. `Border.card`, `Border.dialogBody`,
  `TextBlock.secondary`, `TextBlock.fieldLabel`, `TextBlock.warning`, `TextBlock.error`, `Button.primary`,
  `Button.secondary`, `Button.toolbar`, `Button.danger` (red: delete or remove; `Button.toolbar.danger` and
  `Button.link.danger` for flat buttons and row links), `Border.vseparator`, `StackPanel.optionDetail` (the inputs
  of a radio button choice, indented below it), `DataGrid.wrapRows` (rows grow, status chips show two lines), numeric columns
  (`CellStyleClasses="number"` + header `TextBlock.numberHeader`), `ui|SearchBox.stretch` (fills a narrow card),
  `Border.tile` (a picture tile; selection is its check box), `Border.liveViewSurface` (dark picture surface),
  `Button.picture` (a clickable picture without button chrome). Stack `ui:FormField`s in `StackPanel Classes="form"`
  (row spacing); several inputs in one line go in `StackPanel Classes="inputRow"` (top-aligned, so an error below one
  input moves nothing else). Tables are `DataGrid`s. No local colors, font sizes, font weights or paddings.
- The client runs dark or light (Settings > This computer). Use only the theme's `Oadm.*Brush` keys (`StaticResource`,
  or `TryFindResource` in code), never a color value: both appearances define every key.
- Button and menu labels never end with "...", also when they open a dialog or a file picker. All texts follow the
  "Wording" rule in `CLAUDE.md`.

### Validation (HARD RULE: errors directly below the field)

Every validation error appears **directly below its input**: small red text aligned with the input's left edge, red
input border, no space reserved without an error. Never an error list at the bottom of a dialog or a summary line
under a card. Row errors stay in the row (Status column); an error of a whole table (e.g. "Not enough addresses") goes
directly below the table.

- Form view models derive from `Oadm.Sdk.Client.Validation.ValidatingViewModel` (an `ObservableObject` with
  `INotifyDataErrorInfo`) and register one rule per property in the constructor:

  ```csharp
  public sealed partial class MyDialogViewModel : ValidatingViewModel
  {
      public MyDialogViewModel()
      {
          Validation
              .Rule(nameof(UserName), () => UserName.Trim().Length == 0 ? "Enter a user name." : null)
              .Rule(nameof(Confirm), () => Confirm != Password ? "The passwords do not match." : null);
          Validation.Validate();
      }

      [ObservableProperty] public partial string UserName { get; set; } = "";
      ...
  }
  ```

  The theme shows the error of the bound property below a `TextBox`, `ui:PasswordBox`, `NumericUpDown` or `ComboBox`
  (Avalonia `DataValidationErrors`); `ui:FormField` hides its hint meanwhile.
- `Validation` is a `FormValidator`:
  - `Rule(property, () => message or null)`;
  - `Rules(properties, () => dictionary)`: one validator for several properties (e.g. the server's payload validator);
  - `ShowAll(...)` when the user tries to submit, `Reset(...)` after loading or prefilling values;
  - `SetServerError(property, message)`: an answer of the server or device that belongs to a field (shown at once,
    cleared when the field is edited);
  - `IsValidFor(...)` / `FirstErrorOf(...)` for part of a form (e.g. an inline editor).

  A view model that cannot change its base class owns a `FormValidator` and forwards `INotifyDataErrorInfo` to it.
- An error shows once the field was edited, the user left it (`ui:FormField` reports the bound property on lost focus) or a submit was tried, never on an untouched form. Rules always run: submit
  buttons stay disabled while any error exists (`IsFormValid`, or the command's `CanExecute` with
  `Validation.IsValid`), and their tooltip says why (`FormError` or `FirstErrorOf`, with
  `ToolTip.ShowOnDisabled="True"`). Override `OnValidationChanged` to refresh commands.
- Rows that are their own view models (e.g. command fields in a list) derive from `ValidatingViewModel` too; their
  errors show below their inputs in the row template.

### Icons

Icons are application resources of the client, available to plugin windows at runtime. A plugin project cannot
resolve them at compile time, so use `DynamicResource` (`Icon="{DynamicResource Icon.key}"`). Keys:

`Icon.devices`, `Icon.tasks`, `Icon.settings`, `Icon.plugin`, `Icon.add`, `Icon.range`,
`Icon.remove`, `Icon.refresh`, `Icon.restart`, `Icon.identify`, `Icon.columns`, `Icon.search`,
`Icon.details`, `Icon.cancel`, `Icon.chevronDown`, `Icon.chevronLeft`, `Icon.chevronRight`, `Icon.chevronUp`, `Icon.globe`, `Icon.close`, `Icon.check`,
`Icon.tag`, `Icon.groupBy`, `Icon.edit`, `Icon.braces`,
`Icon.server`, `Icon.externalLink`, `Icon.more`, `Icon.key`, `Icon.lock`, `Icon.eye`, `Icon.eyeOff`, `Icon.copy`, `Icon.log`, `Icon.logs`, `Icon.panelOpen`,
`Icon.panelClose`, `Icon.deleteAll`, `Icon.video`, `Icon.grid`, `Icon.gridAdd`, `Icon.hardDriveDownload`, `Icon.hardDrive`, `Icon.wrench`, `Icon.eraser`, `Icon.lockOpen`, `Icon.focus`, `Icon.focusArea`, `Icon.home`, `Icon.network`, `Icon.metronome`, `Icon.firmware`, `Icon.users`,
`Icon.user`, `Icon.logout`, `Icon.audit`, `Icon.app`, `Icon.upload`, `Icon.file`, `Icon.folder`, `Icon.start`, `Icon.stop`,
`Icon.snapshot`, `Icon.export`, `Icon.clock`, `Icon.activity`, `Icon.toggle`, `Icon.zap`, `Icon.workflow`, `Icon.undo`, `Icon.bell`, `Icon.flame`, `Icon.info`, `Icon.shield`, `Icon.clipboardCheck`, `Icon.gauge`, `Icon.circle`, `Icon.thermometer`, `Icon.lightbulb`,
`Icon.device.camera`, `Icon.device.encoder`,
`Icon.device.speaker`, `Icon.device.audio`, `Icon.device.intercom`, `Icon.device.radar`,
`Icon.device.io`, `Icon.device.door`, `Icon.device.generic`.

A plugin's `IconKey` (context menu, toolbar) is the key without `Icon.`, e.g. `restart`. A new icon goes into
`src/Oadm.Client/Themes/Icons.axaml` (24x24 Lucide stroke geometry) and into this list.

## Toolbar plugins

The Devices page toolbar is made of toolbar plugins. The built-in ones are compiled into the client and registered
like plugin ones: **Add** (group `Add`, a menu with Discovery, Network range, Add manually, Import from file), Remove,
Refresh and Export (`Manage`), one generic plugin with a button per task plugin that sets `ShowInToolbar` (`Tasks`),
and AXIS OS - Release Notes (`Plugins`, last). Group by tag, Columns and the search box are host parts on the right.

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
    string? CannotRunTaskReason(string pluginId);         // the reason as the context menu shows it; null = can run or nothing selected
    Task<IReadOnlyList<string>?> RunTaskAsync(string pluginId, CancellationToken ct); // dialog first when needed
    Task OpenAsync(string hostPage);                      // HostPages.AddScan / AddIpRange / AddManually / AddImport / ExportDevices / Devices / Logs / Settings
    Task RemoveDevicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task RefreshDevicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct); // full refresh now (server), read-only
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
    Task<string?> QueryAsync(string pluginId, Guid deviceId, string method, string? payloadJson, CancellationToken ct);
    Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct);
    Task<string?> InvokePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct);
                                                          // a core plugin's InvokeAsync (PluginService.Invoke); DIM: older hosts throw NotSupportedException
    Window? Owner { get; }                                // owner for the plugin's own dialogs
}
```

- Put the class in the plugin's `*.Client.dll` with a public parameterless constructor; the client loader finds it
  like `ITaskPluginDialog` and `ICorePluginPage`. An id that duplicates a built-in entry is ignored; a control that
  fails to create is logged and left out.
- The host orders entries by group, order and id, with a `ToolbarSeparator` between groups that show something (a
  hidden control does not count, so an empty group leaves no stray line).
- Build buttons with `ui:ToolbarButton`; the toolbar has one `IsPrimary` button (Add). Enable and disable them from
  `SelectionChanged` / `TaskPluginsChanged`; never cache the selection.
- Sample: `tests/TestPlugins/Oadm.TestPlugins.Sample.Client/SampleToolbarPlugin.cs` ("Sample (n)" with the selection
  count, runs the sample task), loaded by `ToolbarPluginTests`.
- A toolbar button with its own server part: a core plugin with `HasPage => false` (no rail entry); the button calls
  its `InvokeAsync` through `ctx.InvokePluginAsync(pluginId, method, payload, ct)` (gRPC errors are `RpcException`,
  show `Status.Detail`). Long work runs as a job the button's dialog polls; large results come back in chunks.
  Sample: the System report (`plugins/Oadm.Plugins.SystemReport` + `.Client`, `SystemReportToolbarPlugin`): save
  dialog first, then a progress dialog. The platform parts sit behind a small interface (`ISystemReportUi`), so the
  flow is testable without windows.
- The toolbar must stay on one line at the 1800 px minimum window width with the rail expanded.

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

A core plugin runs inside the server for the server's lifetime and may have a page in the client's navigation rail.

- **Server part**: a public class implementing `ICorePlugin` (`Id`, `DisplayName`, `IconKey` = rail icon,
  `TaskPlugins` it contributes, `StartAsync(ctx)`, `StopAsync`, `InvokeAsync(method, payloadJson, ct)`). `StartAsync`
  gets `ICorePluginContext`: `Devices` (all managed devices as `IDeviceInfo`, incl. `CertNotAfterUtc`,
  `CertTrustName`, `Tags`), `Vapix.CreateAsync(deviceId)` (authenticated client with the stored credentials; the
  factory caches it, never dispose it), `Tasks`, `Settings` (key/value per plugin), `Logger`. A plugin that throws on
  start is Faulted; the others keep running.
- **`InvokeAsync`** is the page backend (gRPC `PluginService.Invoke`): route by method name, JSON in and out. Throw
  `ArgumentException` for bad input (INVALID_ARGUMENT), `KeyNotFoundException` for an unknown object (NOT_FOUND),
  `InvalidOperationException` for a wrong state (FAILED_PRECONDITION); the message reaches the page. Live updates go
  the other way through `ctx.Events` (see "Host support for service plugins"). Keep replies below the client's 32 MB
  message limit: hand out large results in chunks (see `readReport`) and run long work as a background job the page
  polls (see `generateReport` / `reportStatus`).
- **More context**:
  - `ctx.DataDirectory`: a private working folder `<datafolder>/plugin-data/<id>` in the admin-only data folder,
    created on first use, for downloads and archives. Clean up what you write; never use a shared temp folder for
    device data.
  - `ctx.PluginDirectory`: the plugin folder, for data files such as the VAPIX Commander `Library/*.json`.
  - `ctx.EventPublisher` and `ctx.EventSubscriptions`: events for the event rules and every event the server handles,
    see "Publishing events" and "Subscribing to events". `ctx.TrustAnchors`: CA certificates the server trusts when it
    rates device certificates (the PKI). `ctx.Firewall` (`IFirewallRules`, null outside the Windows service): open a UDP
    port in the Windows firewall while the plugin's server is on (NTP, DHCP; keep it in sync with `FirewallRuleKeeper`).
  - `ctx.Secrets` (`ISecretProtector`): encrypt secrets you store in `Settings`; null = do not store them.
  - `ctx.EventStreams`: device event streams, see below. `ctx.Tasks.Cancel(taskId)`; `ctx.Tasks.IsActive(taskId)` (queued
    or running; null when the host cannot tell): a task cancelled while queued never runs, so ask instead of waiting for it.
  - `ctx.AutoAdd` (`IDeviceAutoAdd`, null when the host has none). `AddAsync(ipAddress, expectedSerial, source, ct)`
    adds an Axis device the plugin saw on the network, through the add page's pipeline (anonymous Axis check, factory
    default check, login with the credential list; passwords never reach the plugin). It returns a
    `DeviceAutoAddOutcome` (`Added`, `AddedCredentialsRequired`, `AddedPasswordNotSet`, `AlreadyManaged`, `NotAxis`,
    `SerialMismatch`, `Unreachable` + a plain message). `FollowAsync(serial, ipAddress, source, ct)` moves a managed
    device's record to an address the plugin saw it at, once the server verified it there (`Moved`, `Unchanged`,
    `NotManaged`, `KeptHostName`, `NotVerified`). Call both off your hot path (a queue with a few workers, like the
    DHCP server's `DhcpDeviceAutoAdd`); the server writes the audit entries.
- A contributed task plugin that only the page starts sets `ShowInMenus => false` and has no public constructor, so
  the loader never registers it on its own.
- **Video sources**: `IVapixClient.GetVideoSourcesAsync()` returns the sources the live view offers (`VideoSource`
  with camera number, name, sensor and resolutions); `VideoResolutions.Choose` picks the largest resolution that fits
  a box with the sensor aspect.
- **Client part**: a public class implementing `ICorePluginPage` (`PluginId` = the server id, `Title`,
  `CreateView(ctx)`) in the plugin's `*.Client.dll`. The host lists the running core plugins
  (`PluginService.ListCorePlugins`), adds one rail entry per plugin and shows the view in the page card under the
  title; `ctx.InvokeAsync(method, payload, ct)` calls the server part. Without the client part the page says that it
  is not installed on this client. A page with several panels sets `HasOwnCards => true` and lays out its own
  `Border.card`s. The context also offers the managed devices and the Devices page selection (`Devices`,
  `SelectedDevices`, `DevicesChanged`), `OwnerName`, `ConfirmAsync`, `ShowMessageAsync`, `OpenAsync(HostPages.Devices)`
  (or another plugin's page: `OpenAsync("plugin:" + id)`, while it is on), `IsPluginOn(packageId)` (whether a plugin is
  turned on; null when the host cannot tell) and `Owner`.
- **Several views at once**: the rail entry's context menu **Open in new window** shows another copy of the page in a
  window of its own, so `CreateView` can be called several times, each time with its own context. Keep all page state in
  the view model of that view (never in static or shared fields of the page class), start server work when the view is
  attached and stop it when it is detached (the windows close on logout, with the main window and when the plugin is
  turned off), and keep server-side state per caller: a stream or job the page starts gets its own id (a "start" method
  returns it, events carry it, the page ignores events of other ids), like the Metadata Monitor and the I/O Monitor.
- **Rail group**: `CorePluginGroup Group` (default `Extensions`) puts the page under a group header in the rail, in
  this order: Servers, Automation, Security, Monitoring, Reporting, Maintenance, Integrations, Utilities, Extensions.
- **Without a page**: `HasPage => false` lists the plugin with `CorePluginInfo.no_page` and the client adds no rail
  entry. Its client part is e.g. a toolbar plugin (see "Toolbar plugins").
- Build pages like dialogs: shared controls (`ui:ToolbarButton`, `ui:SearchBox`, `ui:ProgressRow`, `ui:StatusChip`,
  ...), theme classes only, view models without Avalonia platform calls (decode images and open windows through a
  small interface the view implements, so the view model is testable).
- **Fake mode**: add the plugin's backend to `FakeOadmApi` (`FakeCorePlugins`, `InvokeCorePluginAsync`) so `--fake`
  shows the page.

### Licenses (commercial plugins)

A plugin you sell declares `"license": {"publicKey": "...", "features": [...], "trialDays": 30}` in plugin.json (with the
project SDK: `OadmLicensePublicKey`, `OadmLicenseFeatures` "a;b", `OadmLicenseTrialDays`) and reads
`ctx.License` (`IPluginLicense`: `IsValid`, `Has(feature)`, `Limit(name)`, `Current.Reason`, `Changed`) in its core plugin,
task and action contexts; pages use `ICorePluginClientContext.LicenseOf(packageId)`. OADM checks the signed license key
offline; the plugin decides what runs. Vendor keys and license keys: `oadm license keys` / `oadm license sign` (in this
repository `manage license ...`). Guide:
website `dev/licenses.md`.

### Permissions

Administrators give users roles; a role is a set of permissions in the matrix of the Users page. A plugin declares what
an administrator should be able to give or withhold. The server checks every call; the client only hides what the role
lacks.

- **Core plugins**: every page gets "<id>:show" automatically (see the page, read its state, watch its events). Declare
  named rights with `Permissions` (`PermissionDefinition(key, name, description)`, `OperatorDefault` (default true),
  `ViewerDefault`, `FullDeviceControl` for rights that act with the stored device login) and map methods to them with
  `RequiredPermission(method)`: a declared key or `PermissionKeys.Show`. Without an answer `RequiredRole` decides
  (Operator = "show", Admin = "manage"); a plugin that declares nothing but answers `RequiredRole` gets an automatic
  "manage". Declare "show" only to change its defaults (Web View: off for Operator).
- **Task plugins**: one permission "task:<id>" by default. A task whose choices differ in risk declares `Permissions`
  and answers `RequiredPermissions(payloadJson)` with the keys a run needs (Users: add / update / remove). The run is
  refused before any task starts when the role lacks one; no answer means every declared key.
- **Keys**: letters and digits, starting with a letter, at most 32 characters, unique within the plugin. They are stored
  in roles: never rename one. A new permission reaches the built-in Operator and Viewer by its defaults, custom roles
  get it only when an administrator ticks it.
- **Client**: `ICorePluginClientContext.Has(key)` + `PermissionsChanged` (pages), `ITaskDialogContext.Has(key)` (task
  dialogs), `IToolbarContext.Has(key)` and `IToolbarPlugin.RequiredPermission` (toolbar entries). Build keys with
  `PermissionKeys.Of(pluginId, key)` and `PermissionKeys.Task(taskId, key)`; host keys are `HostPermissionKeys`.
  Hide actions the role lacks and lock inputs that only lead to them (the NTP server page: inputs read-only, no Save).

### Samples

Each plugin has a short entry in `CLAUDE.md` and its own `README.md` with the details (decisions, page and dialog, steps
and task names, device API and verified behavior, settings, tests). A new plugin adds both.

| Plugin | Id | Shows how to |
|---|---|---|
| Snapshot report (`Oadm.Plugins.SnapshotReport`) | `oadm.snapshot-report` | a page with a background job the page polls, results read in chunks |
| VAPIX Commander (`Oadm.Plugins.VapixCommander`) | `oadm.vapix-commander` | a command library from data files, saved commands with encrypted secrets, a three-card page, rollouts as a hidden contributed task plugin with one named step per command |
| NTP server (`Oadm.Plugins.NtpServer`) | `oadm.ntp-server` | a network service (UDP responder, background upstream loop), saved settings, a live request log on the page, a contributed task that configures devices |
| DHCP server (`Oadm.Plugins.DhcpServer`) | `oadm.dhcp-server` | an injectable socket layer (`IDhcpSocketFactory`, in-memory network in the tests), a lease table in plugin settings, live lease changes with versions, a virtualized lease grid and a dialog; manual test plan in its `README.md` |
| PKI (`Oadm.Plugins.Pki`) | `oadm.pki` | a CA key kept with `ctx.Secrets`, trust anchors for the server's certificate rating, OS tools behind `IProcessRunner` (a fake in the tests), a page with several cards and three dialogs. Contributes eight Security task plugins that share the CA through a `Func<PkiService?>`: keys created on the device, CSRs signed by the CA, `UpdateDeviceTlsAsync` to follow the new web server certificate, read-only queries for a grouped certificate list, confirmation-only dialogs (an `ITaskPluginDialog` whose `ShowAsync` shows `MessageWindow.ConfirmAsync` and returns "{}"), and a stateful fake camera (`tests/Oadm.Plugins.Pki.Tests/FakeCamera.cs`) |
| Metadata Monitor (`Oadm.Plugins.MetadataMonitor`) | `oadm.metadata-monitor` | a read-only live view of one camera's event stream: opened through `ctx.EventStreams`, XML parsed on the server, batched messages; the page keeps the newest 10,000 in a `RangeObservableCollection` (one change per batch), filters live and shows the selected message in `ui:CodeView`. Per-page streams end on Stop, on a page change and, for a closed client, when the page's keep-alives stop (a lease pattern for any per-page server resource) |
| System report (`Oadm.Plugins.SystemReport`) | `oadm.system-report` | no page, a toolbar button; background jobs with status deltas by version, files in `ctx.DataDirectory`, streamed device downloads, a ZIP read in chunks |
| Hardening scan (`Oadm.Plugins.HardeningScan`) | `oadm.hardening-scan` | spec in its `README.md`: a read-only scan job over thousands of devices with bounded parallelism, results as events and kept in a plugin setting, request code of other plugins compiled in as linked sources, grid columns built from a catalog (icon-only status cells bound to one byte per row, tooltips built when they open) |

## Event rule actions and events

The event rules (built into the server, "Event rules" in `CLAUDE.md`) run actions
when events happen. Both sides are plugins.

### Writing an action

An action is one class implementing `IActionPlugin` (`Oadm.Sdk.Actions`) in a plugin's `*.Server.dll`: a public class
with a parameterless constructor (the loader registers it like a task plugin), or one a core plugin returns from
`IActionSource.ActionPlugins` (then it shares the core plugin's state and settings). No client part is needed: the rule
dialog draws the settings form from `Fields`.

```csharp
public sealed class EmailAction : IActionPlugin
{
    public string Id => "acme.email.send";              // unique among actions
    public string DisplayName => "Send email";          // the action picker
    public string? IconKey => "send";
    public string Group => "Notifications";
    public bool NeedsDevice => false;                   // true: runs once per target device with ctx.Vapix
    public IReadOnlyList<ActionField> Fields { get; } =
    [
        ActionField.Text("to", "To", required: true),
        ActionField.Text("subject", "Subject", defaultValue: "{event} on {device}"),
        ActionField.Multiline("body", "Message"),
        ActionField.Password("smtpPassword", "SMTP password"),   // stored encrypted, never sent back to a client
    ];

    public string Describe(JsonElement settings) => "Email " + settings.GetProperty("to").GetString();

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation run, CancellationToken ct)
    {
        var subject = ctx.Text(run.Get("subject"));      // {rule} {event} {device} {address} {model} {serial} {state} {time}
                                                         // + every value of the event by name ({port}, {total})
        await Smtp.SendAsync(run.Get("to")!, subject, ctx.Text(run.Get("body")), run.Get("smtpPassword"), ct);
        ctx.Log("Sent to " + run.Get("to"));            // one line in the event log entry; never log secrets
    }
}
```

- **Fields**: `Text`, `Multiline`, `Number` (min / max), `Bool`, `Choice` (fixed list), `DynamicChoice` (the list comes
  from `ListChoicesAsync` while the rule is edited, e.g. saved VAPIX commands), `Password`, `Code` (a body in the code
  editor; `Language` or `LanguageKey` = the field whose value is json / xml / html / text / form), `KeyValues` (name / value
  rows, read with `run.GetPairs(key)`). `Required`, `Hint`, `Default`,
  `VisibleWhenKey` / `VisibleWhenValue` (show a field only for one choice, e.g. "Seconds" for "Pulse"; several as
  "basic|digest") and `OffEdge` (a
  setting of "When the rule turns off", shown only for rules that turn on and off). The host checks required values,
  numbers, choices and lengths (`ActionSettings`, the same code in the dialog and on the server); `Validate` adds the
  action's own rules (errors per field key).
- **Edges**: `ActionInvocation.Edge` is `Fired` (one-shot rule), `TurnedOn` or `TurnedOff`. Set `SupportsTurnOff` when
  the action has an off part and `RunsOn(edge, settings)` to skip edges (default: off edges only with `SupportsTurnOff`).
- **Runs**: at most 16 at once for all rules, 30 s each (`ct` is cancelled; `MaxRunTime(settings)` asks for longer, e.g. a
  pulse the device answers only after it ended); throw for a failure, the message goes into the
  event log; there is no retry. Devices that refuse the login are skipped before the action is called.
- **Settings of the plugin** (an SMTP server): `ctx.Settings` (the package's, or the owning core plugin's).
- **Placeholders**: `ctx.Text(text)` fills `ActionPlaceholders.Fixed` and the event's values; `ctx.Text(text,
  PlaceholderEscape.Json | Xml | Url)` escapes every value for a body or URL, never the text around it. The rule dialog
  lists the placeholders, and actions without devices get **Send test** (sample values).
- Samples: `plugins/Oadm.Plugins.Outputs/SetOutputAction.cs` (device action, choices, a visible-when field, turn off),
  `plugins/Oadm.Plugins.VapixCommander/RunCommandAction.cs` (contributed by a core plugin, dynamic choices, off command).
  `plugins/Oadm.Plugins.Notifications` (actions without devices, code bodies, rows, escaping).

### Publishing events

A core plugin declares its events by implementing `IEventSource` (`Oadm.Sdk.Events`) and publishes through
`ctx.EventPublisher` (null when the host has no event rules). Event types: id, name, group for the picker, kind
(stateful on / off, or stateless one-shot), scope (per device or for the server), optional data fields a condition can
filter on.

- **Stateful**: off is the default. After every start (and after being turned on) send one
  `ReplaceStates(typeId, devicesThatAreOn)` per type: that is the source's "ready" (until then the server keeps the states
  of before the restart and runs nothing for the type; after 2 minutes it counts as nothing on). Afterwards `SetState` per
  change or `ReplaceStates` again whenever convenient (e.g. after each check round): the server works out the changes.
- **Stateless**: `Fire(typeId, deviceId, data)`.
- **Values**: the `data` of `SetState` / `Fire` are the event's values; actions can use each one as a placeholder
  (`{port}`), and a rule condition can filter on declared data fields.
- **Devices** (`EventTypeInfo.DeviceIds`): the devices that can send the event now (null = any device); raise
  `EventTypesChanged` when they change. Dashboards offers only these devices for a widget.
- **Data fields** (`EventField`): name, label, `Kind` (`EventValueKind` Text, Number, YesNo: what the value holds, so a
  dashboard picks the right widget; default Text) and `Filter` (default true; false = a value only, listed as a
  placeholder but without a filter box in the rule dialog).
- **Types that change** (the user creates them, e.g. device triggers): implement `IDynamicEventSource` and raise
  `EventTypesChanged` after `EventTypes` changed. A new stateful type waits for its first `ReplaceStates`; a removed type
  counts as off. Types are read once at registration before `StartAsync`, so a plugin that loads its types from settings
  raises the event in `StartAsync`; rules that use them wait for them meanwhile.
- Calls only queue; `IsWatched(typeId)` says whether an enabled rule or a subscriber uses the type (skip expensive work
  otherwise).
- Sample: Device triggers
  (`plugins/Oadm.Plugins.DeviceTriggers`: dynamic types, a WebSocket per device, "connection lost" per trigger).

### Subscribing to events

`ctx.EventSubscriptions` (null when the host has no event rules) receives every event published to the server, the same
the event rules see: those of every plugin and OADM's own (Device unreachable, Certificate expires soon).
`Subscribe(handler)` returns a subscription; dispose it to end it (the host also ends a plugin's subscriptions when it
stops or is turned off).

- The handler gets `PublishedEvent` {type id, device id or null for server events, kind On / Off / Fired, the values as
  received, time}. A subscription starts with every stateful event that is on now (`FromSnapshot`), then the live events
  in the order the server handled them.
- The handler runs on the subscription's own loop, one event at a time; keep it short (update your own memory, publish to
  your page). The server never waits for it: 10,000 events wait at most, then the oldest are dropped (logged).
- An exception in the handler is logged and the subscription goes on.
- While any plugin subscribes, `IsWatched` is true for every type, so sources never skip events a subscriber needs.
- `Types` lists every event type the server knows (`SubscribedEventType`: the type with its fields and their kinds, the
  source and whether its plugin is on); `TypesChanged` fires when a source changed its types or a plugin was turned on or
  off. Dashboards uses both for its event list.

### Device WebSockets

`IVapixClient.ConnectWebSocketAsync(pathAndQuery, keepAlive, ct)` opens a WebSocket to the device (e.g.
`/vapix/ws-data-stream?sources=events`): ws for HTTP devices, wss with the pinned certificate for HTTPS devices, logged in
with a one-time session token (`wssession.cgi`), so the stored credentials never reach the plugin. The socket pings every
`keepAlive` and aborts when no pong comes back (devices send nothing on a quiet stream). The caller disposes the socket;
reconnect with a backoff when it breaks.

### Device web interfaces in a page

`ctx.DeviceWeb` (`IDeviceWebAccess` on `ICorePluginClientContext`, null on older hosts) opens the web interface of a
managed device for an embedded browser (`Avalonia.Controls.WebView`, shipped by the client host; reference it with
`ExcludeAssets="runtime"`). `OpenAsync(deviceId, ct)` asks the server (refused statuses throw
`DeviceWebRefusedException` with the shared text; the server writes "Opened web interface" to the audit log) and starts a
relay on 127.0.0.1 with its own port: open `session.StartAddress(path)` in the browser once (it hands the browser a
session cookie, other programs get 403), later paths under `session.Origin`. Every request and WebSocket goes through
the server (gRPC `DeviceWebService`), which adds the stored login and checks the pinned certificate. Dispose the session
when the tab closes; the host stops every relay of a page when the page closes. Used by the Web View plugin.

### Live video in a page

`ctx.LiveVideo` (`ILiveVideoAccess` on `ICorePluginClientContext`, null on older hosts) creates the host's live video
component: `Create(new LiveVideoPlace { ... })` returns an `ILiveVideo` whose `View` goes into the page; `Start(deviceId,
camera)` shows a device's channel (0 = the first), `Close()` stops it, `CameraChosen` reports the channel buttons. The
place turns parts on or off (channel buttons, fullscreen, quality, controls button, controls open at the start or in
fullscreen). The server streams with the stored login; the role needs "Live view". The video stops by itself while it is
hidden; dispose it when its place goes away (the host disposes the rest when the page closes). Used by the Live View plugin.

### Files for the page (downloads)

A core plugin that also implements `ICorePluginDownloads` sends files to its page: `OpenDownloadAsync(method, payload,
ct)` returns a `PluginDownload` {`Content` stream, `FileName`, `Length` when known, `AuditDetail`, an optional owner such
as the device response the stream belongs to}. The host checks `RequiredPermission(method)` like an Invoke, writes the
audit entry when `IsAudited(method)` (detail = `AuditDetail`), copies the stream to the client in 256 KB chunks without
compression (gRPC `PluginService.Download`, nothing stored on the server) and disposes the download at the end or when
the client goes away. Errors like Invoke: `ArgumentException` = invalid argument, `InvalidOperationException` = failed
precondition with the message. On the page `await using var file = await ctx.OpenDownloadAsync(method, payload, ct)`,
read `file.Content` with your own token (it is the gRPC stream; disposing ends the call). For a device answer, open the
request with `VapixRequestOptions.StreamResponse` and pass the response as owner. Used by Edge Recording Export.

### Shared lists and tabs

`ui:DeviceTree` with a `DeviceTreeModel` (devices grouped by tag, search, optional `Filter`, `DeviceInvoked`) and
`ui:PageTab` (title, close, active) are the device tree and the tabs of Web View and Live View; a page with devices by
tag or with tabs uses them. `ui:TileGrid` is the grid of tiles of Live View: the page's tab implements `ITileGridSource`
(its tiles as `ITileGridItem`: `Rect` (a `TileRect` of cells), title, content or a message, the remove command, `IsSelected`
(the tile whose settings the page shows, its frame marked), `FrameClass` (a theme class of its frame, e.g. `valueTile`: no video
background), `CopyCommand` (a copy icon left of the X, shown while it can run; tooltip `ITileGridSource.CopyText`);
`IsEditing`; `TryMove(item, rect)`), the grid draws a `TileFrame` per tile (`ShowPreview` where a dragged item lands, `ShowSelectionArea` a selection rectangle), and the rules are an `Oadm.Sdk.Layout.TileLayout`
(size, smallest tile, drop size, most tiles: `Place`, `PlaceSized`, `Fit`, `Drag(rect, TileEdges, dx, dy)`, `Fits`, `FirstOverlap`) that
the server part checks again before it stores. `DeviceTexts.Removed(id)` names a device OADM no longer manages, `ServerErrors.Message(ex)`
reads the user text of a failed server call without a gRPC reference.

### Choose devices first

A page that reads or changes devices never reaches out to every device by itself: a server can manage 30,000, and the
user may want one building. The page starts with the purple choice button (`PageDeviceChoice`: All devices, tags, chosen
devices; the Devices page selection to start with, else the last choice on this computer, kept through
`GetLocalSetting`) as the first control of its toolbar, reads nothing while `IsEmpty`, and sends only the ids of
`Resolve()` to the server (or "all" for All devices, so the request does not grow with the device count). A page for
one device (Metadata Monitor, I/O Monitor) opens `DevicePickerWindow.ShowOneAsync` instead of a drop-down. Work that
takes long per device (a scan) starts with its own button, not on the choice. Everything that can wait longer than 1 s
shows a ring or progress row (CLAUDE.md "loading feedback"). Examples: Edge storage, Image Health Dashboard, Hardening
scan, Snapshot report, Action Rule Editor.

### Table pages read on request

A page with one row per device that the server reads when the page asks (Image Health Dashboard, Edge storage) uses the
linked sources in `plugins/Shared/`: `DeviceSweep<TRow>` on the server (one sweep at a time, `Parallelism` devices at once,
a timeout per device, rows by device id, changed and removed rows pushed in batches through the plugin's callbacks;
`Failure(ex)` is the usual text of a failed read) and `SweepRows<TRow, TVm>` on the page (view models by id, updated in
place, search and new rows with one reset; `AddressSortKeys.Of`). Link them with
`<Compile Include="..\Shared\DeviceSweep.cs" Link="Common\DeviceSweep.cs" />`. Refuse devices with
`DeviceMessages.Refusal(status, refuseUnreachable: true)` before any request, and watch the page events with
`ctx.RunAsync(HandleEvent, retryDelay, ct)` (`PluginEventWatch`, reconnects after a drop). The disk list of the Edge storage
API is `Oadm.Plugins.EdgeStorage.Disks` (`EdgeDisk`, `EdgeDisks.Parse`, `FirstProblem`); link
`..\Oadm.Plugins.EdgeStorage\Disks\*.cs` instead of parsing `disks/list.cgi` again. The devices' own action rules,
recipients and schedules over SOAP are `plugins/Shared/ActionEngine/*.cs` (`SoapActionEngine`, namespace
`Oadm.Plugins.ActionEngine`); a device's event list is `plugins/Shared/DeviceEvents/*.cs` (`EventDeclarations`,
`DeviceEvent`; the client part's filter rows in `DeviceEvents/Client`).

### Host support for service plugins (NTP, DHCP, ...)

- **Saved settings**: `ctx.Settings.GetAsync/SetAsync(key, json)` (per plugin, server database). Store one JSON
  document on Save (the NTP server uses key `config`: enabled, interface, upstream) and read it in `StartAsync`, so the
  service comes back after a server restart. Never block `StartAsync` on the network: start background loops.
- **Live events to the page**: `ctx.Events?.Publish(topic, payloadJson)` (SDK `IPluginEvents`, null on hosts without
  it). Fire and forget, never blocks; every watching page has its own queue of 256 events (oldest dropped). Batch
  high-rate sources (the NTP request log publishes at most every 500 ms) and keep payloads small (max 1 M characters).
  On the page: `await foreach (var e in ctx.WatchEventsAsync(ct))` (gRPC `PluginService.Watch`) while the view is
  attached. The sequence ends or throws when the connection drops, and is empty on older hosts and in fake mode. So
  always: subscribe, read the full state with `InvokeAsync`, apply events (deduplicate by a sequence number), and when
  the sequence ends wait about 2 s, read again and watch again (this doubles as polling). Pattern:
  `NtpServerViewModel.RunAsync`.
- **Server network interfaces**: `Oadm.Sdk.Network.SystemNetworkInterfaces.Instance.List()` returns
  `ServerNetworkInterface` (id, name, description, addresses with IPv4 first, up, loopback; `PrimaryAddress`,
  `PrimaryIpv4`, `PrefixLengths`, `Gateways`, `DnsServers`, `DnsSuffix`, `Ipv4Index`) on Windows, Linux and macOS.
  Depend on `IServerNetworkInterfaces` so tests inject fixed interfaces. Return the list from a page method: it lives
  on the server, the client machine has other interfaces. `InterfaceOptions.From(nic, addressText)` builds the select
  entry (`InterfaceOption`, label "Ethernet - 10.0.0.17/24 (Intel I219)"). On the page,
  `Oadm.Sdk.Client.Network.InterfaceSelection` is the select's view model (selection kept across refreshes; a
  configured interface that is gone stays as "(not available)"); bind the ComboBox to `Listen.Items` /
  `Listen.Selected`.
- **Privileged ports**: make the port injectable (tests bind 0 on 127.0.0.1 or use an in-memory transport, never the
  real port). Map bind errors per OS with `Oadm.Sdk.Network.PortBindErrors` (`Classify(SocketError, HostOs)`; texts
  `InUseText`, `PermissionText`, `FindUdpPortOwner`, `PermissionFix`): Windows has no privileged ports, so
  AccessDenied means another program holds the port; Linux needs root or `cap_net_bind_service`; macOS needs root for
  a single address. `HostOsInfo.Current`; `ScQueryServiceProbe` tells whether a Windows service runs (e.g. W32Time,
  DHCPServer). See `NtpStatusTexts.ForBindError` and `DhcpStatusTexts.ForBindError`.
- **Trust anchors**: `ctx.TrustAnchors?.Set(derCertificates)` (SDK `ITrustAnchors`, null on hosts without it)
  replaces the plugin's set of CA certificates (public DER only) the server trusts, besides the OS store, when it rates
  device certificates (Certificate column). A device certificate that chains to one of them is Trusted; a self-signed
  one stays Self-signed; pinning is never affected. The host keeps one set per plugin (the union counts) and removes it
  when the plugin stops. Devices show the new rating after their next full refresh. The PKI plugin sets its active CA,
  its chain and the previous CAs on start and on every change.
- **Device event streams**: `ctx.EventStreams?.OpenAsync(deviceId, ct)` (SDK `IDeviceEventStreams`, null on hosts
  without it) opens the device's RTSP event stream (`rtsp://<device>/axis-media/media.amp?video=0&audio=0&event=on`,
  port 554, Digest with the stored credentials, which never reach the plugin) and returns an `IDeviceEventSource`.
  `ReadAsync(ct)` yields one complete `tt:MetadataStream` XML document per RTP marker (`DeviceMetadataDocument` with
  the server receive time); `LostDocuments` counts documents dropped for packet loss or size (over 1 MB); dispose =
  TEARDOWN. Failures are `DeviceStreamException` with `Error` (Unreachable: retry with backoff; Unauthorized,
  NotSupported: permanent) and a user message ("Unauthorized - HTTP 401", "The device has no event stream",
  "Unreachable - ..."). Check `IDeviceInfo.Status` first (CertificateChanged, CredentialsRequired, PasswordNotSet). The
  host sends keep-alives and detects a silent connection.
- **Bulk lists on pages**: `Oadm.Sdk.Client.Collections.RangeObservableCollection<T>` (`ReplaceAll`, `AddRange`,
  `InsertRange`, `RemoveAll`, `RemoveFirst`: one Reset instead of one event per item), the collection the host grids
  use.
- **Status line**: `Oadm.Sdk.Network.ServiceStatus` (kind ok / neutral / warning / error, plain text, detail = the
  fix, shown as tooltip). Protocol details go to the server log only.
- **Rate limits**: `Oadm.Sdk.Network.KeyedRateLimiter<TKey>`: a token bucket per key (client address, MAC), an
  LRU-capped table with idle expiry, one global bucket, per-key notices ("log once a minute"); no allocations in the
  steady state. NTP limits per client address, DHCP per MAC.

### Page header (`ui:PageHeader`)

A core plugin page does not repeat its title in a card heading. Put the page description and any trailing content into
the host's page header with attached properties on the page's root control:

```xml
<UserControl ... ui:PageHeader.Subtitle="Answers the time requests of the cameras.">
  <ui:PageHeader.Trailing>
    <!-- trailing content, e.g. a value the whole page describes -->
  </ui:PageHeader.Trailing>
  <!-- the card content starts directly with the form -->
</UserControl>
```

The host shows the subtitle next to the page title and the trailing control right of it; the trailing control keeps
the page's DataContext. A status goes directly left of the Save button it reports on, never top right in the header.
