# Plugin SDK on NuGet: spec

Goal: anyone with the .NET SDK creates, builds, runs, debugs, tests and ships an OADM plugin without a checkout of this
repository, with any editor, on Windows, Linux and macOS. Everything is built from this repository and published with
each release; the repository stays the single source (decision 2026-10-17).

Reference: Milestone's MIP SDK (XProtect plugins, same architecture: one folder per plugin, loaded by the server and the
clients). It ships NuGet packages and Visual Studio templates, but leaves deploy and debugging to the developer: a
hand-written post-build `xcopy` into Program Files, Visual Studio as administrator, services and clients stopped and
started by hand (locked files), debugging the Event Server by attaching to the service, downloaded DLLs blocked by
Windows, a fixed install path, .NET Framework 4.7. OADM supports the installed product the same way and automates
those steps.

## The developer's day

```
dotnet new install Oadm.Plugin.Templates
dotnet new oadm-page -n DoorOverview          # or oadm-task, oadm-action
cd DoorOverview
oadm dev start                                # a portable OADM server on this machine, own data folder
dotnet build                                  # builds and deploys into the dev server
F5                                            # server (and client) with the plugin from the build output
dotnet test                                   # tests with Oadm.Sdk.Testing
oadm deploy --installed                       # into the installed OADM (one admin prompt), service restarted
oadm pack                                     # DoorOverview-1.0.0.oadmplugin for customers
```

## Packages (nuget.org, published by the release workflow)

| Package | From | Contents |
|---|---|---|
| `Oadm.Sdk` | `src/Oadm.Sdk` | The server part of the SDK |
| `Oadm.Sdk.Client` | `src/Oadm.Sdk.Client` | Dialogs, pages, toolbar, shared controls; names the client's exact Avalonia version |
| `Oadm.Sdk.Testing` | `src/Oadm.Sdk.Testing` (new, from `tests/Shared`) | `StepRun`, fake devices and VAPIX, headless page setup |
| `Oadm.Plugin.Sdk` | `src/Oadm.Plugin.Sdk` (new) | MSBuild project SDK: `<Project Sdk="Oadm.Plugin.Sdk/x.y.z">` |
| `Oadm.Plugin.Templates` | `templates/` (new, generated from `samples/`) | `dotnet new oadm-task`, `oadm-page`, `oadm-action`, each with tests |
| `Oadm.Tools` | `tools/Oadm.Tools` (new, takes over `Oadm.LicenseTool`) | The `oadm` command (`dotnet tool install -g Oadm.Tools`) |

Versions follow `SdkInfo.Version`; the packages and `minSdkVersion` agree by construction. Published through NuGet
Trusted Publishing (no key in GitHub).

### Oadm.Plugin.Sdk

- Target framework, references to `Oadm.Sdk` / `Oadm.Sdk.Client` without copying them into the output (the host has them).
- `plugin.json` written from the project: `OadmPluginId`, `Version`, `OadmDisplayName`, `Description`,
  `OadmEnabledByDefault`, `minSdkVersion` from the referenced SDK, optional license (`OadmLicensePublicKey`,
  `OadmLicenseFeatures`, `OadmLicenseTrialDays`).
- Output names `<Name>.Server.dll` / `<Name>.Client.dll`; the plugin folder layout in the build output.
- After a build: deploy into the dev server (when it runs), `oadm pack` layout on publish.

## The `oadm` tool

| Command | What it does |
|---|---|
| `oadm dev start / stop / status / logs` | Downloads the portable OADM server and client matching the SDK once (cached), runs the server in console mode with a dev data folder, creates the first administrator, prints the address and login |
| `oadm dev client [--fake]` | Starts the client against the dev server (or in fake mode for pages without cameras) |
| `oadm deploy` | Into the dev server: copy, restart |
| `oadm deploy --installed` | Into the installed OADM: one admin prompt (UAC / sudo), stop the service, copy into its plugin folder, remove the "downloaded from the internet" mark, start the service; the client part into the installed client's plugin folder |
| `oadm debug --installed` | Stops the installed service and starts the installed server in console mode with the service's data folder and the plugin from the build output (`--Oadm:PluginDir`), so F5 / breakpoints work; starts the service again when it ends |
| `oadm attach` | Finds the server process and prints the steps for Visual Studio, Rider and VS Code |
| `oadm watch` | Rebuild on change, restart the dev server |
| `oadm pack` | The `.oadmplugin` file (ZIP of the plugin folder) |
| `oadm license keys / sign` | Vendor keys and license keys (today `manage license`) |
| `oadm doctor` | .NET SDK, SDK and OADM versions, ports, service state, locked files, rights |

Paths are never guessed: the tool asks the installed server and client where their folders are (`--print-folders`).

## Debugging

- Template launch profiles: "Server" (dev server, or the installed server through `oadm debug --installed`, with
  `--Oadm:PluginDir=<build output>`) and "Client" (`--plugin-dir <build output> --server localhost:5080`).
- The plugin runs in the debugged process, so breakpoints hit from the first line (no attach timing as in MIP).

## What OADM adds

1. Done: server `--Oadm:PluginDir=<folder>` (several separated by ";") and client `--plugin-dir <folder>`
   (repeatable): load a plugin package straight from a build output, before the other folders, so it wins over an
   installed copy of the same package. The server ignores the option as a service (a service loads only admin-only
   folders).
2. Done: portable archives in every release, `OADM-Server-<version>-<rid>.zip` and `OADM-Client-<version>-<rid>.zip`
   (the publish folders with their plugins, one top folder named like the file; job `installers`, in `SHA256SUMS.txt`).
3. Done: `Oadm.Server --print-folders` and `Oadm.Client --print-folders` print JSON and exit (nothing opened or
   created): app, version, app folder, data folder, logs, `deployFolder` (server: the data folder's plugins; client: the
   user's plugins folder, no admin rights), `pluginFolders` in load order, `developmentPluginFolder`, `pluginDirs`, and for
   the server `service` {name, manager}. The tool reads the service's arguments and environment from the service manager
   and runs the installed server with them, so the answer is the service's own folders.
4. `src/Oadm.Sdk.Testing`, `src/Oadm.Plugin.Sdk`, `templates/`, `tools/Oadm.Tools`.
5. Release workflow job `nuget`: pack and publish the six packages with the tag.
6. Developer docs: the NuGet way first, the repository checkout second; the tutorials keep their code from `samples/`.

Later: installing a `.oadmplugin` from the Plugins page.
