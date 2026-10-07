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

- `CanRun(device)`: cheap, synchronous; check `device.Apis.Supports(apiId, minVersion)`.
- `ExecuteAsync(ctx, device, payloadJson, ct)` runs once per device; every device is its own task.
  Re-check `(await ctx.Vapix.GetApiListAsync(ct)).Require(...)` before the first write.
- `ctx.ReportProgress(percent, message)`, `ctx.ReportWarning(message)` (Done with warnings),
  `ctx.Log(level, message)` (task log shown in the task details). Never put secrets in them.
- `ctx.Files.FindAsync / OpenReadAsync(fileId)` for files the dialog uploaded.
- `ctx.UpdateCredentialsAsync(user, password, ct)` after changing the password of the account
  OADM uses (re-read `ctx.Vapix` afterwards); `ctx.MarkCredentialsInvalid()` after a factory
  default. `device.CredentialUserName` is that account: never remove or demote it.
- `MaxParallelDevices` limits how many tasks of the plugin run at once (default 8).
- Long requests: `request.Options.Set(VapixRequestOptions.Timeout, TimeSpan.FromMinutes(20))`
  before `ctx.Vapix.SendAsync(request, ct)`; use `StreamContent` for large bodies.
- `ITaskPluginQuery.QueryAsync` serves the dialog (read-only, 30 s timeout on the server).

## Client side

`ITaskPluginDialog.ShowAsync(ctx, devices, owner)` shows a window and returns the payload JSON
(null = cancelled). `ctx.QueryAsync(deviceId, method, payload, ct)` calls the plugin's query,
`ctx.UploadAsync(path, progress, ct)` uploads a file (progress 0.0 to 1.0) and returns its id for
the payload. Failures are `Grpc.Core.RpcException`; show `Status.Detail` to the user.

Build dialogs from the host look (HARD RULE: reuse controls, no style differences):

- Window and layout like the add devices wizard: `Width`/`Height` explicit (e.g. 1040 x 700),
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
  | `ui:IconLabel Icon Text` | every icon + text row, e.g. button content; toolbar rows are `Button.toolbar` with an `IconLabel`, like the Devices page |
  | `ui:SearchBox Text` | every search field |
  | `ui:StatusChip Text IsOk IsWarning IsError IsAccent` | every status value (border-only chip), in grid cells with `Margin="10,0"` |
  | `ui:FileRow FileName Details Error Command` | a chosen local file with its "Choose file..." button; format sizes with `FileSizeText.Format` |
  | `ui:ProgressRow Value Text IsActive` | upload or scan progress (0 to 100) with its status text |
  | `ui:OadmIcon Data` | a single icon |

- Styles and classes from the host theme (`Themes/OadmTheme.axaml`), e.g. `Border.card`,
  `TextBlock.secondary`, `TextBlock.fieldLabel`, `TextBlock.warning`, `TextBlock.error`,
  `Button.primary`, `Button.secondary`, `Button.toolbar`, `Border.vseparator`. Form fields are
  label left, input right like the wizard steps: `Grid ColumnDefinitions="150,280"` with a
  `TextBlock.fieldLabel`; stack rows in `StackPanel Classes="form"` (or `Grid Classes="form"`),
  which sets the row spacing. Tables are `DataGrid`s (the theme styles them). No local colors,
  font sizes, font weights or paddings.
- Icons are application resources of the client, available to plugin windows at runtime
  (`Icon="{DynamicResource Icon.key}"`; a plugin project cannot resolve them at compile time,
  so use `DynamicResource`). Available keys:

  `Icon.devices`, `Icon.tasks`, `Icon.settings`, `Icon.plugin`, `Icon.add`, `Icon.range`,
  `Icon.remove`, `Icon.refresh`, `Icon.restart`, `Icon.identify`, `Icon.columns`, `Icon.search`,
  `Icon.details`, `Icon.cancel`, `Icon.chevronDown`, `Icon.chevronUp`, `Icon.close`, `Icon.check`,
  `Icon.server`, `Icon.externalLink`, `Icon.key`, `Icon.log`, `Icon.logs`, `Icon.panelOpen`,
  `Icon.panelClose`, `Icon.deleteAll`, `Icon.video`, `Icon.network`, `Icon.firmware`, `Icon.users`,
  `Icon.user`, `Icon.app`, `Icon.upload`, `Icon.file`, `Icon.folder`, `Icon.start`, `Icon.stop`,
  `Icon.device.camera`, `Icon.device.encoder`,
  `Icon.device.speaker`, `Icon.device.audio`, `Icon.device.intercom`, `Icon.device.radar`,
  `Icon.device.io`, `Icon.device.door`, `Icon.device.generic`.

  A plugin's `IconKey` (context menu, toolbar) is the key without the `Icon.` prefix, e.g.
  `restart`. A new icon is added to `src/Oadm.Client/Themes/Icons.axaml` (24x24 Lucide stroke
  geometry) and listed here.
