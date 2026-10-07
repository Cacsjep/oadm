# OADM plugins

How to build a task plugin with a server part and an optional client (dialog) part. The
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

- **Name and group.** `DisplayName` is what the context menu, the toolbar and the tasks pane show:
  at most `TaskPluginNames.MaxDisplayNameLength` (32) characters and **no trailing "..."**, also not for
  tasks that open a dialog (the host never appends dots and strips them defensively; the server loader
  logs a warning and shortens longer names with an ellipsis). `Group` (default `TaskGroups.General`)
  is the context menu submenu the task appears in: reuse a `TaskGroups` constant (`Applications`,
  `General`, `Maintenance`, `Network`, `Security`, `Users`, `Video`) when one fits, or name a new group
  (it gets its own submenu, sorted by name; groups always show as submenus, even with one task).
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
  Test with `TaskStepList` + `tests/Shared/StepRun.cs` (same end rules as the server).
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
  | `ui:StatusChip Text IsOk IsWarning IsError IsAccent` | every status value (border-only chip), in grid cells with `Margin="10,0"` |
  | `ui:FileRow FileName Details Error Command` | a chosen local file with its "Choose file..." button; format sizes with `FileSizeText.Format` |
  | `ui:ProgressRow Value Text IsActive` | upload or scan progress (0 to 100) with its status text |
  | `ui:OadmIcon Data` | a single icon |

- Styles and classes from the host theme (`Themes/OadmTheme.axaml`), e.g. `Border.card`,
  `TextBlock.secondary`, `TextBlock.fieldLabel`, `TextBlock.warning`, `TextBlock.error`,
  `Button.primary`, `Button.secondary`, `Button.toolbar`, `Border.vseparator`. Form fields are
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
