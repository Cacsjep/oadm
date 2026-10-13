---
title: Plugin basics
description: Project layout, plugin.json, loading and the rules every plugin follows.
---

A plugin is a folder with a `plugin.json`, a server assembly and, when it has a UI, a client assembly. The server
loads the first, the client the second. Both come from the same folder.

## Plugin kinds

| Kind | Interface | Lives in | Shows up as |
|---|---|---|---|
| Task plugin | `ITaskPlugin` | `*.Server.dll` | An entry in the device context menu, optionally a toolbar button |
| Task dialog | `ITaskPluginDialog` | `*.Client.dll` | The window that opens before a task runs |
| Core plugin | `ICorePlugin` | `*.Server.dll` | A service on the server, usually with a page |
| Core plugin page | `ICorePluginPage` | `*.Client.dll` | A page in the navigation rail |
| Toolbar plugin | `IToolbarPlugin` | `*.Client.dll` | A control in the Devices toolbar |
| Action | `IActionPlugin` | `*.Server.dll` | A "Then" choice in the event rules |

One folder can hold several of them. The PKI plugin, for example, is one core plugin with a page that also contributes
eight task plugins to the context menu.

## Start from a working plugin

```sh
./manage.sh new task --name "My plugin"      # or page, or action; PowerShell: .\manage.ps1 new ...
```

This creates the plugin of the matching tutorial under your name in `plugins/` and `tests/` and adds it to the
solution: it builds, its tests pass, and it runs. The tutorials explain every file:
[task plugin](../tutorial-task/), [plugin page](../tutorial-page/), [event rule action](../tutorial-action/).

## Project layout

Plugins are built inside a checkout of the repository: they reference the SDK projects, which the host ships. There is
no NuGet package of the SDK yet.

```text
plugins/
  Oadm.Plugins.Hello/                 server part
    Oadm.Plugins.Hello.csproj         AssemblyName Oadm.Plugins.Hello.Server
    plugin.json
    HelloTaskPlugin.cs
  Oadm.Plugins.Hello.Client/          client part (optional)
    Oadm.Plugins.Hello.Client.csproj  AssemblyName Oadm.Plugins.Hello.Client
    HelloDialog.cs
tests/
  Oadm.Plugins.Hello.Tests/
```

Add every project to `Oadm.sln`. The build then picks them up, and so does `manage build plugins`.

### plugin.json

```json
{
  "id": "acme.hello",
  "version": "1.0.0",
  "minSdkVersion": "0.1.0",
  "displayName": "Hello",
  "description": "Says hello to devices.",
  "enabledByDefault": true,
  "alwaysOn": false
}
```

| Field | Meaning |
|---|---|
| `id` | Unique and stable. It names the folder, the settings and the permissions. Never change it |
| `version` | Your plugin's version |
| `minSdkVersion` | The oldest SDK your plugin works with (`SdkInfo.Version`). An older host skips the plugin and logs why |
| `displayName`, `description` | The row on the **Plugins** page: the name and one short sentence |
| `enabledByDefault` | `false`: off until an administrator turns it on. Use it for anything that runs in the background |
| `alwaysOn` | `true`: it cannot be turned off. Reserved for core functionality |

### Server project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>Oadm.Plugins.Hello.Server</AssemblyName>
    <RootNamespace>Oadm.Plugins.Hello</RootNamespace>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <OadmPluginId>acme.hello</OadmPluginId>
    <OadmPluginOutputDir>$([MSBuild]::NormalizeDirectory('$(MSBuildThisFileDirectory)', '..', '..', 'artifacts', 'plugins', '$(OadmPluginId)'))</OadmPluginOutputDir>
  </PropertyGroup>

  <ItemGroup>
    <!-- The SDK comes from the host: compile against it, never copy it. -->
    <ProjectReference Include="..\..\src\Oadm.Sdk\Oadm.Sdk.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>

  <ItemGroup>
    <None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <!-- Copy the output to artifacts/plugins/<id>/, where a checkout loads plugins from. -->
  <Target Name="OadmDeployPlugin" AfterTargets="Build" Condition="'$(OadmSkipPluginDeploy)' != 'true'">
    <ItemGroup>
      <_OadmPluginFiles Include="$(OutDir)**\*" />
    </ItemGroup>
    <Copy SourceFiles="@(_OadmPluginFiles)"
          DestinationFiles="@(_OadmPluginFiles->'$(OadmPluginOutputDir)%(RecursiveDir)%(Filename)%(Extension)')"
          SkipUnchangedFiles="true" />
  </Target>
</Project>
```

The client project is the same with `AssemblyName` `Oadm.Plugins.Hello.Client`, the same `OadmPluginId`, and these
references instead (all shared by the client host, so none of them is copied):

```xml
<PackageReference Include="Avalonia" Version="12.1.4" ExcludeAssets="runtime" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" ExcludeAssets="runtime" />
<ProjectReference Include="..\..\src\Oadm.Sdk.Client\Oadm.Sdk.Client.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>
<ProjectReference Include="..\..\src\Oadm.Sdk\Oadm.Sdk.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>
<!-- Optional: share the payload types with the server part. It then ships in the same folder. -->
<ProjectReference Include="..\Oadm.Plugins.Hello\Oadm.Plugins.Hello.csproj" />
```

Copy the versions from a built-in plugin's project file; they follow the host.

## How plugins load

- The server looks in `plugins/<id>/` next to its exe, in `<data folder>/plugins/<id>/` and, in a checkout, in
  `artifacts/plugins/<id>/`. The client looks in the same three places.
- Each assembly loads in its own load context. Your private dependencies (NuGet packages the host does not have) go into
  the plugin folder with it.
- The loader finds every public class that implements a plugin interface and has a public parameterless constructor.
  Contributed task plugins and actions come from their core plugin instead.
- A plugin that fails to load or throws is logged and skipped. It never stops the server or the client.
- Administrators turn plugins on and off on the **Plugins** page. A plugin that is off has no page, menu entry or toolbar
  button, and its core plugins are stopped. Tasks already running finish.

To try a plugin, build it and start OADM from the checkout:

```sh
./manage.sh build plugins
./manage.sh run dev
```

To install it on a running OADM, copy the folder `artifacts/plugins/<id>/` into `plugins/` next to the server and the
client (or into the data folder's `plugins/`) and restart both.

## Rules every plugin follows

These rules hold for the built-in plugins too. Pull requests that break them are not merged.

- **Device safety.** Check the device's API versions before the first write, validate every input before you touch the
  device, never lock OADM out (never remove or demote the account OADM uses), and write the setting that could cut the
  connection last. Details in [Task plugins](../tasks/#device-safety).
- **Secrets.** Payloads live in memory only. Never log or store a password; store secrets only encrypted
  (`ctx.Secrets`).
- **Steps and names.** Every device request and every wait is a named step. The task name says what the task does.
- **Scale.** A site can have 5,000 devices. Lists are virtualized, searches are one pass, server work for many devices
  runs with bounded parallelism, and every device list has a test with 5,000 fake devices.
- **Look.** Shared controls and theme only, errors directly below the field, one accent button per view, red for
  destructive actions. See [UI kit](../ui/).
- **Wording.** Short and plain, no protocol names in UI text, button labels without "...".
- **Tests.** Every non-trivial class has tests with fakes. No test needs a network or a device. See [Testing](../testing/).
- **Documentation.** Every plugin has a `README.md` in its folder: decisions, page or dialog, steps, the device API it
  uses and what was verified on which device.
