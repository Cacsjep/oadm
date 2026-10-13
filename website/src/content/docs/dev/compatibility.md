---
title: SDK versions and compatibility
description: Which OADM a plugin runs on, what the SDK version means and how the SDK grows without breaking plugins.
---

A plugin is built against the SDK of one OADM checkout and runs on the OADM versions that SDK is compatible with. This
page says what OADM checks, what it promises today and what you do before you update.

## The SDK version

The SDK has its own version, `SdkInfo.Version` in `src/Oadm.Sdk/Plugins/Plugins.cs` (today `0.1.0`), separate from the
OADM version. Your `plugin.json` names the oldest SDK it needs:

```json
"minSdkVersion": "0.1.0"
```

When the server starts, it compares the two. A plugin that needs a newer SDK than the server has is not loaded; the
server log says why:

```text
Plugin problem in plugins/acme.hello: Plugin acme.hello requires SDK 0.2.0, host has 0.1.0.
```

The client loads the client part of a plugin without this check, but without its server part such a page or dialog has
nothing to talk to. Set `minSdkVersion` to the `SdkInfo.Version` of the checkout you build against.

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

OADM provides these at runtime. Compile against them, never copy them into your plugin folder, and use the versions the
built-in plugins use:

| Part | Shared assemblies |
|---|---|
| Server part | `Oadm.Sdk`, `Microsoft.Extensions.*` (logging, options) |
| Client part | `Oadm.Sdk`, `Oadm.Sdk.Client`, Avalonia, `CommunityToolkit.Mvvm`, `Microsoft.Extensions.Logging*`, SkiaSharp, HarfBuzzSharp |

Everything else your plugin uses (a NuGet package for MQTT, a PDF library) goes into its folder. Each plugin folder loads
in a context of its own, so two plugins can use different versions of the same library.

## The SDK today

- The SDK is version 0.x: it still grows with OADM releases, mostly by new members with defaults. Until 1.0 a release
  can also change a member; rebuild your plugin against each new release.
- Plugins are built inside a checkout of the repository. There is no NuGet package of the SDK yet.

## Before you update OADM

1. Read the release notes (`CHANGELOG.md`, and the release on GitHub) for SDK changes.
2. Update your checkout to the new release and build your plugin: the compiler shows what changed.
3. Run your tests, then try the plugin with `./manage.sh run dev`.
4. Raise `minSdkVersion` only when you use something new.
