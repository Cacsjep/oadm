# Plugin SDK on NuGet: spec

Goal: anyone with the .NET SDK creates, builds, runs, debugs, tests and ships an OADM plugin without a checkout of this
repository, with any editor, on Windows, Linux and macOS. Everything is built from this repository and published with
each release; the repository stays the single source (decision 2026-10-17). User guide: website
`dev/plugin-sdk.md` (Developers > Develop with the .NET SDK).

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
dotnet tool restore                           # the oadm tool of the same version (.config/dotnet-tools.json)
dotnet build                                  # the plugin folder .oadm/plugins/<id>/
dotnet test                                   # tests with Oadm.Sdk.Testing
dotnet oadm dev start                         # a portable OADM server here, own data; restarts after a build
dotnet oadm dev client                        # the client against it, with the plugin
dotnet oadm deploy --installed                # into the installed OADM (one admin prompt), service restarted
dotnet oadm pack                              # acme.door-overview-1.0.0.oadmplugin for customers
```

## Packages (nuget.org, job `nuget` of the release workflow)

| Package | From | Contents |
|---|---|---|
| `Oadm.Sdk` | `src/Oadm.Sdk` | The server part of the SDK |
| `Oadm.Sdk.Client` | `src/Oadm.Sdk.Client` | Dialogs, pages, toolbar, shared controls; depends on the client's Avalonia and MVVM toolkit versions |
| `Oadm.Sdk.Testing` | `src/Oadm.Sdk.Testing` | `StepRun` (formerly `tests/Shared`), `FakeCamera`, `FakeDevice`, `FakeSettings`, `FakeTaskContext`, `FakeActionContext`, `FakeCoreContext` |
| `Oadm.Plugin.Sdk` | `src/Oadm.Plugin.Sdk` | MSBuild project SDK: `<Project Sdk="Oadm.Plugin.Sdk/x.y.z">` (`Sdk/`, `targets/`; not `build/`, NuGet would import it into every referencing project) |
| `Oadm.Plugin.Templates` | `templates/` + content generated from `samples/` | `dotnet new oadm-task`, `oadm-page`, `oadm-action`, each with tests |
| `Oadm.Tools` | `tools/Oadm.Tools` (took over `Oadm.LicenseTool`) | The `oadm` command |

All six carry the release version (`manage package nuget --version`). `minSdkVersion` is the SDK contract version
(`SdkInfo.Version`, today 0.1.0), written by the project SDK. Published through NuGet Trusted Publishing (`NuGet/login`:
the job's OIDC token for a one-hour key; repository variable `NUGET_USER` and a trusted publishing policy on nuget.org;
without the variable the job is skipped). Before the push the job creates every template, builds it and runs its tests
against the packed packages.

### Oadm.Plugin.Sdk

- `targets/Oadm.Plugin.props` (before the project): the part from the project name (`X` = server, `X.Client` = client;
  only the command line can override `OadmPluginPart`, the project file is read later), `AssemblyName` `X.Server` / `X.Client` (set before the .NET SDK defaults it), RootNamespace
  = project name, net10.0, nullable, implicit usings, `EnableDynamicLoading`, compiled bindings for the client; references
  `Oadm.Sdk` (+ `Oadm.Sdk.Client`, Avalonia, CommunityToolkit.Mvvm for the client) with `ExcludeAssets=runtime`.
- `targets/Oadm.Plugin.targets` (after the project): `plugin.json` copied when the project has one, else written by the
  server part to `obj/oadm/plugin.json` from `OadmPluginId`, `Version`, `OadmDisplayName` (default the project name),
  `OadmDescription`, `OadmEnabledByDefault` (default true), `OadmLicensePublicKey`, `OadmLicenseFeatures` ("a;b"),
  `OadmLicenseTrialDays`, and `minSdkVersion`; host packages (Avalonia, DataGrid, WebView, MVVM toolkit) never copied,
  `Avalonia.Controls.DataGrid` without a version gets the host's; after every build the output goes to
  `$(OadmDeployRoot)<id>/` (default `../.oadm/plugins/`, the templates set `.oadm/plugins/` of the solution folder;
  `OadmSkipPluginDeploy` skips it).
- `targets/Oadm.Plugin.Versions.props`: the host's versions (SDK contract, target framework, Avalonia, DataGrid, MVVM
  toolkit); `PluginSdkTests` compares them with `SdkInfo.Version` and `Oadm.Client.csproj`.
- In this repository the samples and the plugins of `manage new` import the same files through `Oadm.Plugin.Repo.props`
  / `.targets` (`OadmSdkFromSource`: project references to `src/`, deploy to `artifacts/plugins/`); the samples skip the
  deploy. So a template differs from its sample only in the Sdk attribute and the package references.

### Templates

`tools/Oadm.NewPlugin --templates <folder> --version <v>` (`Templates.cs`, run by `manage package nuget` into
`artifacts/templates`) writes one template per sample: the projects without the `Oadm.Samples.` prefix (sourceName =
the sample name), the plugin parts on `Oadm.Plugin.Sdk/<v>`, the SDK project references of the tests as package
references of `<v>`, a `.slnx`, `Directory.Build.props` (net10.0, `OadmDeployRoot`), `.gitignore` (bin, obj, .oadm),
`.config/dotnet-tools.json` (oadm.tools `<v>`), a README; symbols `id` (default `community.<name in kebab case>`) and
`displayName` (default the name). `-n` must be a C# name (letters and digits): it becomes the class names. Never edited
by hand. The tutorials show the samples' code transformed the same way
(`website/src/components/templateCode.ts`).

## The `oadm` tool

| Command | What it does |
|---|---|
| `oadm dev start [--port 5090] [--from <folder or ZIP>] [--server-version v]` | The portable server of the tool's release (downloaded once from the GitHub release, checked against `SHA256SUMS.txt`, cached in `<LocalApplicationData>/Oadm/dev-cache/<version>/`) in this terminal: `--Oadm:DataDir=.oadm/server-data`, `--Oadm:ListenUrl=https://localhost:5090`, `--Oadm:PluginDir=` every `.oadm/plugins/*`; state in `.oadm/dev-server.json`. A build that changes the files the server loads (not `*.Client.*`; compared by name, size and time after 2.5 s of quiet) restarts it. The first administrator is created in the client (loopback, no setup code) |
| `oadm dev client [--port] [--from] [--client-version]` | The portable client: `--server localhost:<port>`, `--data .oadm/client-data`, `--plugin-dir` per plugin folder. No fake mode: the fake server lists only the bundled plugins |
| `oadm dev stop / status / logs [--lines 50]` | Ends the server of `.oadm/dev-server.json`, its state, the tail of its log |
| `oadm deploy --installed [--plugin <id>]` | The client part into this user's client plugin folder (no admin rights), then elevated (Windows `runas`, output through a log file; Unix `sudo`): service stopped, plugin copied into the server's plugin folder (Unix: root, `go-w`), download mark removed (Zone.Identifier, quarantine), service started |
| `oadm deploy --to <folder>` | Copies the plugin folders into any plugin folder |
| `oadm debug --installed` | Elevated: service stopped, the installed server run in the console with the service's arguments and environment plus `--Oadm:PluginDir`; service started again when it ends (Ctrl+C) |
| `oadm folders` | The workspace's, the installed server's and client's folders |
| `oadm pack [--out] [--configuration Release]` | A build into a temporary `OadmDeployRoot`, one `<id>-<version>.oadmplugin` (ZIP of the plugin folder) per plugin |
| `oadm license keys / sign` | Vendor keys and license keys (`manage license` runs these) |
| `oadm doctor` | .NET 10 SDK, the built plugins, the development server and port, the cache, the installed server and client |

