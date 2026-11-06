# OADM plugins

The plugins that ship with OADM. This file holds what only matters inside this repository: the folders, how a bundled
plugin is built, deployed, tested and packaged, and the conventions.

How to write a plugin is in the **SDK guide**, the developer pages of the website (`website/src/content/docs/dev/`,
published at https://oadm.app/dev/overview/). It is the one place that explains the SDK: see
[SDK guide](#sdk-guide) for the page of each topic. A new or changed SDK member is explained there, never here.

| Where | What |
|---|---|
| [SDK guide](#sdk-guide) | How to write a plugin, with examples |
| SDK reference (https://oadm.app/api/, `manage build docs` locally) | Every type and member, from the doc comments of `src/Oadm.Sdk` and `src/Oadm.Sdk.Client` |
| [`docs/spec/plugins.md`](../docs/spec/plugins.md) | The rules and decisions of the plugin system, the host side |
| `CLAUDE.md` "HARD RULE: device safety for task plugins" | The rules every plugin that changes a device follows |
| [`docs/spec/bundled-plugins.md`](../docs/spec/bundled-plugins.md) | One entry per bundled plugin: id, what it does, on or off by default, its rules |
| `plugins/<name>/README.md` | Each plugin's own decisions and details |

## Folders

```
plugins/
  Oadm.Plugins.<Name>/              server part
    Oadm.Plugins.<Name>.csproj      AssemblyName Oadm.Plugins.<Name>.Server
    plugin.json                     written by hand, see "plugin.json"
    README.md                       the plugin's documentation
  Oadm.Plugins.<Name>.Client/       optional client part (Avalonia)
    Oadm.Plugins.<Name>.Client.csproj   AssemblyName Oadm.Plugins.<Name>.Client
  Shared/                           source linked into several plugins, see "Shared sources"
tests/
  Oadm.Plugins.<Name>.Tests/        one test project per plugin
  TestPlugins/                      the plugins the loader tests load (Oadm.TestPlugins.Sample: a task and a toolbar plugin)
samples/                            the plugins of the developer tutorials, never deployed
```

The plugin id (`oadm.<name>`) names the folder under `artifacts/plugins/`, the settings and the permissions. Never
change it. A package can hold several core plugins (PKI: `oadm.pki`, `oadm.est`, `oadm.radius`).

## Build, deploy, run

- Both projects of a plugin copy their output to `artifacts/plugins/<plugin id>/` after every build (MSBuild target
  `OadmDeployPlugin`). In a checkout the server (`PluginPaths.Development`) and the client
  (`ClientPluginLoader.DefaultRoots`) load plugins from there, so `dotnet build` of the plugin is enough to try it with
  `./manage.sh run dev` (or `dotnet run` of server and client).
- `manage build plugins` builds every plugin; `manage run dev --no-plugin-build` uses what is in `artifacts/plugins`.
- **While a client or server runs**, build and test with `-p:OadmSkipPluginDeploy=true` (for example together with
  `--artifacts-path`): the plugins' own dependency DLLs stay locked, and a running app must never see a half-copied
  plugin.
- The samples set `OadmSkipPluginDeploy` (`samples/Directory.Build.props`), so a development OADM never loads them.
- `--fake` shows a plugin page without a server only when its backend is in `FakeOadmApi` (`FakeCorePlugins`,
  `InvokeCorePluginAsync`) of `src/Oadm.Client`: add it there for a new page.

## Project files

A bundled plugin references the SDK by hand. `Oadm.Plugins.Restart.csproj` is the reference for a server part,
`Oadm.Plugins.DateTime.Client.csproj` for a client part:

```xml
<PropertyGroup>
  <AssemblyName>Oadm.Plugins.<Name>.Server</AssemblyName>        <!-- client part: Oadm.Plugins.<Name>.Client -->
  <EnableDynamicLoading>true</EnableDynamicLoading>
  <OadmPluginId>oadm.<name></OadmPluginId>                      <!-- the same in both parts and in plugin.json -->
  <OadmPluginOutputDir>...artifacts/plugins/$(OadmPluginId)</OadmPluginOutputDir>
</PropertyGroup>

<!-- The SDK is shared from the host: never copy it into the plugin folder. -->
<ProjectReference Include="..\..\src\Oadm.Sdk\Oadm.Sdk.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>
<!-- client part also: Oadm.Sdk.Client the same way, the server project for shared payload types, and -->
<PackageReference Include="Avalonia" Version="12.1.4" ExcludeAssets="runtime" />
<PackageReference Include="Avalonia.Controls.DataGrid" Version="12.1.2" ExcludeAssets="runtime" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" ExcludeAssets="runtime" />
```

- The versions are the host's (`src/Oadm.Plugin.Sdk/targets/Oadm.Plugin.Versions.props`).
- The server part copies `plugin.json` to its output (`CopyToOutputDirectory`); the client part's deploy target leaves
  it out.
- The samples and the plugins of `manage new` use the plugin project SDK from source instead: their
  `Directory.Build.props` imports `src/Oadm.Plugin.Sdk/targets/Oadm.Plugin.Repo.props` (project references to `src/`
  instead of the NuGet packages, `plugin.json` written from the project's properties, the plugin folder in
  `artifacts/plugins/`). These are the same files as the NuGet package `Oadm.Plugin.Sdk`; the properties are in the guide,
  [Plugin basics](../website/src/content/docs/dev/plugins.md).
- **Banned APIs.** `Directory.Build.props` turns the banned-API analyzer (RS0030,
  `src/Oadm.Plugin.Sdk/targets/BannedSymbols.txt`) into an error for `src/` and `plugins/`: no own sockets, `Ping`,
  `Dns` or HTTP handlers. An exception (your own mail or HTTP server) carries a `SuppressMessage` with a justification
  and is listed in `RoutingRulesTests.AllowedBannedApiUsers`.

## plugin.json

Written by hand for a bundled plugin:

```json
{
  "id": "oadm.restart",
  "version": "0.1.0",
  "minSdkVersion": "0.4.0",
  "displayName": "Restart",
  "description": "Restarts devices.",
  "alwaysOn": true,
  "siteSupport": "full"
}
```

- `description`: one short sentence for the Plugins page. Every bundled plugin has one.
- `enabledByDefault` (default true): false for a plugin that is off until an administrator turns it on. `alwaysOn`
  (default false): core functionality that cannot be turned off. Both lists are in `docs/spec/plugins.md` "Plugins on
  and off"; `PluginPackageTests` checks them.
- `minSdkVersion`: 0.4.0, or the version of the newest SDK member the package uses at run time
  (`docs/spec/plugins.md` "SDK versions"). Never below 0.4.0: users with a device group could not use the plugin.
- `siteSupport`: always written (`full` or `none`); a `plugin.json` without it counts as `none`. `sitePorts` lists the
  ports the plugin listens on at sites.
- `license`, `pro`: `docs/spec/plugins.md` "Plugin licenses" and "OADM PRO plugins".

## A new plugin

`./manage.sh new task|page|action --name "My plugin"` (PowerShell `.\manage.ps1 new ...`) copies the plugin of the
matching developer tutorial under your name into `plugins/` and `tests/` and adds it to `Oadm.sln`. It builds, its
tests pass and it runs; then change it. The tutorials of the guide explain every file.

A new bundled plugin also gets, in the same change:

- its `README.md` and its entry in `docs/spec/bundled-plugins.md` ("Plugin documentation" in `docs/spec/plugins.md`);
- a line in "Solution Layout" of `CLAUDE.md` and, for a new feature, its spec file;
- its user page on the website (`website/src/content/docs/tools/` or `tasks/`) and the sidebar entry in
  `website/astro.config.mjs`;
- a `CHANGELOG.md` line.

The templates outside this repository (`dotnet new oadm-task|oadm-page|oadm-action`) are generated from `samples/` by
`tools/Oadm.NewPlugin/Templates.cs`, with a README, an `AGENTS.md` and a `CLAUDE.md` that imports it; `templates/` packs
them. Change a sample, never a template.

## Tests

One test project per plugin (`tests/Oadm.Plugins.<Name>.Tests`), on the fakes of `src/Oadm.Sdk.Testing`. The guide page
[Testing](../website/src/content/docs/dev/testing.md) explains them; the rules for every test are in `CLAUDE.md`
("tests that never flake"). Repository tests that look at every bundled plugin:

| Test | Checks |
|---|---|
| `Oadm.Core.Tests` `TaskPluginNamesTests` | The names of every task plugin deployed to `artifacts/plugins` |
| `Oadm.Server.Tests` `PluginPackageTests` | The bundled manifests: off by default, always on, descriptions |
| `Oadm.Server.Tests` `DeviceGroups/CanaryTests` | Every bundled plugin's tests run a `PluginCanary` for device groups and its `plugin.json` says 0.4.0 or later. Only a package without a core plugin may be listed in `PluginsConverted` instead, with its reason |
| `Oadm.Core.Tests` `RoutingRulesTests` | The banned APIs and their allowed users |
| `Oadm.ApiDocs.Tests` | Every public SDK type and member has a doc comment; `HeadlessDispatchConventionTests`: no async headless body without a value |

- Headless UI helpers (`HeadlessEntry`, `Headless.Host`, `Pump`, `Capture`) are in the `Support.cs` of a page plugin's
  tests, for example `tests/Oadm.Plugins.ImageHealth.Tests/Support.cs`.
- A plugin with a device list has a `Perf` test with `DeviceLimits.MaxDevices` fake devices.
- Hardware tests that write are `Category=HardwareWrite` and run only when the developer asks.
- `tests/Shared/` holds sources linked into several test projects (`TestRepository`, `RecordingFirewall`,
  `RecordingStreams`, `CallWatchers`, `CountingDeviceScopes`, `FakeAxisCom`, `FakeSiteTransport`).
- The sample toolbar plugin is `tests/TestPlugins/Oadm.TestPlugins.Sample.Client/SampleToolbarPlugin.cs` ("Sample (n)"
  with the selection count, runs the sample task), loaded by `ToolbarPluginTests`.

## Shared sources

`plugins/Shared/` is source that several plugins compile in. It is not part of the SDK: every type is `internal` in the
plugin that links it.

```xml
<Compile Include="..\Shared\DeviceSweep.cs" Link="Common\DeviceSweep.cs" />
```

| Source | What |
|---|---|
| `CachedApiCheck.cs` | The API check of `CanRun` on the cached list: one pass, no LINQ (the host calls `CanRun` for every device and plugin) |
| `DeviceSweep.cs` | The server side of a table page read on request (Image Health Dashboard, Edge storage, Action Rule Editor): one sweep at a time, `Parallelism` devices at once, a timeout per device, rows by device id, changed and removed rows pushed in batches, `Failure(ex)` for a failed read. `DeviceSweeps<TRow>` keeps one sweep per device scope: `For(caller)`, publish with `PluginEventAudience.DeviceScope(key)`, counts from `ScopedSnapshot()`, rows for a caller from `SnapshotAsync(caller)` |
| `SweepRows.cs` | The client side of such a page: view models by device id, updated in place, search and new rows with one reset; `AddressSortKeys.Of` |
| `ContextCall.cs` | `ContextCall` (the call of the old `InvokeAsync` overload, for in-process work without a user) and `OwnerScope` (objects kept per device group: `Sees`, `ForSave`, `HasNoDevices`, `LeftOwnerAsync`) |
| `ViewerScopes.cs` | `ViewedSet`: the devices a user or a device scope sees, as sets, to filter rows of server-wide state (PKI, EST, NTP, DHCP) |
| `ActionEngine/` | The devices' own action rules, recipients and schedules over SOAP (`SoapActionEngine`, namespace `Oadm.Plugins.ActionEngine`) |
| `DeviceEvents/` | A device's event list (`EventDeclarations`, `DeviceEvent`); the client part's filter rows in `DeviceEvents/Client` |
| `DeviceClock/` | `CameraClock`: the clock check before a certificate goes onto a camera |
| `DeviceId/` | `AxisDeviceId`: the Axis device ID check, with the Axis root certificates |

Other plugins' code is linked the same way: the disk list of the Edge storage API is
`..\Oadm.Plugins.EdgeStorage\Disks\*.cs` (`EdgeDisk`, `EdgeDisks.Parse`, `FirstProblem`); link it instead of parsing
`disks/list.cgi` again. The rules for sweeps and saved objects of a device group are in `docs/spec/device-scope.md`.

## Conventions

- **Documentation.** Every plugin has its `README.md` (decisions, page and dialog, steps and task names, the device API
  and what was verified on which device and AXIS OS, settings, tests) and its entry in `docs/spec/bundled-plugins.md`.
  A changed decision updates both.
- **Icons.** A new icon goes into `src/Oadm.Theme/Icons.axaml` (a 24 x 24 Lucide stroke geometry) and into the icon list
  of the guide, [UI kit](../website/src/content/docs/dev/ui.md#icons).
- **Shared controls.** A control that a second plugin needs moves into `src/Oadm.Sdk.Client/Controls`, with its entry in
  `docs/spec/visual-style.md` "Shared controls" and in the guide's UI kit.
- **A new SDK member** is a default interface member, gets a doc comment, a line in the SDK history of
  [SDK versions](../website/src/content/docs/dev/compatibility.md), its place in the guide and, when it is a rule, in
  `docs/spec/plugins.md`.
- **Wording.** All UI texts and docs follow "Wording" in `CLAUDE.md`.

## Packaging

- `manage publish` publishes every plugin project once into `artifacts/publish/plugins/<plugin id>/` (server and client
  part together) and copies it next to both exes, so a new plugin is packaged without further changes
  (`docs/spec/packaging.md`).
- `THIRD-PARTY-NOTICES.txt` is generated from the `*.deps.json` of every plugin folder (`tools/Oadm.Notices`). A plugin
  folder, or a part of one, with its own `LICENSE` is named at the top (PKI's `Est/` and `Radius/`).
- An optional plugin (`<OadmAddOn>true</OadmAddOn>`, Text recognition) is never in the installers: `manage package
  addons` makes its ZIP per platform (`docs/spec/add-ons.md`).
- `Oadm.Server --self-check` of the published server loads every plugin and calls its `ICorePlugin.SelfCheck`; the
  release runs it on every platform.

## SDK guide

The source of each page is `website/src/content/docs/dev/<page>`; it is published at `https://oadm.app/dev/<page>/`.

| Topic | Page | Published |
|---|---|---|
| Architecture, what a plugin can do | [overview.md](../website/src/content/docs/dev/overview.md) | https://oadm.app/dev/overview/ |
| NuGet packages, templates, the `oadm` command, the development server, shipping | [plugin-sdk.md](../website/src/content/docs/dev/plugin-sdk.md) | https://oadm.app/dev/plugin-sdk/ |
| Build OADM from source, `manage` | [build.md](../website/src/content/docs/dev/build.md) | https://oadm.app/dev/build/ |
| Plugin kinds, project files, `plugin.json`, loading, the rules every plugin follows | [plugins.md](../website/src/content/docs/dev/plugins.md) | https://oadm.app/dev/plugins/ |
| Tutorials: a task with a dialog, a page, an event rule action | [tutorial-task.mdx](../website/src/content/docs/dev/tutorial-task.mdx), [tutorial-page.mdx](../website/src/content/docs/dev/tutorial-page.mdx), [tutorial-action.mdx](../website/src/content/docs/dev/tutorial-action.mdx) | https://oadm.app/dev/tutorial-task/, https://oadm.app/dev/tutorial-page/, https://oadm.app/dev/tutorial-action/ |
| `IDeviceInfo`, status and refusals, `IVapixClient`, `SendAsync` and its limits, API checks, WebSockets, event streams, video sources, network checks, a new address or certificate | [devices.md](../website/src/content/docs/dev/devices.md) | https://oadm.app/dev/devices/ |
| Task plugins: names and groups, greyed entries, steps, the dialog, queries, uploads, permissions, files from axis.com, device safety | [tasks.md](../website/src/content/docs/dev/tasks.md) | https://oadm.app/dev/tasks/ |
| Core plugins: `ICorePlugin`, the context, `InvokeAsync` and `PluginJson`, the page, tabs, live events, `PluginEventWatch`, `GuardedRun`, choose devices first, several views, tasks from a page, downloads, live video, device web interfaces, toolbar plugins, permissions and audit, the self check | [core-plugins.md](../website/src/content/docs/dev/core-plugins.md) | https://oadm.app/dev/core-plugins/ |
| Settings and records, the server's network interfaces, ports and the firewall, status and rate limits, trust anchors, adding devices, services between plugins, protected tags | [host-services.md](../website/src/content/docs/dev/host-services.md) | https://oadm.app/dev/host-services/ |
| Actions and their fields, placeholders, recipients and recipient sessions, publishing and subscribing to events | [event-rules.md](../website/src/content/docs/dev/event-rules.md) | https://oadm.app/dev/event-rules/ |
| Compliance check types | [compliance-checks.md](../website/src/content/docs/dev/compliance-checks.md) | https://oadm.app/dev/compliance-checks/ |
| Devices at a site: `siteSupport`, network checks, listeners and relays, banned APIs | [sites.md](../website/src/content/docs/dev/sites.md) | https://oadm.app/dev/sites/ |
| The call context: `caller.*` and `ctx.*`, jobs, event audiences, objects per group, the canary | [device-groups.md](../website/src/content/docs/dev/device-groups.md) | https://oadm.app/dev/device-groups/ |
| Shared controls, theme classes, layouts, the page header, validation, buttons, grids, icons, wording | [ui.md](../website/src/content/docs/dev/ui.md) | https://oadm.app/dev/ui/ |
| Fakes, headless UI tests, scale tests, real cameras | [testing.md](../website/src/content/docs/dev/testing.md) | https://oadm.app/dev/testing/ |
| Debugging, logs, a plugin that does not show up | [debugging.md](../website/src/content/docs/dev/debugging.md) | https://oadm.app/dev/debugging/ |
| SDK versions, `minSdkVersion`, what is shared, the SDK history | [compatibility.md](../website/src/content/docs/dev/compatibility.md) | https://oadm.app/dev/compatibility/ |
| Licenses for plugins you sell | [licenses.md](../website/src/content/docs/dev/licenses.md) | https://oadm.app/dev/licenses/ |
| Writing a plugin with an AI assistant | [ai-plugins.mdx](../website/src/content/docs/dev/ai-plugins.mdx) | https://oadm.app/dev/ai-plugins/ |
