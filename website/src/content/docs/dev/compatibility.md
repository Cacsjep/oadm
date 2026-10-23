---
title: SDK versions and compatibility
description: Which OADM a plugin runs on, what the SDK version means and how the SDK grows without breaking plugins.
---

A plugin is built against the SDK of one OADM release (the NuGet packages of that version) and runs on the OADM versions
that SDK is compatible with. This
page says what OADM checks, what it promises today and what you do before you update.

## The SDK version

The SDK has its own version, `SdkInfo.Version` in `src/Oadm.Sdk/Plugins/Plugins.cs` (today `0.3.0`), separate from the
OADM version. Your `plugin.json` names the SDK it needs (the project SDK writes the version you build with):

```json
"minSdkVersion": "0.3.0"
```

When the server starts, it compares the two. A plugin that needs a newer SDK than the server has is not loaded; the
server log says why:

```text
Plugin problem in plugins/acme.hello: Plugin acme.hello requires SDK 0.4.0, host has 0.3.0.
```

The client loads the client part of a plugin without this check, but without its server part such a page or dialog has
nothing to talk to.

The shared assemblies (below) always come from OADM, whatever version your plugin was built against, so the plugin
folder loads on every OADM whose SDK `minSdkVersion` allows. The `oadm` tool is stricter: on Windows,
`deploy --installed` and `debug --installed` refuse an installed OADM older than the release of the packages you build
with; on Linux and macOS they only warn. To support older releases, build with the packages of the oldest one; use
members that are newer only with the check below.

## How the SDK grows

New members of the SDK interfaces come with a body in the SDK (default interface members). That keeps both directions
working:

| You | OADM | What happens |
|---|---|---|
| Implement an interface (`ITaskPlugin`, `ICorePlugin`) built with an older SDK | Newer | Your plugin loads; the new members use their default |
| Call a member of an interface OADM implements (`ICorePluginContext`, `ITaskExecutionContext`) | Older, without that member | The default answers: null, an empty list or `NotSupportedException`, as the member's comment says |

In the [SDK reference](../../api/) such members carry **Default implementation**. When your plugin uses one that may be
missing on an older OADM, check for it:

```csharp
if (ctx.EventStreams is not { } streams)
{
    return; // an older OADM: no event streams
}
```

## What is shared and what your plugin brings

OADM provides these at runtime. Compile against them, never copy them into your plugin folder; the project SDK
`Oadm.Plugin.Sdk` references them that way, in the versions of its OADM release:

| Part | Shared assemblies |
|---|---|
| Server part | `Oadm.Sdk`, `Microsoft.Extensions.*` (logging, options) |
| Client part | `Oadm.Sdk`, `Oadm.Sdk.Client`, Avalonia, `CommunityToolkit.Mvvm`, `Microsoft.Extensions.Logging*`, SkiaSharp, HarfBuzzSharp |

Everything else your plugin uses (a NuGet package for MQTT, a PDF library) goes into its folder. Each plugin folder loads
in a context of its own, so two plugins can use different versions of the same library.

## The SDK today

- The SDK is version 0.x: it still grows with OADM releases, mostly by new members with defaults. Until 1.0 a release
  can also change a member; rebuild your plugin against each new release.
- The NuGet packages (`Oadm.Sdk`, `Oadm.Sdk.Client`, `Oadm.Sdk.Testing`, `Oadm.Plugin.Sdk`, `Oadm.Plugin.Templates`,
  `Oadm.Tools`) come with every OADM release, all in its version. A checkout of the repository builds the same.

## SDK history

| SDK | OADM | What changed |
|---|---|---|
| 0.3.0 | 4.1 | OADM PRO and plugin services: `ICorePluginContext.Pro` (`IProLicense`, `ProLicenseInfo`, `ProLicenseTexts`), `"pro": true` in `plugin.json`, `ICorePluginContext.Services` (`IPluginServices`: `Offer`, `Withdraw`, `Find`), `IDeviceCertificateAuthority` (PKI's CA), `ITrustAnchors.SetRenewalIssuers`. Task dialogs: `ITaskDialogContext.CanRunTasks` and `RunTaskAsync` (`TaskRunState`, `TaskRunStatus`). Records: `ICorePluginContext.Records` (`IPluginRecords` with `GetAsync`, `MemoryPluginRecords`, `PluginRecordRules`). Network: `IPluginNetwork.ListenTcpAsync` with the addresses of one interface, `InterfaceOptions.All` / `AllId` / `AllLabel` / `IsAll`. Testing: `FakeProLicense`, `FakePluginServices`, `FakeCoreContext.PluginRecords`, `FakePluginNetwork.LastTcpAddresses` |
| 0.2.0 | 4.0 | Devices at a site: `DeviceStatus.SiteOffline`, `IDeviceInfo.SiteId` / `SiteName`, `IDeviceNetwork` (`ITaskExecutionContext.Network`, `ITaskQueryContext.Network`), `ICorePluginContext.Network` and `Sites` (relays), `siteSupport` / `sitePorts` in `plugin.json` (the project SDK writes `full` by default), `ITaskPlugin.SiteSupport`, `IComplianceCheckType.SiteSupport`, `IRelay.ReleaseAsync`, `{site}` placeholder, `DeviceMessages.Refusal(IDeviceInfo)` / `TaskSupportReasons.ForStatus(IDeviceInfo)` (they say "Site offline: <site>") and `TaskSupportReasons.ForSiteSupport`, `HostPages.Sites`, `HostPermissionKeys.SitesView`. Client: `ICorePluginClientContext.OpenDeviceRequested`, `CheckChoice` / `ICheckChoice` / `CheckChoiceList` (`ComboBox Classes="multiSelect"`), `PageTab.Count` and fixed tabs (no `CloseCommand`), `DeviceTexts.OpenNotAllowed`. Testing: `FakeDevice.SiteId` / `SiteName`, `FakeDeviceNetwork`, `FakeSites`, `FakePluginNetwork`, `InMemoryUdpRelay` / `InMemoryTcpRelay`. The project SDK adds the banned-API analyzer (warning RS0030 for own sockets). See [Sites](../sites/) |
| 0.1.0 | 3.x | The first SDK on NuGet |

A plugin built with the 4.1 packages (SDK 0.3.0) needs OADM 4.1 or later; one built with the 4.0 packages (SDK 0.2.0)
needs OADM 4.0 or later. A plugin built with SDK 0.1.0 still loads on 0.2.0 or later, but its `plugin.json` has no `siteSupport`: OADM keeps it away from
devices at a site until you rebuild it with the new project SDK (or add `"siteSupport": "full"` to your own
`plugin.json`).

## Before you update OADM

1. Read the release notes (`CHANGELOG.md`, and the release on GitHub) for SDK changes.
2. Change the version in the `Sdk="Oadm.Plugin.Sdk/..."` of your projects, the `Oadm.Sdk.Testing` reference of your tests
   and `.config/dotnet-tools.json` to the new release, and build: the compiler shows what changed.
3. Run your tests, then try the plugin with `dotnet oadm dev start`.
4. The project SDK writes `minSdkVersion` into `plugin.json`: the SDK version of the packages you build with. To support
   older OADM releases, build with their packages.
