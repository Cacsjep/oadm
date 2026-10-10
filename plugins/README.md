# OADM plugins

How to build a task plugin (server part, optional client dialog), a toolbar plugin and a core plugin with its own
rail page. Contracts: `src/Oadm.Sdk` (server) and `src/Oadm.Sdk.Client` (client). The spec and the device safety HARD
RULE are in `CLAUDE.md` ("Plugin System").

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
`Oadm.Plugins.Restart.csproj`). In a repository checkout the server (`PluginPaths.Development`) and the client
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
  `tests/Shared/StepRun.cs` (the server's end rules, including "Completed: Done").
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
  `ExtendClientAreaToDecorationsHint="True"`,
  `ExtendClientAreaTitleBarHeightHint="{DynamicResource Oadm.TitleBarHeight}"`, a root grid of title bar, cards with
  `Margin="16,0"` (`16,16,16,0` for each further card) and the footer. A dialog with a single form has no card: the form
  sits in `Border.dialogBody`, the window has a fixed width and `SizeToContent="Height"`.
- Controls from `Oadm.Sdk.Client.Controls` (`xmlns:ui="using:Oadm.Sdk.Client.Controls"`), never hand-built copies:

  | Control | Use for |
  |---|---|
  | `ui:DialogTitleBar Text="..."` | first row of every dialog window (title, draggable, caption buttons) |
  | `ui:CardHeader Title Description` | heading of every card; child controls go right of the title (e.g. a device picker). Class `flush` when the content below is optional and adds `Margin="{DynamicResource Oadm.GapTop}"` itself |
  | `ui:DialogFooter CancelCommand` | last row: Cancel left; children are the right-hand buttons (`Button.secondary` for Back, one `Button.primary`) |
  | `ui:IconLabel Icon Text` | every icon + text row, e.g. button content |
  | `ui:ToolbarButton Text IconKey (or Icon) IsPrimary IsIconOnly` | every toolbar button: `Button.toolbar` (or `Button.primary`) with an `IconLabel`; `IsIconOnly` shows only the icon with `Text` as tooltip |
  | `ui:ToolbarSeparator` | vertical line between toolbar groups |
  | `ui:SearchBox Text` | every search field |
  | `ui:FormField Label Hint Error` | **every labeled form row**: label left, level with the input; below the input its validation error, else the `Hint` (small, secondary). Widths from the theme (`Oadm.FormLabelWidth` 180, `Oadm.FormInputWidth` 280; a narrower row shrinks the input). Class `wide`: the input fills the row; class `inline`: the label sizes to its text (fields in one line, e.g. From / To). No label: the label column stays empty, so a check box or button row lines up with the inputs. `Error` only for content without data validation (a group of check boxes, a text value) |
  | `ui:PasswordBox Text` | **every password field**: bullet mask and an eye button ("Show password" / "Hide password"); never a `TextBox` with `PasswordChar` |
  | `MessageWindow.ConfirmAsync(owner, title, message, confirmText)` / `ShowMessageAsync` | **every confirmation or message popup** (the host uses the same window); e.g. the Network dialogs confirm risky changes on Apply / Finish, no inline warning with a check box |
  | `ui:StatusChip Text IsOk IsWarning IsError IsAccent` | every status value (colored icon + text), in grid cells with `Margin="10,0"` |
  | `ui:FileRow FileName Details Error Command` | a chosen local file with its "Choose file" button; sizes with `FileSizeText.Format` |
  | `ui:ProgressRow Value Text IsActive` | upload or scan progress (0 to 100) with its status text |
  | `ui:OadmIcon Data` | a single icon |
  | `ui:CodeView Text Language ContentType IsFormatted` | **every read-only code or response display** (bodies, headers, JSON/XML previews): monospace, selectable (Ctrl+C and context menu copy), no wrapping, horizontal scroll. `Language`: `Auto` (default: from `ContentType`, then from the text), `Json`, `Xml` (also SOAP), `KeyValue` (param.cgi `key=value`, `# Error` lines red) or `Plain`. JSON and XML are pretty-printed with 2 spaces, also when minified; `IsFormatted="False"` shows the exact text, still highlighted (for a Pretty / Raw switch). Text that does not parse, or is longer than `HighlightLimit` (512 K characters), shows without colors. Colors: theme brushes `Oadm.Code.KeyBrush`, `StringBrush`, `NumberBrush`, `LiteralBrush`, `PunctuationBrush`, `TagBrush`, `AttributeBrush`, `CommentBrush`, `ErrorBrush`. `CodeText` (`Detect`, `Prepare`, `FormatJson`, `FormatXml`, `Tokenize`) works without UI. Editable bodies stay a `TextBox Classes="code"` |

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
- An error shows once the field was edited or a submit was tried, never on an untouched form. Rules always run: submit
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
`Icon.details`, `Icon.cancel`, `Icon.chevronDown`, `Icon.chevronRight`, `Icon.chevronUp`, `Icon.close`, `Icon.check`,
`Icon.tag`, `Icon.groupBy`, `Icon.edit`,
`Icon.server`, `Icon.externalLink`, `Icon.more`, `Icon.key`, `Icon.lock`, `Icon.eye`, `Icon.eyeOff`, `Icon.copy`, `Icon.log`, `Icon.logs`, `Icon.panelOpen`,
`Icon.panelClose`, `Icon.deleteAll`, `Icon.video`, `Icon.network`, `Icon.firmware`, `Icon.users`,
`Icon.user`, `Icon.logout`, `Icon.audit`, `Icon.app`, `Icon.upload`, `Icon.file`, `Icon.folder`, `Icon.start`, `Icon.stop`,
`Icon.snapshot`, `Icon.export`, `Icon.clock`, `Icon.activity`, `Icon.info`, `Icon.shield`, `Icon.clipboardCheck`,
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
  - `ctx.Secrets` (`ISecretProtector`): encrypt secrets you store in `Settings`; null = do not store them.
  - `ctx.EventStreams`: device event streams, see below. `ctx.Tasks.Cancel(taskId)`.
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
  and `Owner`.
- **Rail group**: `CorePluginGroup Group` (default `Extensions`) puts the page under a group header in the rail, in
  this order: Servers, Automation, Security, Monitoring, Reporting, Maintenance, Integrations, Utilities, Extensions.
- **Without a page**: `HasPage => false` lists the plugin with `CorePluginInfo.no_page` and the client adds no rail
  entry. Its client part is e.g. a toolbar plugin (see "Toolbar plugins").
- Build pages like dialogs: shared controls (`ui:ToolbarButton`, `ui:SearchBox`, `ui:ProgressRow`, `ui:StatusChip`,
  ...), theme classes only, view models without Avalonia platform calls (decode images and open windows through a
  small interface the view implements, so the view model is testable).
- **Fake mode**: add the plugin's backend to `FakeOadmApi` (`FakeCorePlugins`, `InvokeCorePluginAsync`) so `--fake`
  shows the page.

### Samples

Each spec is in `CLAUDE.md` under the plugin's name unless noted; NTP server, DHCP server and PKI also have one in
`docs/specs/` (`ntp-server.md`, `dhcp-server.md`, `pki.md`).

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