- **Paths are never guessed, and installed programs are never run to ask** (decision 2026-10-17, after an older
  installed server without `--print-folders` started on the live data folder when asked): the service definition is read
  from the service manager (`sc qc OadmServer`; `systemctl show oadm-server` ExecStart and Environment plus
  `/etc/default/oadm-server`; the LaunchDaemon plist through `plutil`) and the data folder follows the server's own
  rules (`--Oadm:DataDir`, env `Oadm__DataDir`, `appsettings.json`, env `OADM_DATA_DIR`); the client keeps the user's
  plugins in `<LocalApplicationData>/Oadm/plugins`. `--print-folders` stays for people and support.
- `deploy --installed` and `debug --installed` refuse an installed OADM older than the tool (Windows reads the file
  version; Linux and macOS only warn; a pre-release of the same version counts as older); `doctor` reports it. Plugin ids
  must be plain folder names (`Cli.IsSafeId`) before anything is copied; a plugin is copied next to its folder first and
  swapped in, so a file in use leaves the old one. The service is started again only when it ran, also when the window
  is closed (SIGHUP); a service that does not stop within 60 s stops the run before anything is copied. The elevated run
  starts the dotnet host of the running runtime (an elevated environment may not find a per-user .NET) and sends its
  output and errors back through a file. `dev stop` ends only processes that are the development server.
- On Windows a console run of the installed server (debug) finds the service's TLS key in the machine key store and keeps
  it (`ServerTlsCertificate`): a new certificate would make every client refuse the server.
- The workspace is the nearest folder upward with `.oadm/`, else with a solution file.
- Debugging: attach to `Oadm.Server` / `Oadm.Client`; both load the plugin's main assembly from memory with its pdb, so
  breakpoints hit and builds never meet a locked file. No `watch` or `attach` commands: `dev start` restarts by itself,
  attaching is the IDE's.

## What OADM adds

1. Done: server `--Oadm:PluginDir=<folder>` (several separated by ";") and client `--plugin-dir <folder>`
   (repeatable): load a plugin package straight from a build output, before the other folders, so it wins over an
   installed copy of the same package. The server ignores the option as a service (a service loads only admin-only
   folders).
2. Done: portable archives in every release, `OADM-Server-<version>-<rid>.zip` and `OADM-Client-<version>-<rid>.zip`
   (the publish folders with their plugins, one top folder named like the file; job `installers`, in `SHA256SUMS.txt`).
3. Done: `Oadm.Server --print-folders` and `Oadm.Client --print-folders` print JSON and exit (nothing opened or
   created): app, version, app folder, data folder, logs, `deployFolder`, `pluginFolders` in load order,
   `developmentPluginFolder`, `pluginDirs`, and for the server `service` {name, manager}.
4. Done: `src/Oadm.Sdk.Testing`, `src/Oadm.Plugin.Sdk`, `templates/`, `tools/Oadm.Tools`.
5. Done: release workflow job `nuget`.
6. Done: developer docs, the NuGet way first, the repository checkout second; the tutorials keep their code from `samples/`.
7. Done: shared assemblies (`Oadm.Sdk`, `Oadm.Sdk.Client`, Avalonia, `Microsoft.Extensions.*`, ...) resolve to the host's
   copy whatever version a plugin asks for (`PluginLoadContext.FromHost`), so a plugin built with the packages of another
   release loads when its `minSdkVersion` fits.

Later: installing a `.oadmplugin` from the Plugins page.
