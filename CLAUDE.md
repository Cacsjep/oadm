# OADM - Open AXIS Device Management

Open source, cross-platform alternative to AXIS Device Manager (ADM). Users coming from ADM
must feel at home: same workflows, same information, but our own modern dark Fluent look.

- Name: **OADM**, root namespace `Oadm`, folder `odam`.
- License: Apache-2.0 (`LICENSE` at repo root).
- Main branch: `main`.
- Language of UI and code: English.

Every rule here is agreed with the maintainer; change one only after asking.

# Tech Stack

| Area | Choice |
|---|---|
| Runtime | .NET 10 LTS, C# 14 |
| Server | Generic Host worker service + Kestrel, gRPC (Grpc.AspNetCore), EF Core + SQLite |
| Client | Avalonia 12, Fluent theme, dark (default) or light, CommunityToolkit.Mvvm |
| Client <-> Server | gRPC with protobuf contracts. Server-streaming for live device and task updates |
| Logging | Serilog (console + rolling file) behind Microsoft.Extensions.Logging; log files roll daily or at 20 MB (server) / 10 MB (client), only the last 14 days and at most 30 files are kept |
| Tests | xUnit, NSubstitute for mocks |
| Device API | VAPIX only (HTTP/HTTPS, digest auth). No ONVIF |
| Live video | RTSP (server, own client) relayed as encoded H.265/H.264 over gRPC, decoded in the client with FFmpeg (LGPL, bundled) |
| Embedded browser | `Avalonia.Controls.WebView` (MIT; WebView2, WKWebView, WPE WebKit / WebKitGTK of the OS), in the client host for the Web View plugin |

Cross-platform is a day-one requirement: Windows, Linux, macOS. No Windows-only APIs
(no DPAPI, no registry, no WMI). Paths via `Path.Combine`, data folder via
`Environment.SpecialFolder.LocalApplicationData/Oadm` unless `--Oadm:DataDir=<folder>` or env
`OADM_DATA_DIR` sets it (the installed services always do, see Packaging). Every release must build on all three
(CI matrix: `ci.yml` and `package.yml` run only for release tags `v*.*.*`) and the server must run as a plain console process on all three.

# Packaging

Server and client publish as one self-contained single-file exe per platform
(`PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract`, `PublishReadyToRun`), see
`manage publish`. Every plugin project is published once into `artifacts/publish/plugins/<plugin id>/`
(`<OadmPluginId>`, server and client part together) and copied next to both exes in
`plugins/<plugin id>/`, so a new plugin project is packaged without further changes.
`--version` (default `0.1.0-dev`, a leading `v` is removed) goes to every assembly (`-p:Version`).
After the publish `manage publish` runs `tools/Oadm.Notices` (generator, never shipped): it reads the published
`*.deps.json` files (server and client from `obj/Release/<tfm>/<rid>/`, every plugin folder) and writes
`THIRD-PARTY-NOTICES.txt` next to each exe: the hand-written `THIRD-PARTY-NOTICES.md` (FFmpeg, Inter, .NET), every NuGet
package and runtime pack with license, copyright, project and where it is used (server, client, plugin x), the full
license texts from `packaging/notices/licenses/` (MIT, Apache-2.0, BSD-2/3-Clause, OFL-1.1, LGPL-2.1; Apache-2.0,
LGPL-2.1 and OFL-1.1 always) and the license and notice files the packages ship (root `LICENSE*`/`NOTICE*`/
`THIRD-PARTY-NOTICES*`, the nuspec license file, `legal/**`), deduplicated by content; a license without a text is a
warning. `LICENSE.txt`, `NOTICE.txt` (the root `NOTICE`: "Copyright 2026 The OADM contributors"; `LICENSE` keeps the Apache appendix template unchanged), `LGPL-2.1.txt` and `TERMS.md` are written next to
each exe too (MSI folders, the .pkg app's `Contents/Resources`, .deb `/usr/share/doc/<package>/`); the About page starts
its license sentence with the same copyright line.
**Terms of use** (`/TERMS.md`, plain-language disclaimer, "as is", no liability for device
changes, not affiliated with Axis; text fixed by the user) are accepted on the **first start of the client** on each
computer, before the splash and any connection (`Shell/TermsWindow` + `TermsViewModel`: the terms and the license below them, Quit / Accept, the X
quits; `ClientSettings.AcceptedTerms` = SHA-256 of the text, so changed terms ask again). The installers show no license
page (decision 2026-10-13: silent and command-line installs skipped it, Debian packages never ask; every user runs the
client). TERMS.md also stands at the top of the client's About page (embedded resource `Oadm.Client.TERMS.md`), in
`/usr/share/doc/<package>/` and in every publish folder / installed app folder.

Installers (`manage package <windows|linux|macos> [--rid] [--version]` into `artifacts/packages/`; layout
table and notes in `packaging/README.md`). Every installer offers **"Server and client"** (default) or **"Client
only"** and ships `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` and `LGPL-2.1.txt` with each app. CI is one workflow,
`.github/workflows/release.yml`, only on release tags `v*.*.*` (e.g. `v0.0.1`): job `tests` (Windows, Linux, macOS,
`dotnet test` with the unit filter `Category!=Hardware&Category!=Perf&Category!=HardwareWrite`, one run without a retry,
`--blame-hang-timeout 5m --blame-crash`: a crashed test host leaves a dump and its test sequence, uploaded as the artifact
`test-results-<os>` when the step fails (`pr.yml` the same as `test-results`), 30 min timeout) -> job `installers` (needs tests;
builds the installers (the .deb packages for amd64 and arm64); installs, checks and removes the .deb packages as "Server and client" and as "Client
only"; checks the .pkg's choices and payload without installing) -> job `smoke-windows` (needs installers, 25 min: the MSI
installed, checked and removed both ways by `packaging/windows/smoke.ps1`, which prints the time on every line and a
heartbeat with free memory and the busiest processes while a program runs; its own job because the Windows runner
sometimes stops answering there, so a re-run repeats only this job) -> job `release` (needs all; the GitHub
release of the tag with the installers, the portable ZIPs `OADM-Server|Client-<version>-<rid>.zip` and `SHA256SUMS.txt`)
-> job `nuget` (needs release and `nuget-pack`; skipped without the repository variable `NUGET_USER`: `dotnet nuget push`
with NuGet Trusted Publishing (`NuGet/login`, `id-token: write`, no API key in GitHub)). Job `nuget-pack` (needs tests,
before the release): `manage package nuget` with the tag's version, every template created, built and tested against
only these packages (`packageSourceMapping` `Oadm.*` to the local folder), uploaded as the artifact `nuget-packages`. The
portable ZIPs are their own artifact (`portable-<version>-<rid>`; smoke-windows downloads only the MSI). Plugin
SDK: "Loading and packaging" and `website/src/content/docs/dev/plugin-sdk.md`:
- **Windows MSI** (WiX Toolset 6 via the `WixToolset.Sdk` MSBuild SDK, `packaging/windows/`, not in
  Oadm.sln; WiX 7 not used, it requires the OSMF EULA), x64 only (no ARM), per machine: `Program Files\OADM\Server`
  and `\Client`, Windows service "OADM Server" (`OadmServer`, automatic, LocalSystem, restart on failure,
  `--Oadm:DataDir="%ProgramData%\OADM"`), Start menu shortcut, two firewall rules for `Oadm.Server.exe`, TCP 5080 and (on branch `feature/sites`) TCP 5081 for site nodes, profiles
  Domain and Private (`Profile="[OADM_FW_PROFILE]"` = 3), fixed UpgradeCode + MajorUpgrade (same version reinstall
  allowed). Features `Client` (always) and `Server` (service, firewall rule, data folder); own UI `WixUI_Oadm`
  (welcome, page "Choose what to install" with the radio buttons "Server and client" / "Client only" =
  AddLocal / Remove of `Server`, ready, progress, finish; Change in Apps and Features opens the same page, upgrades keep
  the choice through MigrateFeatureStates); silent `ADDLOCAL=Client` = client only. `%ProgramData%\OADM` is
  created for SYSTEM and Administrators only and never removed. Files are harvested from the publish folders.
- **Linux .deb** (`dpkg-deb`, `packaging/linux/build-deb.sh`, amd64 and arm64 (job `installers` on `ubuntu-24.04-arm`,
  installed and checked there like amd64), built on Linux or in a container), two
  packages: `oadm-server` (`/opt/oadm/server`, systemd unit `oadm-server.service` (root, `Type=notify`,
  `Restart=on-failure`, `OADM_DATA_DIR=/var/lib/oadm`, StateDirectory 0700, optional `/etc/default/oadm-server`),
  maintainer scripts: postinst enables and starts it like dh_installsystemd and works without systemd; prerm stops it;
  data kept even on purge), `oadm-client` (`/opt/oadm/client`, `/usr/bin/oadm-client`, `oadm.desktop` + hicolor icon,
  X11 libraries as Depends, `libwebkit2gtk-4.1-0` as Recommends for the Web View); both files in one `apt install` = "Server and client", `oadm-client` alone = "Client
  only". No metapackage (without an apt repository it only added a third file; the `oadm` of
  0.0.3..1.0.0 is removed on upgrade through `Breaks: oadm`). Both take over the files of the former single package `oadm`
  (`Replaces: oadm (<< 0.0.3~)`, `Breaks: oadm`). Each has `/usr/share/doc/<package>/{copyright,THIRD-PARTY-NOTICES.txt,
  LGPL-2.1.txt}`. Pre-release versions are written `1.2.0~rc.1`. No .rpm.
- **OADM Site Node** (branch `feature/sites`; `manage package site-node [--rid win-x64|linux-x64|linux-arm64]
  [--version]`, details in `packaging/README.md`): `OADM-SiteNode-<version>-win-x64.msi` (WiX 6,
  `packaging/windows/site-node/`; `Program Files\OADM Site Node` on the system PATH, service "OADM Site Node"
  `OadmSiteNode` automatic, its own virtual account `NT SERVICE\OadmSiteNode`, restart on failure (also after a stop with
  an error: a part of the node that fails ends it with exit code 1), data folder `%ProgramData%\OADM Site Node` for SYSTEM
  and the service SID only and kept, Start menu shortcut, no firewall rule, silent `SERVER=<address>`; the main project no longer
  picks up .wxs files by itself), `oadm-site-node_<version>_<arch>.deb` (service + `/usr/bin/oadm-site-node`, user
  `oadm-site-node`, `CAP_NET_BIND_SERVICE`, `RuntimeDirectory`, `NoNewPrivileges`, `/var/lib/oadm-site-node` 0700) and
  `oadm-site-node-app_<version>_<arch>.deb` (Depends oadm-site-node of the same version, Recommends pkexec, icon
  `oadm-site-node.png`); amd64 and arm64; notices `tools/Oadm.Notices --product site-node` (no LGPL part); no macOS
  package, no portable ZIP. "Wait for the server" adds the Windows Firewall rule "OADM Site Node (TCP 5081)" through the
  administrator process that sent the change (`Admin/NodeFirewall`); uninstall removes it. CI: job `installers` builds,
  installs, checks and removes the node .deb packages, `smoke.ps1 -Mode SiteNode` the node MSI.
- **macOS .pkg** (`pkgbuild` + `productbuild`, `packaging/macos/build-pkg.sh`, x64 (Intel) only, built on macOS
  only): `/Applications/OADM.app` (Info.plist, icns, license texts and plugins in `Contents/Resources`), server in
  `/Library/Application Support/OADM/server`, LaunchDaemon `com.oadm.server` (root, `OADM_DATA_DIR=/Library/Application
  Support/OADM`, `OADM_SERVICE=launchd`, folder 0700), `uninstall-oadm.sh` next to the server and in the app's
  Resources. Choices (`customize="allow"`): `com.oadm.choice.client` "OADM client" (always) and
  `com.oadm.choice.server` "OADM server" (LaunchDaemon + server; uncheck for "Client only", or
  `installer -applyChoiceChangesXML`). Unsigned (executables ad hoc signed); Developer ID signing and notarization are a
  later step.
- The server runs as root / LocalSystem (NTP and DHCP plugins need UDP 123 and 67). As a service the host
  integrates with the SCM and systemd (`Oadm.Server.Hosting.ServiceHosting`: `AddWindowsService`, `AddSystemd`,
  content root = app folder; no-ops on a console). `ServiceHosting.IsRunningAsService()` (Windows service, systemd, or
  macOS with `OADM_SERVICE=launchd`; `OadmServerHostOptions.ServiceMode` overrides it, tests) switches on the service
  hardening of "Security and access": folder checks (`FolderGuard`), netsh firewall rules for plugins, no
  development plugin folder. Services set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a folder
  only the service account can write (`%ProgramData%\OADM\runtime`, `/var/cache/oadm`,
  `/Library/Application Support/OADM/runtime`), never a shared temp folder.
- Version from the tag (`v1.2.0` -> `1.2.0`), else `0.1.0-dev`; MSI and .pkg use the numeric part.
- A tag with a pre-release part (`v3.5.0-rc.1`) makes a GitHub pre-release that never becomes the latest release, so the
  website's download buttons (`/releases/latest`) and "latest" links stay on the last real release; the job `nuget`
  is skipped for it (nuget.org keeps every version forever; `nuget-pack` still builds and tests the packages).
- Icons: the app icon is `icon/oadm-app-icon-<size>.png` (16-1024); `packaging/icons/make-icons.py`
  builds `oadm.ico` (client exe, MSI, shortcuts), `oadm.icns` (macOS), `oadm.png` / `oadm-256.png` (Linux, window icon of
  every client window) from it.
Supply chain and security: every GitHub Action is pinned by its full commit hash (the version as
a comment); `.github/dependabot.yml` opens weekly pull requests for the Actions (new hashes) and the NuGet packages
(grouped: Avalonia, gRPC, Microsoft, Serilog, test packages; ignored: JsonSchema.Net 9+ and WixToolset 7+, whose license
moved to the OSMF EULA); vulnerable transitive npm packages of the website are pinned with `overrides` entries in
`website/package.json` (postcss-selector-parser 7.1.6; katex 0.18.9 under mermaid 11) until their parents update; Dependabot alerts and security updates, private
vulnerability reporting and secret scanning with push protection are on; `SECURITY.md` says how to report; `codeql.yml`
runs CodeQL (C#) on pushes to main, pull requests and weekly; `pr.yml` builds and unit-tests every pull request on Linux; changes only in `website/`, `docs/`, Markdown files (`*.md`) or the release and Pages workflows skip CodeQL and the .NET build and tests
(`pr.yml` still reports its required check, with the steps skipped); rulesets on `main`: no force push or deletion (everyone), changes through a pull request with
the passing check "tests ubuntu-latest" (administrators bypass); minimal issue templates (bug, feature) and pull request
template (one line); checkout runs with `persist-credentials: false`, workflow permissions
are `contents: read` except the release job (`contents: write`), the nuget job (`id-token: write`) and the Pages deploy
job (`pages: write`, `id-token: write`).
Website: landing page and user docs in `website/` with Astro Starlight (MkDocs Material is in
maintenance mode), own domain https://oadm.app/ (DNS at world4you: four A records to GitHub Pages, `www` CNAME `cacsjep.github.io`; custom domain and Enforce HTTPS in the Pages settings; the old cacsjep.github.io/oadm addresses redirect), published by `pages.yml` on pushes to main that change
`website/` and after every successful `release` run (`workflow_run`, so the static download links
match the new release; the browser also re-reads the latest release with `cache: 'no-store'`) (deploy job `pages: write`, `id-token: write`); npm versions pinned exactly, Dependabot npm group `website`;
screenshots in `website/src/assets/screens/` come from the headless tests (`OADM_SCREENSHOT_DIR`), click to enlarge; dark only (no theme switch); the landing page has its own slim top bar instead of the docs header (`TopBar.astro`: GitHub, LinkedIn, Discord and YouTube left (the docs header the same), Documentation and Bug report right (a menu: Support Forum https://support.oadm.app/ for users, GitHub issue for developers); phones narrower than 40rem see icons only, fixed, no border), its download buttons link the latest release files (Windows, Linux, macOS, each icon in the system's color; Linux is a menu x64 / ARM64, each = both .deb of its architecture), below them the intro video (`IntroVideo.astro`, `public/intro.mp4` with the preview image `intro-poster.jpg` and a play button in the middle; loaded on click, with sound and controls), below them a **Support Forum** button (the same link) and **Subscribe to newsletter**, which opens an email field that posts to the forum's `POST https://support.oadm.app/api/portal/subscribe` (double opt-in by mail, no forum account; the forum (`G:\oadm-portal`, PocketBase) must list `https://oadm.app` in `--origins`); the page checks the forum on every visit (`src/lib/forum.ts`: `GET /api/health`, 2 s; any answer counts) and while it does not answer greys out Support Forum, the newsletter and the Bug report menu's Support Forum entry with "Currently not available. We are working on it."; below them **Learning**: YouTube channel (https://www.youtube.com/@oadm-ca) and Documentation (`start/install`); texts follow "Wording"; search: the landing title names "AXIS Device Manager alternative", the page `start/axis-device-manager-alternative` compares with ADM (the same work, more in OADM, "Not in OADM yet": several servers in one client, firmware lookup, warranty data; no claims about ADM), `StructuredData.astro` (SoftwareApplication JSON-LD); **Changelog** (Project > Changelog, `project/changelog`) is written from
`CHANGELOG.md` by `website/scripts/changelog.mjs` before every build and dev start (npm `prebuild` / `predev`, git-ignored;
`pages.yml` also runs on changes of `CHANGELOG.md`). **SDK reference**
(Developers > SDK reference, `/api/`): `tools/Oadm.ApiDocs` reads the built `Oadm.Sdk` and `Oadm.Sdk.Client` with Roslyn
(their doc comment files: `GenerateDocumentationFile` in both projects, CS1591 off, never published:
`PublishReferencesDocumentationFiles` false) and writes one page per public type (signature with nullability, members,
XAML properties of controls, enum values, links between types and to the .NET docs, "Used by", the guide page) into
`website/src/content/docs/api/` and the sidebar into `website/src/generated/api-sidebar.json` (both git-ignored; the site
builds without them). `manage build docs` locally, `pages.yml` before every deploy (also on changes of the SDK or the
generator). Overrides, record and record struct plumbing are left out; a partial property of a source generator gets its
written comment from the source. Tests `tests/Oadm.ApiDocs.Tests`: every public type has a page, **every type, member and
XAML property has a description** (a new SDK member without a comment fails), every link and anchor resolves.
**Diagrams**: ```mermaid blocks render in the browser (`astro-mermaid` 2.1.0 + mermaid 11, before Starlight in the
integrations, theme `base` dark with the site colors); used for the architecture, plugin kinds, task runs, event rules
and the tutorials. Developer pages also cover debugging (`dev/debugging.md`) and SDK versions (`dev/compatibility.md`);
the admin page Plugins has "Plugins from others" (plugins run with the server's rights). **Tutorials** (Developers: "Tutorial: Task plugin", "Plugin page", "Event rule action", `dev/tutorial-*.mdx`): one page
each with every file, self-contained (no jumping to other pages), NuGet first (`dotnet new oadm-task|page|action`, the
`oadm` tool; the checkout's `manage new` in an aside); the code is imported raw from `samples/` (`?raw` + Starlight
`Code`; `vite.server.fs.allow` includes the repository) and shown as the templates write it
(`website/src/components/templateCode.ts`: no `Oadm.Samples.` prefix, `Sdk="Oadm.Plugin.Sdk/x.y.z"`), so the pages always
show code that builds and passes its tests. Developers > **Develop with the .NET SDK** (`dev/plugin-sdk.md`): the packages,
the templates, the project SDK's properties, the development server, debugging, deploy into the installed OADM, the
`.oadmplugin` file, every `oadm` command. **`manage new task|page|action --name "<name>" [--id <id>]`**
(`tools/Oadm.NewPlugin`): copies that sample into `plugins/Oadm.Plugins.<Name>(.Client)` and
`tests/Oadm.Plugins.<Name>.Tests` with the sample's name, namespaces, id (default `community.<kebab>`) and display name
replaced, test references pointed at `plugins/`, a `Directory.Build.props` / `.targets` in each plugin project folder that
imports the plugin project SDK from source (`src/Oadm.Plugin.Sdk/targets/Oadm.Plugin.Repo.*`), and adds the projects to
Oadm.sln (with the dotnet the script found); existing folders are never overwritten; names that would clash in code (Task,
Plugin, ...) are refused. `Oadm.NewPlugin --templates <folder> --version <v>` (`Templates.cs`) writes the content of the
template package from the same samples. Tests `tests/Oadm.NewPlugin.Tests` (`ScaffoldTests`, `PluginSdkTests`: the
templates, and `Oadm.Plugin.Versions.props` against `SdkInfo.Version` and `Oadm.Client.csproj`). A change of a sample
changes its tutorial, what `manage new` creates and the templates: keep the tutorial text in line.

Developer commands: one entry point per shell at the repo root, `./manage.sh` (bash) and
`.\manage.ps1` (PowerShell 5.1/7), verb + target (`build`, `run`, `test`, `publish`, `package`, `new`, `license`, `clean`,
`info`), `manage <verb> help` at every level. `manage package nuget [--version]` writes the plugin SDK packages to
`artifacts/nuget` (templates generated into `artifacts/templates` first); `manage license` runs `oadm license` of
`tools/Oadm.Tools`. Both read their help texts from
`scripts/manage-help.txt`; any new verb, target or option goes into both scripts and that file.
The plugins (`build`, `run` server / client) are built in one MSBuild run in parallel through a generated solution filter
`artifacts/plugins.slnf` (every `plugins/*/*.csproj`, which must be in `Oadm.sln`).
Native AOT and trimming are deliberately NOT used for the host apps: AOT cannot load managed
plugin assemblies at runtime and trimming removes framework APIs plugins rely on. Small
helper tools without plugin loading may use Native AOT.
Code must never use `Assembly.Location` (empty in single-file); use `AppContext.BaseDirectory`.

The client bundles the FFmpeg decoder libraries (avcodec, avutil, swscale, swresample; FFmpeg 9,
LGPL-2.1 build from the `DevEnvy.FFmpeg.Binaries.LGPLv2.Runtime.<rid>` package, bindings
`FFmpeg.AutoGen`). They are native shared libraries embedded in the single-file exe and extracted
by the .NET host at startup (`DOTNET_BUNDLE_EXTRACT_BASE_DIR`); the publish folder contains only
the exe. They add about 57 MB to the win-x64 exe. `OADM_FFMPEG_DIR` loads a user-supplied build
instead (LGPL replaceability), `Oadm.Client --check-decoder` verifies the decoder without a
window. Licenses and source offer: `THIRD-PARTY-NOTICES.md` (hand-written part of the generated
`THIRD-PARTY-NOTICES.txt`, see above). Never add GPL or nonfree FFmpeg builds.

# Solution Layout

```
Oadm.sln
src/
  Oadm.Contracts/      protobuf files + generated gRPC stubs, shared enums
  Oadm.Sdk/            plugin SDK: interfaces, attributes, context objects, Layout/TileLayout (tile grid rules). No Avalonia, no EF
  Oadm.Sdk.Testing/    fakes for plugin tests (NuGet Oadm.Sdk.Testing): StepRun, FakeCamera, FakeDevice, FakeSettings,
                       FakeTaskContext, FakeActionContext, FakeCoreContext
  Oadm.Plugin.Sdk/     MSBuild project SDK of plugins (NuGet Oadm.Plugin.Sdk, Sdk/ + targets/): assembly names, SDK
                       references without copies, plugin.json from properties, the plugin folder; Oadm.Plugin.Repo.* for
                       plugins inside this repository; Oadm.Plugin.Versions.props = the host's versions
  Oadm.Sdk.Client/     client-side plugin SDK: dialog/page/toolbar interfaces, ITaskDialogContext,
                       IToolbarContext, shared controls (Controls/: IconLabel, SearchBox, OadmIcon,
                       DialogTitleBar, CardHeader, DialogFooter, StatusChip, StatCard, FileRow, ProgressRow, LoadingOverlay,
                       ToolbarButton, ToolbarSeparator, ToolbarOverflowPanel, PasswordBox, MessageWindow, FormField, DevicePickerWindow, DeviceIcon,
                       GroupedChoice, DeviceTree, PageTab, TileGrid, PageDeviceChoice, ProgressRing, InfoIcon,
                       WizardSteps, CodeView, CodeEditor), LiveVideo (ILiveVideoAccess), DeviceTexts, ServerErrors, PluginEventWatch,
                       form validation (Validation/: ValidatingViewModel, FormValidator), Network/InterfaceSelection,
                       Collections/RangeObservableCollection (bulk list of the host grids and plugin pages)
  Oadm.Core/           domain model, VAPIX client, discovery, task engine, persistence (EF Core)
  Oadm.Server/         host: gRPC services, plugin loader, polling, Serilog setup
  Oadm.Client/         Avalonia app: views, view models, gRPC client, plugin loader
  Oadm.Theme/          the OADM theme (colors of both appearances, icons, OadmTheme.axaml, OadmColors) and the shared
                       TermsWindow, used by the client and the OADM Site Node app
  Oadm.Sites/          the link between server and site node (feature/sites): Kestrel over a connection the node opened,
                       TLS with pinned fingerprints, pairing code and SRP binding
  Oadm.SiteNode/       OADM Site Node service and command line (feature/sites; README with the manual test plan)
  Oadm.SiteNode.Admin/ the node's local channel, texts and parsing shared by service, command line and app
  Oadm.SiteNode.App/   the OADM Site Node app (Avalonia)
plugins/                (layout and SDK guide: plugins/README.md)
  Oadm.Plugins.Restart/   first Task plugin (server only)
  Oadm.Plugins.Firmware(.Client)/         Upgrade firmware
  Oadm.Plugins.Users(.Client)/            Users (accounts on the devices)
  Oadm.Plugins.DateTime(.Client)/         Date and time
  Oadm.Plugins.Network(.Client)/          Network settings, Assign IP address
  Oadm.Plugins.Acap(.Client)/             Applications (ACAP) and their license keys
  Oadm.Plugins.SnapshotReport(.Client)/   first Core plugin: rail page + PDF maintenance report
  Oadm.Plugins.VapixCommander(.Client)/   core plugin: VAPIX command library, raw requests, rollouts
  Oadm.Plugins.NtpServer(.Client)/        core plugin: NTP server (RFC 5905 server mode) + "Use OADM as NTP server"
  Oadm.Plugins.DhcpServer(.Client)/       core plugin: DHCP server (RFC 2131) with static leases and lease list
  Oadm.Plugins.MetadataMonitor(.Client)/  core plugin: live event stream of one camera (RTSP metadata, port of AXIS Metadata Monitor, off by default)
  Oadm.Plugins.Pki(.Client)/              core plugin: PKI (one CA for device certificates, trusted root store)
  Oadm.Plugins.SystemReport(.Client)/     core plugin without a page: toolbar "System report" (server reports in one ZIP)
  Oadm.Plugins.HardeningScan(.Client)/    core plugin: read-only scan against the AXIS OS hardening guide (off by default)
  Oadm.Plugins.ImageHealth(.Client)/      core plugin: Image Health Dashboard, AXIS Image Health Analytics status of the chosen cameras (off by default)
  Oadm.Plugins.DeviceConfig(.Client)/     Maintenance: Export / Import configuration, Configure devices, Create configuration file
  Oadm.Plugins.Outputs/                   event rule action "Set output" (relays, outputs)
  Oadm.Plugins.Notifications/             event rule actions "Send email", "Send HTTP request" and "Send TCP message"
  Oadm.Plugins.IoMonitor(.Client)/        core plugin: live inputs and outputs of one device (off by default)
  Oadm.Plugins.DeviceTriggers(.Client)/   core plugin: events of the devices for the event rules (WebSocket, off by default)
  Oadm.Plugins.WebView(.Client)/          core plugin: the web interface of the devices in tabs, logged in through the server
  Oadm.Plugins.LiveView(.Client)/         core plugin: saved views of several cameras (a grid of live videos), opened in tabs (off by default)
  Oadm.Plugins.Dashboards(.Client)/       core plugin: live values of events (device triggers) and live video on saved dashboards (off by default)
  Oadm.Plugins.EdgeRecordingExport(.Client)/ core plugin: downloads the recordings on the cameras' SD cards to the client (off by default)
  Oadm.Plugins.EdgeStorage(.Client)/      core plugin: the SD cards of the chosen cameras and their tools (format, check, encryption; off by default)
  Oadm.Plugins.ActionRules(.Client)/      core plugin: Action Rule Editor, the devices' own action rules changed for many at once (off by default)
  Shared/                                 source linked into several plugins (CachedApiCheck, DeviceSweep, SweepRows,
                                          ActionEngine: the devices' action rules over SOAP, DeviceEvents: a device's event list)
  Oadm.Plugins.<Name>/          server part: Oadm.Plugins.<Name>.Server.dll + plugin.json
  Oadm.Plugins.<Name>.Client/   optional Avalonia part: Oadm.Plugins.<Name>.Client.dll
                                (both copy their output to artifacts/plugins/<plugin id>/)
tests/
  Oadm.Core.Tests/
  Oadm.Server.Tests/
  Oadm.Client.Tests/
  Shared/              RecordingFirewall (linked into plugin tests; StepRun moved to src/Oadm.Sdk.Testing)
tools/                 Oadm.DiscoveryProbe (manual discovery tests), Oadm.Notices (THIRD-PARTY-NOTICES.txt generator),
                       Oadm.ApiDocs (SDK reference of the website), Oadm.NewPlugin ("manage new", the templates),
                       Oadm.Tools (the "oadm" command, NuGet Oadm.Tools; "manage license")
samples/               the plugins of the developer tutorials: Oadm.Samples.UpnpName (task with a dialog),
                       Oadm.Samples.FirmwareOverview (core plugin with a page), Oadm.Samples.VirtualInput (event rule
                       action), each with its tests (Oadm.Sdk.Testing); on the plugin project SDK from source (plugin.json
                       from their properties); in Oadm.sln, built and tested by CI, never deployed
                       (samples/Directory.Build.props sets OadmSkipPluginDeploy)
templates/             Oadm.Plugin.Templates.csproj: packs the templates generated from samples/ (not in Oadm.sln)
packaging/             installers (windows/, linux/, macos/), icons/, notices/licenses/ (license text templates)
docs/                  specs of features not built yet (sites.md); built features live in this file
```

# Architecture

Two processes, like ADM:

- **Oadm.Server** owns the truth: device table, credentials, task table, discovery, polling,
  plugin execution. Listens on `https://0.0.0.0:5080` (gRPC over TLS) by default, configurable. Clients log in; the
  token travels in gRPC metadata, never in messages (see "Security and access").
- **Oadm.Client** is a thin UI: no device logic, no VAPIX calls. Everything goes through gRPC.
  It subscribes to `DeviceService.Watch` and `TaskService.Watch` streams (with snapshot end marker; the
  task snapshot is limited to `TaskStore.MaxTasks` = 10,000 plus the active tasks) and keeps an
  in-memory mirror for the grids. Stream changes reach the UI thread through a coalescing
  `ChangeBatcher` (one dispatcher post per burst) and are applied to the stores as batches.
- Server change feeds (devices, tasks) deliver every change in order while a watcher keeps up; beyond
  1,024 waiting changes they coalesce per entity (`KeyedCoalescingChannel`: the latest snapshot wins,
  an Added stays Added), so a slow client never loses a final state and never blocks the server.
- gRPC responses are gzip-compressed (live view frames excepted); server `MaxReceiveMessageSize` 16 MB,
  client 32 MB.

## gRPC services (Oadm.Contracts)

- Every call needs `authorization: Bearer <token>` in the metadata (plus `oadm-client-machine`), except
  `AuthService.Status`, `Login`, `CreateFirstAdmin`; UNAUTHENTICATED without a valid token, PERMISSION_DENIED with what the
  role lacks (`Oadm.Server.Auth.AccessPolicy`, see "Security and access", Roles and permissions).
- `AuthService` (`auth.proto`): `Status` (server name, version, `needs_first_admin`, `setup_code_required` when the
  caller is not loopback), `Login(user, password, remember)` -> {token, UserInfo} (UNAUTHENTICATED one message for
  wrong password / unknown / disabled user, RESOURCE_EXHAUSTED while locked), `CreateFirstAdmin(user, password,
  setup_code, remember)` (FAILED_PRECONDITION users exist, PERMISSION_DENIED setup code, INVALID_ARGUMENT), `Logout`
  (revokes the call's token), `Me`, `WatchSession` (the caller's user with role and permissions now and after every
  change). `UserInfo` carries `role` (the two-value role of older clients: ADMIN for Administrator), `role_id`,
  `role_name`, and for the caller `permissions` + `all_permissions`. `UserService` (Admin): `List`, `Add` (`role_id`, else
  `role`), `Update` (optional `role_id` / `role` / disabled, new password; a password or disabling ends the user's
  sessions, a role applies to the next call), `Delete`; FAILED_PRECONDITION for the own account or the last enabled
  administrator. `AuditService` (Admin): `List(limit, offset)` newest first + `total_count` (limit 0 = 10,000).
- `RoleService` (`roles.proto`, Admin except `Mine`): `GetMatrix` (every `PermissionInfo` {key, name, description, group,
  icon_key, operator_default, viewer_default, full_device_control, available} in matrix order and every `RoleInfo` {id,
  name, description, kind, permissions, user_count, version}), `SaveRoles(roles)` (create = empty id, change; one transaction;
  FAILED_PRECONDITION for the Administrator role and for a change whose `version` is older than the stored role (two
  administrators: "Another administrator changed the role X meanwhile. Click Discard and make your changes again.";
  version 0 = no check), INVALID_ARGUMENT names / over 50 roles), `DeleteRole(id)` (custom roles
  without users), `Mine` (the caller's role name and its permissions with names). `ServerSettings.roles_notice = 22`
  (administrators only): what operators lost with the upgrade to roles.
- `DeviceService`: `List` (legacy: one message with every device, 23 MB for 5,000 devices with their
  API lists; clients do not use it), `Watch(WatchDevicesRequest)` (stream of DeviceChanged events: one
  ADDED per device, then with `snapshot_end_marker = 1` one `SNAPSHOT_END` (kind 4, no device), then live
  changes; the request is wire compatible with the former Empty), `Remove` and `SetCredentials` (one
  transaction for all ids), `Refresh`, `GetWebUiUrl`, `LogIn(device_ids, user_name, password,
  save_to_credential_list)` (the context menu's "Log in", see "Main Window"; Operator: per device
  (`Oadm.Core.Devices.DeviceLoginService`, 8 at a time, 15 s each) the stored address, scheme and pinned certificate with the
  new credential, authenticated `basicdeviceinfo`, the serial must be the record's; CertificateChanged devices are refused
  without a request; the accepting devices get the credential in one transaction, their cached client dropped and a full
  refresh queued (that sets Ok); reply `results` {device_id, ok, rejected, message: "The user name or password is
  wrong.", "Unreachable - ...", "Another device answers at this address.", "Certificate changed - accept the new
  certificate first"} in request order + `credential_list_note`; save to the credential list only for an Admin caller
  and only when a device accepted it, a full list is the note "The credential list is full (20 entries); the login was
  not saved there.", never a failed login; audit "Device login" {user name, "5 devices, 3 logged in"}; INVALID_ARGUMENT
  without devices, user name or password), `GetCredentialUserName(DeviceIds)` (the stored user name the devices share,
  else empty: prefill of the dialog), `SetFirstPassword(device_ids, password)` (the context menu's "Set password", see
  "Main Window"; Operator: per device (`Oadm.Core.Devices.DeviceFirstPasswordService`, 8 at a time, 15 s per request)
  anonymous HTTPS first (a device stored with HTTPS keeps its pin, one stored with HTTP is asked over HTTPS, then HTTP),
  the anonymous `getAllUnrestrictedProperties` serial must be the record's ("Another device answers at this address.
  Nothing was changed."), systemready must say `needsetup=yes` right before writing (else "The device already has a
  password. Nothing was changed.", nothing sent), then pwdgrp.cgi add root exactly like the add page (POST body only);
  a refusal is "The device rejected the password: <device text>."; CertificateChanged devices are refused without a
  request; the devices that took it get root + password stored in one transaction, their client dropped and a full refresh
  queued; reply `results` {device_id, ok, message} in request order; audit "First password set" {"root", "3 devices, 2
  set"}, never the password; INVALID_ARGUMENT without devices or for a password that is not 1-64 printable ASCII),
  `GetPassphrasePolicies(DeviceIds)` (anonymous systemready per device: `policies` {device_id, policy none / length /
  complex, empty when unreadable}; the dialog's hint).
- `DiscoveryService`: `StartZeroConf`, `StartRangeScan(from, to)`, `ProbeAddress(address, user_name = 2,
  password = 3, expected_serial = 4)` (one entered IP or host name, optional port/scheme; INVALID_ARGUMENT for an unusable or
  unresolvable address, only one of user name (1..64) and password (1..256), or an expected serial that is not 12 hex;
  the optional credentials of an imported line belong to that session only: tried first, then the credential list,
  server memory only, never saved to the list, never logged; `DiscoveryAuthenticator.AddSessionCredential`; with
  `expected_serial` (a line of an ADM export, `SetExpectedSerial`) a device with another serial gets no password at all:
  UNREACHABLE "Another device answers at this address (X, expected Y). No password was sent.", RetryAuth refused), `WatchDiscovered` (stream; every device with its automatic login result
  `auth_state = 14` (PENDING, AUTHENTICATED, PASSWORD_NOT_SET, LOGIN_FAILED, UNREACHABLE,
  ALREADY_ADDED), `auth_user_name = 15`, `credential_id = 16` ("list:<id>",
  "entered"), `auth_detail = 17`, `passphrase_policy = 18`, `entered_address = 19`, `dns_name = 20` (the verified DNS name of the
  address, see "Add Devices Page"); every scan
  (zero-conf after `Discovery.ZeroConfSeconds`, range scans, address probes, or `StopScan`) ends with
  scan_finished and the stream ends once every login of the session finished; watching a finished
  session again replays its devices with their current login result, then streams the logins still
  running), `StopScan` (ends the scan early, the session and its devices stay; unknown ids ignored),
  `Stop` (forgets the session; the add page calls it on close).
- `AddDevicesService`: `RetryAuth(session_id, discovered_id, user_name, password,
  save_to_credential_list, related_session_ids = 6)` (returns the updated DiscoveredDevice, also pushed
  to the stream; on success the credential is tried on the failed devices of the session and of the
  related sessions, see "Add Devices Page"; NOT_FOUND, FAILED_PRECONDITION factory default / already
  added, INVALID_ARGUMENT),
  `Commit(session_id, discovered_ids, initial_passwords map = 6, initial_root_password, use_host_name = 3)` (the page's
  "Use DNS names instead of IP addresses"; reply:
  `device_ids`, `results = 3` with discovered_id, device_id, status), `Prepare` (legacy, unused).
  See "Add Devices Page".
- `TaskService`: `ListTaskPlugins(ListTaskPluginsRequest)` (context-menu entries incl. those contributed by Core
  plugins; `TaskPluginInfo.display_name` normalized by the server, `group = 8` never empty; runnable sets
  cached by `TaskPluginRunnableCache` per device-table version, at most 10 s old; with `compact = 1` a
  plugin carries the shorter of `runnable_device_ids` or `runnable_on_all_except = 9` +
  `not_runnable_device_ids = 10`: 74 KB instead of 1.8 MB for 5,000 devices x 10 plugins; `not_runnable_groups = 11`
  (`NotRunnableGroup` {reason, device_ids, count, other_devices}): why the plugin cannot run on the other devices
  (`ITaskPlugin.NotSupportedReason`, computed in the same cache), grouped by reason, most devices first, every id sent
  once: with `runnable_on_all_except` the not-runnable ids are sorted by group and each group takes its `count` of them,
  otherwise the most common reason is `other_devices` (no ids) and the rarer ones list their ids; 5,000 devices x 15
  plugins: compact reply 109 KB, the reasons of the "all except" plugins 833 bytes), `Run(pluginId, deviceIds, payloadJson)` (one task per device, reply `task_ids`;
  `task_id` is deprecated = first id; all tasks of a Run written in one transaction), `List(ListTasksRequest)`
  (newest first; `limit`/`offset` paging, `TaskList.total_count = 2`; limit 0 = all, legacy),
  `Watch(WatchTasksRequest)` (stream: snapshot of every active task plus the newest `snapshot_limit`
  (0 = all), `SNAPSHOT_END` (kind 4, no task) when `snapshot_end_marker`, then live changes), `Cancel`, `Delete`,
  `DeleteAll` (cancels and waits for active tasks, then one bulk delete), `GetLog(taskId)` (per-task log, oldest first, live while running); `TaskInfo` carries
  `repeated TaskStep steps = 13` (index, name, `TaskStepState`, detail, progress, started, finished) and
  `current_step_index = 14` (-1 none),
  `Query(pluginId, deviceId, method, payloadJson)` (read-only `ITaskPluginQuery` for task
  dialogs, 30 s timeout; NOT_FOUND, UNIMPLEMENTED, FAILED_PRECONDITION for an incompatible device,
  changed certificate or rejected credentials, UNAVAILABLE, DEADLINE_EXCEEDED, INVALID_ARGUMENT;
  the status detail is the user message).
- `FileService`: `Upload` (client stream: header {name, size} then 256 KB data chunks; returns
  id, name, size, SHA-256; INVALID_ARGUMENT, RESOURCE_EXHAUSTED over `Uploads.MaxMegabytes`, over the 10 GB of
  all uploads together or when less than 1 GB of disk would stay free, see "Security and access" 4),
  `Delete(fileId)`. Uploads live in `<datafolder>/uploads/<id>.bin` + `<id>.json`, are deleted
  after `Uploads.RetentionHours` (checked every 15 min) and reach tasks through `IUploadedFiles`.
- `PluginService`: `ListCorePlugins` (navigation pages; `CorePluginInfo.no_page = 4` for a core plugin without a page,
  `ICorePlugin.HasPage` false: the client adds no rail entry), per-plugin generic
  `Invoke(pluginId, method, payloadJson)` for Core plugin UI pages (NOT_FOUND unknown plugin or
  object, FAILED_PRECONDITION not running, INVALID_ARGUMENT for an `ArgumentException` of the
  plugin, INTERNAL otherwise; the status detail is the message), `Watch(plugin_id)` (stream of `PluginEvent`
  {plugin_id, topic, payload_json} the plugin publishes through `ICorePluginContext.Events` from the call on; NOT_FOUND
  unknown plugin; `Oadm.Core.Plugins.PluginEventHub` fans out with 256 events buffered per watcher, oldest dropped),
  `Download(InvokeRequest)` (stream of `DownloadChunk` {file_name, length (-1 unknown), data}: a file of a core plugin that
  implements `ICorePluginDownloads`, e.g. a recording export passed on from a camera; the first message carries the name
  and length, every data message at most 256 KB, uncompressed, nothing stored on the server; the permission, the audit
  (detail `PluginDownload.AuditDetail`) and the errors of Invoke; UNIMPLEMENTED for a plugin without downloads; the
  download is disposed at the end or when the client goes away; tests `PluginDownloadTests`).
  Users: "Snapshot report", "VAPIX Commander", "NTP server", "DHCP server", "PKI", "Metadata Monitor", "Hardening scan",
  "Image Health Dashboard", "I/O Monitor", "Device triggers", "Dashboards", "Edge Recording Export" (search; its downloads through
  `Download`), "System report" (from its toolbar button through `IToolbarContext.InvokePluginAsync`).
  `ListPackages` (every loaded plugin package with id, display name, version, enabled, enabled_by_default, has_page,
  menu_task_count, always_on, description; any user) and `SetPackageEnabled(id, enabled)` (Admin; NOT_FOUND unknown package; reply: the whole
  list), see "Plugins on and off".
- `SettingsService`: `Get`, `Set` (`ServerSettings.zero_conf_seconds = 8`, 0 keeps the stored value; read-only
  `server_version = 21` in both replies for the About page),
  `ListCredentials`, `AddCredential(user_name, password)`
  (INVALID_ARGUMENT, RESOURCE_EXHAUSTED over 20 entries; an identical pair returns the existing
  entry; a new entry is tried on the failed devices of every open add session), `RemoveCredential(id)` (NOT_FOUND), `RevealCredential(id)` (reply `RevealedCredential { password = 1 }`:
  the stored password of one credential list entry for the Credentials page eye and copy buttons; NOT_FOUND;
  logged as "Credential list password of <user> revealed", never the password). Credential entries carry id,
  user name and created time, never a password.
- `LiveViewService`: `Watch(device_id, max_width, max_height, fps, accepted_codecs, camera)`
  (stream of encoded access units), `ListSources(device_id)` (view areas / sensors / channels),
  `GetOptics(device_id)` (live view permission; zoom and focus state, `supported` false for a camera without them, one
  OADM cannot log in to or that does not answer, never an error), `SetOptics(device_id, one of magnification,
  focus_position, focus_window, autofocus, reset_focus)` ("Optics Control"; INVALID_ARGUMENT unless exactly one,
  FAILED_PRECONDITION with the camera's refusal; audited "Changed zoom or focus"), `GetRtspPort(device_ids)` (any user:
  the shared port, 554 = default, `mixed`) and `SetRtspPort(device_ids, port)` ("Optics Control"; 0 or 554 = default;
  stored in OADM, the devices are not changed; audited "Changed RTSP port in OADM"), `GetPtz(device_id, camera)` (live
  view permission; presets, home, move, zoom, center of a PTZ channel, `supported` false otherwise, never an error) and
  `MovePtz(device_id, camera, move, x, y, value, preset)` ("Optics Control"; not audited, a drag sends several a second;
  FAILED_PRECONDITION "The camera refused: <camera text>"). See "Live view".
- `EventService` (`events.proto`, event rules, see "Event rules").
- `ServerLogService` (`logs.proto`, permission `logs.server`): `List` (the server's log files, a page newest first), see
  "Logs page".
- `DeviceWebService` (`deviceweb.proto`, the Web View relay, see "Web View"): `Open(device_id)` (Operator; refuses
  Certificate changed, Credentials required, Password not set with the shared text; audit "Opened web interface"),
  `Exchange` (one bidirectional call per HTTP request or WebSocket of the embedded browser; the server sends it to the
  device with the stored login and the pinned certificate; FAILED_PRECONDITION for the refused statuses of Open (no
  request reaches the device) and while the Web View plugin is turned off, INVALID_ARGUMENT for a path that leaves the
  device; a user's requests to a device without an Open in the last 8 hours write the audit entry once; request bodies
  over 16 MB are spooled to `<datafolder>/web-relay/`, at most 4 at once, 1 GB of disk kept free, the folder emptied once
  per start; a `Location` to the device's host on any scheme or port becomes a path).
- `ComplianceService` (`compliance.proto`, see "Compliance"): `GetCatalog`, `ListChecks`, `WatchChecks`, `GetCheck`,
  `SaveCheck`, `DeleteCheck`, `SetCheckEnabled`, `ListChoices`, `CheckNow`, `WatchResults` (snapshot in batches of 5,000 with
  `snapshot_end`, then changes; `checking` while a result was asked for), `SetEnabled` (compliance on / off, off by default;
  `ComplianceCheckList.enabled`).
- `TagService` (`tags.proto`, device tags, see "Device tags"): `List` (every definition sorted by name with its
  `device_count`, then names used on devices without a definition, `defined = false`), `Watch` (the whole `TagList` on
  subscribe and after every change of the definitions; small, no ChangeBatcher needed), `Create(name, color)`
  (ALREADY_EXISTS, INVALID_ARGUMENT, RESOURCE_EXHAUSTED over 500 definitions; `TAG_COLOR_UNSPECIFIED` = next palette
  color), `Update(name, new_name, color)` (Admin; rename and / or recolor, empty / UNSPECIFIED keep; a rename rewrites
  every device's tag in one transaction; an undefined name gets a definition), `Delete(name)` (Admin; removes the tag
  from every device in one transaction, reply `devices_changed`), `SetDeviceTags(device_ids, add, remove)` (one call
  and one transaction for the whole selection, names in `add` without a definition are created with the next palette
  color, unknown ids ignored, RESOURCE_EXHAUSTED when a device would get more than 20 tags; reply `devices_changed`,
  `created`). Every write publishes one Updated device change per changed device.

# Data Model (EF Core, SQLite)

SQLite runs with the WAL journal (`DatabaseInitializer`) and `synchronous=NORMAL` on every connection
(`SqlitePragmaInterceptor`): a commit does not wait for an fsync, the database stays consistent after a
crash; with FULL a poll round of 5,000 devices took 22 s instead of 6 s.

- `Device`: Id (Guid), Serial (= MAC, unique, upper hex, no separators), Address,
  UseHostName (bool: Address is a name, not an IP), HostName (the verified DNS name or the typed name), Model (ProdNbr), FirmwareVersion, DhcpEnabled, HttpsEnabled,
  Dot1xEnabled, UpnpFriendlyName, ServerName, Status (enum below), Scheme (http/https),
  ProductType (raw basicdeviceinfo ProdType, e.g. "Dome Camera"), Soc (raw basicdeviceinfo Soc, e.g. "Axis Artpec-8",
  max 64, migration `DeviceSoc`; proto `soc = 29`), Category (enum below),
  HasVideo (derived from Category, not stored),
  CertFingerprintSha256 (nullable), CertNotAfterUtc (nullable), CertTrust (enum below),
  CertSubject, CertIssuer, CertNameMatches (nullable bool, address in SAN; stored, not shown
  yet), LastSeenUtc, WarrantyExpiry (nullable, later), ReplacementModel (nullable, later), Tags (JSON array of tag
  names, distinct case-insensitive and sorted, with the spelling of the definition; written only by `DeviceTagStore`;
  proto `repeated string tags = 19`; SDK `IDeviceInfo.Tags`),
  Apis (JSON column `[{id, version, name, status}]` from `apidiscovery.cgi getApiList`, written on
  every full refresh incl. the first one after add; proto `repeated DeviceApi apis = 28`),
  CredentialUserName (not stored: user name of the DeviceCredential, filled by repository reads
  for plugins; never the password).
- `DeviceCredential`: DeviceId, UserName, EncryptedPassword (AES-GCM, see Security).
- `TagDefinition` (table TagDefinitions, migration `DeviceTags`): Id (Guid), Name (1..64, trimmed, no ";" or control
  characters), NormalizedName (upper invariant, unique: names are unique case-insensitive), Color (Blue, Green, Teal,
  Amber, Orange, Red, Pink, Violet; stored as string), CreatedUtc. At most 500 definitions; a device has at most 20 tags.
  `Oadm.Core.Devices.DeviceTagStore` owns both (serialized writes, one transaction per call, rewrites read only the Tags
  column and load just the changed rows), `TagNames` the rules.
- Plugin licenses: no table; settings `Server.Id` (the server ID, a random GUID made at the first start, in backups),
  `Plugins.Licenses` (package id -> license text) and `Plugins.TrialStarted` (package id -> first day), see "Plugin licenses".
- `WaitingDevice` (table WaitingDevices, migration `WaitingDevices`): the import's waiting list, see "Waiting list".
- `CredentialListEntry` (table CredentialListEntries, migration `CredentialList`): Id (Guid),
  UserName (max 64), EncryptedPassword (AES-GCM with the entry id as associated data), CreatedUtc.
  The technician's credential list (Credentials page), at most 20 entries, tried in the order added.
- `Task`: Id, BatchId (shared by the tasks of one Run; proto `batch_id`), PluginId, Name, Status
  (Queued, Running, Done, Failed, Cancelled, DoneWithWarnings), Owner (client machine/user name),
  CreatedUtc, StartedUtc, FinishedUtc, Progress (0-100), PayloadJson (column kept but always NULL:
  payloads may carry secrets and are never persisted; the migration clears old values),
  ScheduledUtc (nullable, unused). Indexes: CreatedUtc (paging, newest first), BatchId, Status
  (migration `TaskStatusIndex`: active tasks for startup recovery and every Watch snapshot).
- `TaskDeviceResult`: TaskId, DeviceId, Status, Message (last progress message, warning or
  error), Progress. Exactly one row per task (proto `TaskInfo.device_id` = its DeviceId).
- `TaskStep` (table TaskSteps, migration `TaskSteps`): key (TaskId, Index), Name (max 200), State
  (Pending, Running, Done, Warning, Skipped, Failed; stored as string), Detail (max 1000: result, progress
  detail, skip reason or error), Progress (0-100), StartedUtc, FinishedUtc (nullable). Cascade-deleted with
  the task; rows are rewritten by index with every task snapshot (a dynamic step may shift later ones).
  At most 200 steps per task (`TaskStepList.MaxSteps`). The current step (`TaskRecord.CurrentStepIndex`,
  not stored) is the running one, else the last that started (the failed one), else the last ended.
- `TaskLogEntry` (table TaskLogEntries): Id (autoincrement), TaskId (cascade delete), DeviceId
  (nullable = task level), TimeUtc, Level (Info, Warning, Error), Message (max 2000 chars).
  At most 1000 entries per task; the 1000th says that later entries were dropped.
- `ComplianceCheckEntity` / `ComplianceResultEntity` (tables ComplianceChecks, ComplianceResults, migration
  `ComplianceChecks`): the compliance checks and the latest result per check and device, see "Compliance".
- `Setting`: Key, ValueJson.

Device status enum: `Ok`, `Unreachable`, `CredentialsRequired` (401/403), `PasswordNotSet`
(factory default), `CertificateChanged`, `Unknown`, and on branch `feature/sites` `SiteOffline` (computed, never stored:
the site node of `Device.SiteId` is not online in `ISiteStates`; `DeviceRepository` reads and `DeviceChangeFeed` show it,
writes keep the stored status; `Oadm.Core.Sites.SiteDeviceStatus`, `NoSiteStates` without sites; `SiteStatusPublisher`
publishes a site's devices in one batch when it goes offline or online and queues a full refresh after more than 5
minutes offline). Every status switch refuses it before any request with "Site offline: <site>"
(`DeviceMessages.Refusal(IDeviceInfo)`, `TaskSupportReasons.ForStatus(IDeviceInfo)`); a `SiteException` never changes a
status, fails a poll, counts towards the outage share or fails a compliance check (Not checked). Event rules:
`SiteEventSource` (group Sites): "Site offline: <site>" per site ({site}, {address}) and "Any site offline" ({count},
{site}); device status events keep their state while the site is offline. `{site}` in `ActionPlaceholders.Fixed`; an empty
fixed placeholder takes the event's value of the same name. Client: "Site offline", amber icon; fake mode sites Building B
(online) and Warehouse (offline). Tests: `tests/Oadm.Core.Tests/Sites/*` (+ Perf `SiteOfflineScaleTests`),
`tests/Oadm.Server.Tests/SiteOfflineServerTests`, `tests/Oadm.Client.Tests/SiteOfflineTests`.

Device category enum (`Oadm.Sdk.Devices.DeviceCategory`, also used by Core): `Camera`,
`Encoder`, `Speaker`, `Audio`, `Intercom`, `Radar`, `IoModule`, `DoorController`, `Other`,
`Unknown`. Mapped from ProdType in exactly one place, `Oadm.Core.Devices.DeviceCategoryMapper`:
case-insensitive keyword rules, first match wins (intercom/door station, door controller,
encoder/video server, camera, radar, speaker/horn, audio/microphone/amplifier, I/O / relay
module). Empty ProdType is `Unknown`; an unmatched one is `Other` and logged once per string.
Verified: AXIS P3265-V on AXIS OS 12.11 reports ProdType `Dome Camera` (also anonymously via
`getAllUnrestrictedProperties`). `HasVideo` is true for Camera, Encoder and Intercom.
Filled by the anonymous probe on the add page (discovered list shows the icon), on add and on
every basicdeviceinfo poll.

Certificate trust enum: `Trusted` (chain builds to a root in the server OS trust store, or to a
trust anchor of a core plugin such as the PKI CA: `ITrustAnchors`, `Oadm.Core.Vapix.TrustAnchorRegistry`),
`SelfSigned` (subject equals issuer and the only chain error is the untrusted root),
`Untrusted` (any other chain failure, e.g. private CA not in the store), `Expired` (past
NotAfter), `Unknown` (not checked yet or HTTP only). Only the chain is evaluated; the host name
is ignored because devices are usually reached by IP address.

# Device Discovery

## Zero-conf: mDNS / Bonjour only

Browse `_axis-video._tcp.local`. Verified on AXIS OS 12.11: the TXT record has exactly one
key `macaddress=<SERIAL>`, the instance name is `<Bonjour.FriendlyName> - <SERIAL>`, SRV
points to port 80 at `axis-<serial lowercase>.local`, A/AAAA gives the address. Ignore
169.254.x.x link-local addresses when a routable one is announced.
Must work on all three OS and on multiple NICs (bind one socket per interface). A scan of the add
page runs at most `Discovery.ZeroConfSeconds` (default 30, 5..300; `DiscoveryService.StartZeroConf(duration)`,
timer from the server's TimeProvider) or until `StopScan`: browsing stops, the probes already started
finish, watchers get scan_finished, the devices stay in the session until `Stop`. Without a duration
(server start, periodic re-find) it runs until stopped. No SSDP or WS-Discovery.

## Following moved devices

OADM keeps a device managed when its address changes (`Oadm.Core.Devices.DeviceAddressService`):
- **By a task** (Network plugins after setting a static address): `ITaskExecutionContext.UpdateDeviceAddressAsync`.
  The server reads basicdeviceinfo at the new address with the stored credentials, scheme and pinned
  certificate; a different or missing serial throws `DeviceIdentityException` and the record stays. Otherwise
  the record's Address changes (credentials and pin kept, Unreachable becomes Unknown), the change is published
  and a full refresh queued. Logged ("Device X moved from A to B").
- **By the periodic re-find** (`Oadm.Server.Devices.DeviceRelocationHostedService`, `DeviceRelocationOptions`):
  every 5 minutes (first run 30 s after start), only while at least one device is Unreachable and addressed by
  IP, a 15 s zero-conf session looks for those serials; a device announced at another address is verified
  there the same way and moved (`TryRelocateAsync`). This covers DHCP changes, where the new address is unknown.
- **By a DHCP lease**: when the DHCP server plugin ACKs a lease to the MAC (= serial) of a
  managed device and the address differs from the record, the plugin calls `IDeviceAutoAdd.FollowAsync`
  (`ICorePluginContext.AutoAdd`, see "DHCP server"): the server verifies the device at the leased address the same
  way and moves it right away, also while the device still counts as reachable (`TryRelocateAsync(..., requireUnreachable:
  false)`; a lease proves the change). Not verified yet (the device is still starting): one retry after 30 s. Logged
  and audited ("Device moved", user "system", "DHCP server: from A to B"). Works whether or not the automatic add is on.
- Devices addressed by a name (added by their DNS name or a typed host name) keep it; their record never moves.

## IP range scan

For every address in the range (parallelism from settings, default 32): try HTTPS 443 then
HTTP 80, `POST /axis-cgi/basicdeviceinfo.cgi` with
`{"apiVersion":"1.0","method":"getAllProperties"}`.
- `401` with `WWW-Authenticate` realm `AXIS_<serial>` -> Axis device, serial from realm.
- `200` (anonymous allowed) -> parse properties directly.
- Connection refused / timeout (default 1500 ms) -> not a device.
No ICMP, no ARP. Result goes into the same discovered list as mDNS, deduplicated by serial.

# Device Communication (VAPIX)

- Auth: AXIS OS 12 uses Digest on port 80 and Basic on port 443 (seen in
  `/config/rest/virtualhost/v1` and the 401 headers). The client offers Digest only over
  HTTP and Basic or Digest over HTTPS. Basic is never sent over plain HTTP, with one exception: the add
  page's automatic login to a device that passed the anonymous Axis check (`AllowBasicOverHttp`, see
  "Security and access" 2).
- Factory-default check without credentials: `systemready.cgi` returns `needsetup` (yes =
  no admin user yet) and `passphrasepolicy` (none, length, complex).
- Prefer HTTPS, fall back to HTTP if 443 is closed. Store which scheme worked.
- Certificates: trust on first use. Store SHA-256 fingerprint on add. A later mismatch sets
  status `CertificateChanged` and blocks tasks until the user accepts the new certificate.
- Certificate info: the TLS callback (`CertificatePinning`) also describes every presented
  certificate with a separate `X509Chain` build (system trust store, `RevocationMode.NoCheck`,
  no downloads, validity dates ignored) into `VapixClient.ObservedCertificate`; pinning and
  TOFU decisions are unaffected. Expiry is applied at refresh time (`TrustAt(now)`). Captured
  on add and on every full refresh. Devices OADM reaches over HTTP (e.g. a port forward of port 80 only, HTTPS not reachable)
  have no handshake to observe: the full refresh reads the web server certificate over VAPIX instead
  (`WebServerCertificateReader`: SOAP `GetWebServerTlsConfiguration` at `/vapix/services` names it and its CA certificates,
  REST cert v1 `certificates/<alias>` and `ca_certificates/<alias>` return the PEM; AXIS OS 11.11+, read-only, verified on
  the development camera) and rates it the same way (nothing is pinned); cleared when HTTPS is off or the API is missing. Cross-platform APIs only.
- **Old AXIS OS** (verified read-only on an AXIS Q6154-E with 8.40.104;
  `tests/Oadm.Server.Tests/Hardware/OldFirmwareHardwareTests`, opt-in with `OADM_OLD_CAMERA_URL` / `_USER` / `_PASSWORD`):
  no `apidiscovery.cgi` (404, it came with 8.50), no anonymous `getAllUnrestrictedProperties` (401: the Axis check uses the
  realm, see "Security and access" 2), no `systemready.cgi`, `time.cgi`, `ntp.cgi`, `/config`; `network_settings.cgi`
  without `getNetworkInfo`. Authenticated `basicdeviceinfo` (incl. Soc), `param.cgi`, `firmwaremanagement.cgi` (1.0),
  `pwdgrp.cgi`, `applications/list.cgi` work. `VapixClient.GetApiListAsync` answers a 404 with `LegacyApiList` from the
  `param.cgi` group `Properties` (param-cgi 1.0 always, fwmgr from `Properties.FirmwareManagement.Version`, application
  when `Properties.EmbeddedDevelopment.Version`, user-management when `Properties.API.Browser.UserGroup=yes`; status "from
  parameters"); an API list without "param-cgi", "application" or "user-management" gets them the same way from one read
  of `Properties` (VAPIX identifies these by parameters, not by the API list; an AXIS OS 11.9 camera did not list
  "application"), so Upgrade firmware, Users, Applications and (through their param.cgi fallbacks) Network settings and Date
  and time run; PKI (11.11) and Device configuration (12.5) stay greyed with their reason. Writes on such firmware are not
  tested yet; the discovered row has no model (no anonymous properties); a factory-default 8.x device is not recognized
  (no systemready).
- Endpoints of the core:
  - `basicdeviceinfo.cgi` -> SerialNumber, ProdNbr, ProdShortName, Version, HardwareID
  - `param.cgi?action=list&group=Network.BootProto,Network.UPnP.FriendlyName,Network.Interface.I0.dot1x.Enabled,HTTPS.Enabled`
    -> DHCP, UPnP name, 802.1X, HTTPS (`HTTPS.Enabled=yes|no` still exists on 12.x)
  - `pwdgrp.cgi` action add, user root, grp root, sgrp admin:operator:viewer:ptz
    -> first password on a factory-default device. Works over HTTP without credentials, but
    we send it over HTTPS when available and always with arguments in the POST body, never
    in the URL. Since AXIS OS 11.5 the first user does not have to be named root.
  - `restart.cgi` -> Restart task plugin
  - `param.cgi?action=list&group=Properties.Image,Image` -> live view codecs
    (`Properties.Image.Format`, e.g. `jpeg,mjpeg,h264,h265`), resolutions and video sources
  - RTSP `rtsp://<host>/axis-media/media.amp?videocodec=h264|h265&camera=<n>&resolution=WxH&fps=N&videokeyframeinterval=N&audio=0`
    on port 554 -> live view (see "Live view")
- All VAPIX access lives in `Oadm.Core.Vapix.VapixClient`, one method per endpoint,
  unit-tested against recorded responses in `tests/.../Fixtures`.
- **Routing** (docs/sites.md, on branch `feature/sites`): every device connection opens through
  `Oadm.Core.Sites.DeviceRouter` on the device's `DeviceRoute` (`Device.SiteId`, `DiscoveredDevice.Route`): the server's
  own network = a normal socket connect, a site = a new `ISiteTransport.OpenStreamAsync` stream per connection.
  `VapixClient` runs on `SocketsHttpHandler` (router connect callback, pinning via `CertificatePinning.ValidateSslCallback`;
  WebSockets through the same handler); `VapixDeviceProbe` keeps one handler per route (a pool keyed by host and port only
  would share a connection between two sites with the same IP) and reads the route from `DeviceRouter.RouteOption`;
  `RtspClient.ConnectAsync(..., route, router)` keeps the per-device RTSP port. `DeviceConnections.For(device |
  discovered)` is the only place that builds `VapixConnectionOptions` (test `RoutingRulesTests`). The factory's cache key
  holds the route; a change of route or address drops the cached client. Through a site: at most 2 connections per
  device, 30 s idle; every request timeout + 4 x the site's round trip; never Basic over plain HTTP; a `SiteException`
  passes through unchanged; a WebSocket upgrade on a new connection counts as a permanent dial. Names resolve on the route
  (`RoutedDns`; through a site in stream 5). With no site everything connects as before.
- **Banned APIs**: `Microsoft.CodeAnalysis.BannedApiAnalyzers` with `src/Oadm.Plugin.Sdk/targets/BannedSymbols.txt`
  (Socket, TcpClient, UdpClient, Ping, Dns, HttpClientHandler, SocketsHttpHandler, ClientWebSocket.ConnectAsync): errors in
  src/ and plugins/ (`OadmBannedApis=error`), warnings RS0030 in every plugin built with Oadm.Plugin.Sdk
  (`OadmBannedApis=warning`, `none` turns it off; version `OadmBannedApiAnalyzersVersion` in Oadm.Plugin.Versions.props);
  tools/ and tests/ outside. Exceptions carry a SuppressMessage with a justification and are listed in
  `RoutingRulesTests.AllowedBannedApiUsers` (`AddressProbe`, `NtpServerService`, `UseOadmNtpServerTask` until they use
  `IDeviceNetwork`).

# Add Devices Page (fast add, no wizard)

Goal: maximum technician satisfaction. Adding devices is one page, no steps; the server
logs in to every device it finds with the credentials the technician already has.

Toolbar (toolbar plugins, see "Toolbar plugins"): the **Add** menu entries Discovery, Network range, Add manually and
Import from file open the same dialog window (header = the entry name) (`Discovery/AddDevicesWindow`, shared dialog
controls: title bar, one card, footer) in four modes:
- **Scan**: zero-conf (mDNS) discovery starts immediately; devices appear live. The scan ends after
  `Discovery.ZeroConfSeconds` (Settings page, default 30 s, 5..300).
- **Scan IP range**: From / To inputs on top; Enter or the Scan button starts (several ranges add to
  the same list).
- One info line above the list (`ui:ProgressRow`, indeterminate for zero-conf, percent for ranges): the scan status and the
  counts in one text (`AddDevicesViewModel.StatusLine`: "Import finished, 5 devices found · 4 ready to add · 1 need a login
  · 4 selected"; while a scan runs "Searching the network... · 8 found · ..."), then a
  **Stop** button (icon stop) while a zero-conf or range scan runs (`StopScan` for every running
  session; the devices found stay) and **Scan again** (icon refresh) after it finished or was stopped
  (zero-conf: a new session; range: the same range again; found devices stay, deduplicated by serial; a
  "Checking" of the new search never hides the result or selection of an earlier one). The text then
  says "Scan finished, N devices found" or "Scan stopped, N devices found" ("1 device found"), plus ", M already added" when
  devices OADM already manages answered: those are not listed at all and not in the summary.
  Add manually has neither button.
- **Add manually**: Address input (IP or host name, optional port and scheme,
  `https://camera.example.com:8443`); Enter or Find probes that address (`ProbeAddress`); every
  address adds a row; "No Axis device answered at X." when nothing answers. The entered address
  (host[:port]) becomes the device address on add, as typed.
- **Import devices** (built-in icon button after Add manually, icon download): first the dialog "Import from file"
  (`Discovery/ImportHelpWindow` + `ImportHelpViewModel`, single operation: the columns, the example
  `DeviceImportFile.Example` in a `ui:CodeView` (the user docs show the same, test `DeviceImportFileTests`), the limits;
  Cancel / **Choose file**), then the client asks for a CSV file (open picker through `IDialogService.OpenFileAsync`) and reads it with
  `Discovery/DeviceImportFile` (O(n)): the Export devices CSV or any CSV with an address column (header names
  case-insensitive: Address, IP address, IP, Host name; optional **User name** and **Password**; other columns
  ignored), or a plain file with one address per line; comma or semicolon, UTF-8 (BOM optional), UTF-16 with BOM,
  else Latin-1; a formula guard `'` of the export is removed. A password without a user name is for `root`; a user
  name alone is no credential. The whole file is refused in `ui:MessageWindow` ("Import from file") when it is larger
  than 64 MB (a guard only: the lines are the limit), has more than 30,000 addresses (`DeviceLimits.MaxDevices`; the
  same for an ADM export), no address at all, no Address column (with a header) or is not text.
  Problems of single lines stay in their row (chip "Not added" + detail): no address, not an IP address or host
  name (http/https, port, no path), "Listed before in line N." (same address), line longer than 4,096
  characters, unclosed quote, user name > 64 / password > 256. The page (header "Import from file", "Addresses from
  <file>. Logins in the file are tried first.") shows one row per line at once in file order ("Queued"), then probes the
  addresses with `ProbeAddress(address, user, password, import_line)`, at most `MaxImportProbes` = 64 at a time (a probe holds
  its slot until its watch stream ended, i.e. its logins finished): the row becomes "Checking...", then shows
  the device it found (`DiscoveredRowViewModel.Adopt`, the line's row keeps its place) with the login result;
  "Not found: No Axis device answered at X." (also for a host name that does not resolve), "Not added: Same
  device as <address> (<MAC>)." when another line found the device; managed devices leave the list like
  everywhere. Progress row "Checking addresses: 120 of 5,000" (percent), **Stop** (lines not probed yet: "Not
  checked", neutral), then "Import finished|stopped, N devices found" (+ ", M already added"); no Scan again.
  Summary adds "N not added". Credentials of the file stay in client memory until their probe, go to the server
  only, are never logged and never saved to the credential list (the inline login editor keeps its own "Save to
  credential list"). Then the technician reviews and clicks Add as usual. Scale: 30,000 lines are listed,
  filtered and selected in one reset each (test). The first contact of an imported line waits only `Scan.TimeoutMs`
  (1.5 s, like the range scan; `ProbeAddressRequest.import_line = 5`), Add manually keeps 3 s: 30,000 lines with 10 % off
  take about 12 to 15 minutes on a LAN.

**Waiting list** (Import from file and Import from ADM: while lines did not answer, the footer shows "2 devices do not
answer yet. OADM adds them once they come online." and a secondary button **Add 4 now, 2 later** ("Add 2 later" without
checked rows; `AddWithWaitingCommand`) left of **Add 4 devices**, which adds only the checked devices): with it the lines whose device
did not answer or did not identify itself as an Axis device (`DiscoveredRowViewModel.CanWait`: "Not found" or Unreachable;
not file problems, not Login failed, not "Not checked") go to the server in batches of 5,000 (`WaitingDeviceService.Add`)
with their address, login, expected MAC address and tags, before the found devices are committed. The page reads the waiting list before it probes (`ListWaiting`, a role without the
right: none): a line that does not answer and is waiting already shows **Already waiting** ("An earlier import keeps it
waiting. OADM adds it once it answers.", neutral), is not kept again, and the finish line adds ", 12 already waiting".
Server: `Oadm.Core.Devices.WaitingDeviceStore` (table WaitingDevices, migration
`WaitingDevices`: one entry per address (`WaitingDeviceStore.Normalize`: as a device record stores it, no scheme, upper
case), an address kept again replaces login, MAC address and tags, renews its kept time and is tried like new; writes go
one at a time; password AES-GCM with the master key, entry id as associated data, never listed or logged; at most 30,000,
over it the whole call is refused "The waiting list holds at most 30,000 devices. Remove devices from it first.") and
`Oadm.Server.AddDevices.WaitingDeviceRunner` (hosted): a round every `Devices.WaitingRetryMinutes` (15, 1..1440, Settings
page "Try waiting devices every (min)", after the end of the last round, first round a minute after the start, a changed
setting applies within a minute) or at once for `TryNow`; each round reads the entries to try and, only when there are any,
drops every entry whose address or MAC address OADM manages by now (`DeviceRepository.ListAddressesAsync`, no full rows),
then tries the rest 32 at a time the import's way but with the normal first contact (`StartAddressProbeAsync(importLine:
false)`: a device behind a slow link is found), the expected MAC address and the line's login in the session, the Axis check
before any password, then the credential list, at most 10 rejected per device) and stores a device that answers with
`DeviceAdder` like the add page (the login that worked; factory default as Password not set), its tags, a full refresh and
the audit entry "Device added automatically" (user "system", "<source> (waiting list, kept by <user>): ..."), only while
its entry is still the one the round read (`IsCurrentAsync`: removed or kept again meanwhile = not added, nothing written
back; results carry the kept time they were read with); a try never throws and a failed write of results is logged without
ending the round; a device that
answers but rejects every login is marked Login failed and **not tried again by itself** (no repeated wrong passwords) until
Try now; results are written in batches of 256. Nothing drops off by itself: an entry stays until its device is added (also
another way) or a user removes it. gRPC `WaitingDeviceService` (`waiting.proto`, all "Add and remove devices"): `Add`,
`List(after, limit)` (address order with a cursor, `next_after`: entries removed while a client reads never shift a page;
`checking = 13`: asked for by Try now or in the running round and not tried yet), `Watch` (summary
{count, login_failed, trying, next_try, remaining}, at most once a second), `Remove(ids | all)`,
`TryNow(ids | all; all also tries the failed logins)`; audited "Kept devices waiting" and "Removed waiting devices". Client:
`ServerConnection` watches the summary for roles with the right into `DeviceStore.Waiting` (`WaitingDevicesState`); the
Devices page shows the link "2,412 devices waiting to be added" next to "N devices, M selected" while the list is not empty;
it opens `Devices/WaitingDevicesWindow` (`WaitingDevicesViewModel`: "Next try at 14:35", toolbar Try now, Export, Remove (red,
confirmed; needs a selection), count and search; grid Address, MAC address, Tags, Kept, Last try, Result (warning chip for a
failed login); read in pages of 5,000, read again after each round; Try now and Export take the selection, else every row
the search shows; the export is the import's format without passwords; rows being checked show the spinning "Checking..."
chip and the line above the list "Checking: 120 left"; the list is read again after Try now, when a round starts and when
it ends). Fake mode keeps the list in memory
(`FakeOadmApi.Waiting.cs`, nothing ever answers). Tests: `WaitingDeviceStoreTests` (+ Perf 30,000), server
`WaitingDeviceTests`, client `WaitingDevicesTests` (+ Perf 30,000, screenshots), `AddDevicesPageTests`.

**Import from ADM** (Add menu, `HostPages.AddImportAdm`): the same help dialog in its ADM mode (`ImportHelpViewModel.ForAdm`:
addresses, logins, tags, everything else read from the devices, OADM is one server, an amber warning that the export holds
the passwords in plain text), a `.json` picker, then `Discovery/AdmDeviceExport` reads the device export of AXIS Device
Manager (format 1.x and 2.x: `encrypted`, `fileFormatVersionMayor` (missing = 1), `data.serverInformation` (object, or a
list for several servers), `data.deviceData.devices` {id, macAddress, address, httpPort, httpsPort, deviceUserName,
devicePassword, ...}; 1.x: the password as it is, tags `deviceTag` + `deviceTagRelation`; 2.x: the password base64 of UTF-8
(not decodable: the row's problem "The password in the export cannot be read.", no login), tags `deviceTags` [{name,
devices: [ids]}]; both tag layouts read in both versions; property names case-insensitive, unknown ones ignored) into the import lines of
`DeviceImportFile` (`CheckLine`: the same checks as a CSV): the address with its port when not 443 / 80 (`https://a:8443`,
else `http://a:8080`), the login, the tags, and the MAC address as `ImportLine.ExpectedSerial`. Refused whole: not JSON or
no device list ("not a device export of AXIS Device Manager"), `encrypted: true` ("Export the devices again ... without
encryption"), a major format version other than 1 or 2, no devices. An export
naming several ADM servers asks first (`DevicesViewModel.MultiServerText`: devices only the other servers reach show Not
found). The add page says "Import from ADM" and "Devices from <file> (ADM server <name>)"; a row whose device answers with
another MAC address is "Not added: Another device answers at this address (X). The file lists Y." and the server sent that
device no password. Model, firmware, host name, product type, apiMode and securityMode of the export are not used (read
from the device). Tests `AdmDeviceExportTests`, `AddDevicesPageTests`, `FastAddTests`.

**DNS names**: Discovery and Network range have the check box **Use DNS names instead of IP addresses** (in the scan
progress row, left of Stop / Scan again; remembered per client, `ClientSettings.AddByDnsName`). The server looks up the DNS name of every found IP
(`Oadm.Core.Discovery.IDnsNameLookup`, `SystemDnsNameLookup`: reverse lookup, a full name with a domain, not ".local" or
".arpa", that resolves back to the same IP; 3 s, 16 at a time, once per device and address, never for typed addresses;
`DiscoveredDevice.DnsName`, dropped when the address changes). Checked, the Address column shows the DNS name (tooltip
"IP address x"; the search also finds the IP) and `Commit(use_host_name)` adds those devices by it
(`DeviceAdder.ChooseAddress`); a device without a DNS name is added by IP. The address a device is added with stays its
address (no switching later, the grid shows it). Add manually and Import from file keep the typed address. Tests:
`DiscoveryServiceTests` (fake lookup), `AddDevicesServiceTests`, `AddDevicesPageTests`.

List: checkbox (40 px; shown only for rows that can be added), category icon (36 px), Address (200), MAC address (150),
Model (160), Login (status, star: takes the rest so status and detail sit right after Model; rows grow so the reason
shows on up to two lines, grid class `wrapRows`), Action (auto, at the right edge). An empty list says what to do first ("Enter a range and press Scan.", "Enter an address and press Find."). Scan
and Find are filled secondary buttons. A page error (Add or discovery failed) stands directly below the list. The scan
progress bar disappears once a scan or import ended; its text stays. Problems of file lines ("Not added") use the
warning chip, devices that do not answer the error chip. Above it one row: the info line (above), Stop / Scan again and the
search box; Discovery and Network range add the DNS name check box below it. Login status per device
(`ui:StatusChip`): Checking... (accent), Authenticated (user) (ok), Password not set (warning),
Login failed (error, reason as tooltip), Unreachable (error), Added (ok, row greyed; already managed devices are not
listed, see above).
Only addable devices can be checked: authenticated ones, and factory-default ones
once a first password is entered. A device is checked by itself the first time it can be added (forum: the button was
easy to miss and Add did nothing); one the user unchecks stays unchecked. The check box column's header is a three-state
check box (`AllSelected`: all, some = dash, none of the devices that can be added; O(1) from the counters; disabled while
none can be added): a click checks every addable row the search shows, or unchecks them when all were checked. **Add** (footer, "Add 3 devices") commits the checked devices
in one click and the page always closes (no "Keep open" option).
Every password field (login editor, first password + confirm) is `ui:PasswordBox` with the eye button.

- **Login failed**: a click on the row or the "Log in" link opens the inline editor below the list
  (user name, password, "Save to credential list" checked by default on its own row, Retry, Cancel; while an inline
  editor is open its Retry / Apply is the only accent button, the footer's Add turns secondary). Retry calls
  `RetryAuth` right away (with every other session of the page as `related_session_ids`) and updates
  the row; success checks the row and closes the editor, a wrong password shows "The user name or
  password is wrong.". After a successful Retry the server tries the same credential on every other
  device of the page whose login failed; the page watches the sessions whose stream already ended
  again to receive those results.
- **Factory default**: "Set password" opens the inline password editor (new + confirm, user root,
  hint with the systemready passphrase policy: none = 1-64 printable ASCII, length = at least 15,
  complex = at least 12 with upper, lower, digit and special character; checked client side, the
  device checks again), "Use for all factory-default devices". The password stays in client memory
  until Add; the server sets it as today (pwdgrp.cgi, HTTPS when offered, POST body only) and
  stores it as the device credential. The row shows "Password ready · Change".

Server: `DiscoveryAuthenticator` (Oadm.Server/AddDevices). For every device event of a watched
session it starts the automatic login once per device state: managed serial = ALREADY_ADDED, probe
said factory default = PASSWORD_NOT_SET (plus the passphrase policy read anonymously), unreachable =
UNREACHABLE, otherwise it tries the known credentials one at a time, at most 10 per device (no
lockout risk): the credentials typed on the page (RetryAuth), then the credential list in the order
added. Passwords of managed devices are never tried on new devices. Before the first credential goes to
a scheme the device must pass the **Axis check** on it: anonymous `basicdeviceinfo
getAllUnrestrictedProperties` answering a 12-hex `SerialNumber` equal to the discovered serial and a
`ProdNbr` (once per scheme and login run). Logins go to HTTPS first, then HTTP ("Add manually" with an
entered scheme: only that one); the handler answers Digest when offered, else Basic, also over plain HTTP
for such a verified device. A device that fails the check gets no credential and shows UNREACHABLE "The
device did not identify itself as an Axis device. No password was sent." (logged as a warning). One try
= `basicdeviceinfo getAllProperties` with the credential (plus `param.cgi` network parameters when the
device allows anonymous access); 401 = next, a scheme that does not answer = the next scheme, no scheme
answering = UNREACHABLE, another serial at the address = UNREACHABLE. A device that logged in over HTTP
with Basic only is stored with scheme http and then needs Digest like every managed device (Basic stays
HTTPS only outside the add page's login). At most 32 devices log
in at the same time (`DiscoveryAuthenticator.Parallelism`; each device still gets one login after another). Results live per (session, serial) for 30 min after the last use together with
the credential that worked (server memory); `Commit` stores that credential for the new device
(explicit `credentials` in the request still win, legacy). Passwords never leave the server once
stored; RetryAuth credentials travel only client -> server and are kept in server memory (or the
credential list when asked). Adding devices is not a task; the first full refresh is queued in the
background as before.

**Device limit**: one server manages at most 30,000 devices (`Oadm.Sdk.Devices.DeviceLimits.MaxDevices`, text
`TooManyDevicesText`). `DeviceRepository.AddAsync` refuses an add over it (`DeviceLimitException`; adds are serialized), so
every way in is covered; `Commit` checks the whole add first and answers RESOURCE_EXHAUSTED, nothing is stored; the add page
shows it in `ui:MessageWindow` and stays open; the automatic add returns `DeviceAutoAddResult.LimitReached` and logs a
warning at most once an hour. Plugin runs are sized by the same limit (System report 50, Device triggers 10,000 watched
devices). Tests `DeviceLimitTests`.

Automatic add by core plugins (the DHCP server's "Automatically add Axis devices that get an
address"): the SDK service `IDeviceAutoAdd` (`ICorePluginContext.AutoAdd`, DIM null) is implemented by
`Oadm.Server.AddDevices.DeviceAutoAddService` on the same code as this page, no second pipeline: `AddAsync(address,
expectedSerial, source)` runs the anonymous probe (`VapixProbe`; nothing = Unreachable, another serial = SerialMismatch),
the Axis check of `DiscoveryAuthenticator` without any credential (fails = NotAxis, "No password was sent."), then for a
factory-default device no login at all, else the automatic login in a session of its own (credential list only, at most
10 rejected, HTTPS first; Unreachable = not added) and stores the device through `DeviceAdder` (shared with `Commit`:
address = the IP, pin from the probe, the credential that worked, verified) with status Ok, Credentials required (no
credential fits; the context menu's "Log in" fixes it) or Password not set (the context menu's "Set password"), queues the
first full refresh and writes the audit entry "Device added automatically" (user "system", target "<ip> (<serial>)",
detail "<source>: <result>"). Not added (not Axis, other serial, unreachable): logged only. `FollowAsync(serial, address,
source)` follows managed devices (see "Following moved devices"). Passwords never reach the plugin.

Follow-up logins: per (session, serial) the authenticator keeps the device as last observed, the
number of rejected automatic attempts and which credentials (SHA-256 of user + password) the device
rejected. When a new credential becomes known it is tried on every LOGIN_FAILED device that has
attempts left and has not rejected it: (a) a successful `RetryAuth` adds it ("entered", or the list
entry when saved) to the session and its `related_session_ids` and starts those logins before the reply
returns (the devices show PENDING); (b) a credential added to the credential list (Credentials page or
"Save to credential list") reloads the candidates of every session. The follow-up uses the normal login
loop: one login at a time per device, at most 8 devices at once, at most 10 rejected credentials per
device in total (typed credentials of `RetryAuth` are remembered as rejected but not counted), entered
credentials first, then the list; credentials added while it runs are
tried in the same run. Unreachable attempts count neither as attempt nor as rejected.

# Visual Style

Reference: MultiDrive (Avalonia showcase), https://multidrive.io/.
Dark by default, calm and spacious, no gradients inside the app (one exception: the soft glow of the start splash).
**Appearance** Dark or Light per computer (Settings > This computer, `ClientSettings.Appearance`, takes effect after the
client restarts: "Takes effect after the client restarts" below the select while it differs from the running one). The
colors live in `src/Oadm.Theme/Colors.Dark.axaml` and `Colors.Light.axaml` (same keys, `ThemeColorsTests`);
`Oadm.Theme.OadmColors` merges the chosen one into `OadmTheme.axaml` (brushes and styles, shared), read in `App.Initialize`
before the styles load (`JsonClientSettingsStore.ReadAppearance`, `AppOptions.Appearance` overrides it), and the Fluent
theme variant follows. The color values below are the dark ones. Video stays dark in both: plates over the picture use
`Oadm.OverlayBrush` (dark or white translucent with the normal text), the autofocus dimming `Oadm.VideoDimBrush`. Headless
tests render light with `OADM_TEST_APPEARANCE=Light` (client tests).

- Start splash (approved mockup `mockups/loading/index.html`): `Shell/SplashWindow` (borderless,
  520x560, centered) with `Controls/SplashLogo` (the app icon built from seven hexagons: the outer six fly in one after another,
  each drawn as an accent outline that fills; then the center hexagon, the lens ring and the lens opening like an iris; a soft
  glow (`Oadm.SplashGlowBrush`) breathing behind it, one scan line sweep, one lens blink; complete after 2.3 s), the wordmark
  "OADM" whose letter spacing pulls together, "Open AXIS Device Management", the shared progress bar and the real loading step
  (`SplashViewModel`: Starting, Connecting to <server> (+ resuming a remembered login), Loading devices (main streams started,
  waits for the first device list, at most 15 s), Ready). `AppShell.StartupAsync` keeps it at least 2.7 s (logo and wordmark
  complete), then fades it out (0.35 s) and shows the login window (nobody remembered; the login view model is already
  initialized) or the main window. Log out shows the login window directly, without the splash. Tests: `SplashTests`.

- Window: starts maximized. Custom title bar (ExtendClientAreaToDecorationsHint), app name
  top-left, the user and Log out top-right before the window buttons, background near-black `#141414`. Caption button hover
  is a neutral grey slightly lighter than the title bar (never the system blue/accent); the
  close button hovers red like Windows. No connection indicator in the title bar; a banner
  appears only while the server is unreachable.
- Left navigation rail: expanded by default (collapsed: icons only, ~56 px, tooltips with the label),
  expand/collapse toggle at the bottom of the rail; expanded ~220 px with icon + label.
  Selected item as rounded pill `#1C1C1C` (hover `#181818`, the icon wobbles once on hover, `Oadm.NavSelectedBrush` / `NavHoverBrush`, quieter than buttons) with a short accent bar at its left edge like WinUI's NavigationView (3 x 16 px,
  rounded; `Button.nav` template, `Border#PART_Indicator`). Plugin pages (not host pages) have a context menu
  **Open in new window** on their rail entry: the plugin creates another, independent view (own context, streams, state)
  shown in `Shell/PluginWindow` (an empty title bar: the page header names the page; window title
  "<page> · OADM" for the task bar; page padding, not modal, as many as wanted); the windows close
  with the main window, on logout and when the plugin is turned off; a closed window (or a removed rail page) disposes its
  page: the plugin context drops every `DevicesChanged` subscription and the view's DataContext is disposed when it is
  `IDisposable`; the main window unsubscribes from the shell view model when it closes (a new main window per login) (`MainWindowViewModel.PluginWindowRequested` /
  `ClosePluginWindowsRequested`; tests `PluginWindowTests`). Entries top: Devices, **Compliance** (built in, right below Devices, no group; while the role may see
  compliance), then one per Core plugin
  page, grouped by `ICorePlugin.Group` (`CorePluginGroup`, in this order: Servers (NTP, DHCP), Automation (VAPIX
  Commander, Device triggers, Action Rule Editor), Security (PKI, Hardening scan), Monitoring (Live View, Dashboards, Metadata Monitor, Image Health Dashboard), Reporting (Snapshot report; System report has
  no page), Maintenance (Edge Recording Export, Edge storage), Integrations, Utilities (Web View), Extensions (default, third-party)): only groups
  with pages appear, the server's order inside a group; expanded rail = a small grey header (`TextBlock.navGroup`) above
  each group (in the lighter accent color), collapsed = a separator at each group start; the pages scroll when they do not fit. Pinned bottom, top to bottom: Plugins with a divider below it (it changes what the whole client offers), Users,
  Credentials (these three Admin only: hidden for operators, follow the session role live), Logs, Settings, About (icons
  `plugin`, `users`, `key`, `logs`, `settings`, `info`), then the expand toggle. The logged-in user (an initial avatar and
  the name, `Button.titleUser`, opens "Your role") and Log out (icon-only `ui:ToolbarButton`) sit at the right of the title
  bar, next to the system window buttons (`Shell/TitleBarLayout` keeps clear of them: right on Windows and Linux, left on
  macOS, where the app name moves past them; Avalonia reports only the title bar height, not where the buttons are). Keys = `HostPages` (`users`, `credentials`, `plugins`, `logs`, `settings`, `about`); opening an
  admin page as operator does nothing, a hidden page that was open falls back to Devices. No Tasks page. Every page
  header (core plugin pages, Users, Credentials, About, Settings) is the shared `Controls/PageTitleBar` (title, grey
  side subtitle, trailing content); host pages never repeat the page title in a card heading. A plugin page may leave the header row out
  (`ui:PageHeader.IsHidden`, Live View: every pixel for the video).
- Content in rounded cards: background `#181818` (only slightly lighter than the window),
  corner radius 8 (Microsoft Fluent geometry: 8 for cards, menus and flyouts, 4 for buttons, inputs and rows inside a page), padding 20-24, 16 px gap. The device grid sits in one card, the tasks
  pane in a second card below it, separated by a draggable splitter so the user can resize
  the tasks pane; the split is persisted per client.
- Accent violet `#6C5CE7` (hover `#7D6EF0`), used for selection, progress bars, primary
  buttons and status pills. Secondary accent teal `#7FC8D0`.
- Text: primary `#F2F2F2`, secondary `#9A9A9A` (labels like "Source", column headers).
  Font Inter (Avalonia.Fonts.Inter), whole-pixel sizes only (fractional sizes blur): 12 px body, 10 px small, 18 px card titles, 22 px page titles, semibold titles; every window renders text with subpixel anti-aliasing, strong hinting and pixel-aligned baselines (App.ApplyCrispText). Page subtitles sit right of the page title, small, grey, baseline aligned.
- Status is never a chip: everywhere a status or result is shown (device certificate columns,
  tasks, task steps, logs, add page, plugin dialogs) use `ui:StatusChip` = a small icon colored
  by the status (check ok green, warning amber, error red, running violet and spinning, information blue "i" (`IsInfo`,
  something happened, nothing to check), neutral grey) with plain text in the normal text color to its right, plus optional detail text
  (e.g. the error message). Colour marks exceptions: the device grid's first-column
  device icon is neutral for OK and colored only for warning and error statuses, the Status column is plain text, and
  certificates that are fine (more than 30 days left, Trusted) are plain text without an icon. Tasks pane: the Status column shows only the
  colored icon, with the error or warning message right of it when there is one; finished tasks
  show no progress bar. Count badges stay a small solid violet pill.
- Window chrome: title bar and caption buttons are 32 px high (caption buttons 46x32), exactly
  like the Windows default. Main window minimum size 1200x640.
- **Small and scaled screens** (Full HD at 125 % leaves about 1536 x 816 logical pixels): every window, host and plugin
  dialogs alike, is fitted to the screen it opens on (`Oadm.Theme.Shell.WindowFit`, also used by the site node app, attached in `App`, applied once the window is placed: dialogs open next to
  their owner, also on another monitor, plugin windows on the monitor of the main window; a window larger than the
  work area less 16 px per side shrinks to it, its minimum and maximum follow, then it is centered or moved back onto the
  screen; `WindowFitTests`). Toolbars of buttons are `ui:ToolbarOverflowPanel` (entries that do not fit go behind a More
  button), input rows that must not leave the card wrap (`WrapPanel.inputRow`), grid columns keep readable minimum
  widths and the grid scrolls sideways. Check a new page with the layout survey (`LayoutSurveyTests`,
  `OADM_LAYOUT_SURVEY=1`, `OADM_LAYOUT_SURVEY_SIZE=1280x672`, screenshots and a list of controls that leave the window).
- Dialogs (every dialog window incl. `ui:MessageWindow`, the device picker and the login
  window): no system title bar (`WindowDecorations="BorderOnly"`); the first row is `ui:DialogTitleBar`, the title as a
  heading and an X top right that closes the window, aligned with the content. Modal, not movable and fixed size
  (`CanResize="False"`: a resizable border-only window shows a white resize strip on top on Windows), like a dialog on a
  web page. The main window and plugin windows keep the custom title bar.
- Text fields, number fields and select fields: text vertically centered (measured; select
  fields need the Inter correction, text boxes do not). A focused field has the accent border (select and number
  fields too, also after a click). The mouse wheel over a closed select field or a number field scrolls the page and never
  changes the value (`Infrastructure/WheelScroll`, attached in `App`, so plugin views too; test `WheelScrollTests`).
- Icons: one outline icon set (Fluent System Icons / Lucide geometry), monochrome
  `#CFCFCF`, 16 px. Toolbar uses icon + text buttons, not ADM's icon-only bar.
- All colors in `src/Oadm.Theme/Colors.Dark.axaml` / `Colors.Light.axaml`, brushes and sizes in `src/Oadm.Theme/OadmTheme.axaml`
  (the client's `Themes/OadmTheme.axaml` includes it plus the client-only styles: tag groups, `TagChipList`, `TagColorPicker`); no
  hard-coded colors in views or code (a new color gets a key in both files). Exceptions: the splash glow gradient and the
  Fluent palettes in `OadmTheme.axaml` (accent based, the same in both), fallbacks in code when a key cannot be found.

# Main Window: Manage Devices

Reference for content: the Manage devices page of ADM. Same information density, styled as described in Visual Style.

Layout, top to bottom:
1. Title "Devices". Left navigation rail as described in Visual Style.
2. Toolbar: toolbar plugins (**Add** menu | Remove, Refresh, Export | task plugin actions that declare `ShowInToolbar`,
   then System report (plugin) | plugin entries) in `ui:ToolbarOverflowPanel`: entries that do not fit (narrow
   window, live view open) move into a **More** menu (icon `more`, right of the toolbar). Right-aligned: the AXIS OS
   release notes link (icon only, toolbar controls with the class `trailing` go here), **Group by tag** icon button (icon
   `groupBy`, class `active` while on), **Show each compliance check** icon button (icon `squareActivity`, class `active`
   while on, only while the Compliance column is on), Columns icon button (tooltip "Choose columns") and search box (host parts);
   everything fits on one line at a 1800 px window width with the rail expanded and the live view closed. One primary **Add** button with a menu Discovery (zero-conf scan), Network range,
   Manual, Import from file, Import from ADM instead of five buttons; every toolbar entry is a button with text (only Columns, Group by tag
   and the release notes link are icons). The empty Devices page shows a primary **Add devices** button with the same
   menu.
3. Status line: "N devices, M selected".
4. Device grid (virtualized): sortable, column chooser, column order and width persisted per
   client, horizontal scroll, multi-select, right-click context menu: the core actions (Open web
   interface, Refresh, **Log in**, **Set password**, **Tags**), a separator, then one **submenu per task group** (`TaskPluginInfo.group`, sorted
   by name, with a group icon: Applications app, Maintenance settings, Network network, Security key,
   Users users, Video video, others plugin; a group is a submenu even with one entry)
   holding **every** menu Task plugin (`ShowInMenus`), sorted by name, with their icons. A plugin whose `CanRun` is
   false for any selected device stays visible but **disabled** (theme's disabled menu look) with the reason as tooltip
   (`ToolTip.ShowOnDisabled`): one device the plugin's text, e.g. "Needs AXIS OS 11.11 or
   later (this device has 11.9.65)"; several devices the most common reason without the device detail and how many of
   the selected devices it concerns, plus the number of other reasons ("Needs AXIS OS 11.11 or later: 3 of 5 selected
   devices (+1 other reason)", `TaskPluginCatalog.NotRunnableReason`, O(selection)); a group whose entries are all
   disabled is still shown. **Remove** comes last, after its own separator (away from the everyday entries); the entry
   of an open submenu stays highlighted. Entries never end with "..." (the host appends none and strips "..." / "…" defensively,
   `TaskPluginNames.Normalize`). Menus and submenus are at least `Oadm.MenuMinWidth` (240) wide (theme).
   The toolbar task buttons have no groups; one the selection cannot run is disabled with the same reason as tooltip
   (`IToolbarContext.CannotRunTaskReason`; no tooltip without a selection).
   **Log in** (icon `key`) is shown only while the selection holds a device with status
   Credentials required (not Password not set: "Set password" below) and follows the status live; it
   applies to those devices of the selection. A rejected stored credential only shows as that status (polling), there
   are no automatic retries. Dialog `Devices/DeviceLoginWindow` (`DeviceLoginViewModel`, single operation:
   `Border.dialogBody`, 460 px, no card): "10.0.0.48 (P3265-V) rejects the stored credentials." / "5 devices reject the
   stored credentials.", User name (prefilled with the stored one the devices share, `GetCredentialUserName`, else
   "root"), Password (`ui:PasswordBox`), "Save to credential list" (checked; administrators only, hidden for
   operators), Cancel / **Log in** (disabled with the reason as tooltip). One `DeviceService.LogIn` call for all of them
   with the shared `ui:ProgressRow`; all accepted: the dialog closes (a credential list note then shows in the message
   window); otherwise the accepted devices are done and leave the dialog, the result stands directly below the password
   field: one device its reason ("The user name or password is wrong."), several "2 of 5 devices rejected it:
   10.0.0.48, 10.0.0.49" (other reasons: "2 of 3 devices failed: 10.0.0.48 (wrong user name or password), 10.0.0.49
   (Unreachable - ...)", at most 5 named, "+N more"). Fake mode accepts every password except "wrong" (10.0.0.23 is
   Credentials required). Tests: `tests/Oadm.Server.Tests/DeviceLoginTests` (+ Perf 5,000 devices in one call),
   `tests/Oadm.Client.Tests/DeviceLoginTests`.
   **Set password** (icon `lock`) is shown only while the selection holds a device with status
   Password not set (e.g. added by the DHCP server's automatic add) and follows the status live; it applies to those
   devices. Dialog `Devices/DeviceSetPasswordWindow` (`DeviceSetPasswordViewModel`, single operation, 460 px, like the add
   page's first-password editor): "10.0.0.40 (AXIS F9111) has no password yet (factory default)." / "3 devices have no
   password yet (factory default).", User name "root" (fixed, read-only), New password + Confirm password
   (`ui:PasswordBox`), hint with the passphrase policy from `GetPassphrasePolicies` (`PasswordRules`, the add page's
   rules; several devices: every policy applies, "length" and "complex" together = at least 15 with every class), errors
   below the fields, Cancel / **Set password** (disabled with the reason as tooltip). One `DeviceService.SetFirstPassword`
   call with the `ui:ProgressRow`; all took it: the dialog closes; otherwise those devices leave the dialog and the result
   stands below the password ("1 of 3 devices failed: 10.0.0.41 (The device already has a password. Nothing was
   changed)"). Fake mode sets it in memory (10.0.0.40 is Password not set, policy "complex"). Tests:
   `tests/Oadm.Server.Tests/SetFirstPasswordTests`, `tests/Oadm.Client.Tests/DeviceSetPasswordTests`.
   **Tags** (icon `tag`, one or many devices) opens the Tags dialog, see "Device tags".
   **OADM** (icon `settings`, after the task groups, before Remove): settings of OADM itself for the selected devices,
   rarely needed; today **RTSP port** (video devices, "Optics Control"), see "Live view".
5. Resizable, collapsible bottom pane **Tasks** (no tabs), one row per task (= per device).
   Columns: Name (3*, min 220 px), Device (110 px), Status (3*, min 220 px: icon plus message), Current step, Start time, Owner, Progress (bar).
   An empty history says "No tasks yet. Right-click devices to run one." **Current step** is
   "Step 3/6 · Upload firmware" plus " · 45 %" while the running step reports progress (tooltip: the text
   and the step detail); a failed task shows its failed step, a successful one just "Completed"; empty for plugins without steps. **Device** is the task's
   device by its device grid address (IP or host name, resolved through the client device store
   and updated live); a device removed since shows as "Removed device 1a2b3c4d" (id shortened).
   Tooltip: "10.0.0.200: Failed - Connection refused". Sortable by the text. Status chips:
   Queued/Cancelled neutral, Running violet, Done green, **Done with warnings** amber (warn
   chip), Failed red. **Details** opens the task details window, live while it is open: one header line with the
   device (address, MAC address, model, status chip with the message), a **Steps** card (scrolls to the step the task
   is on) ("3 of 9 steps finished"; per
   step #, state chip (Pending/Skipped neutral, Running violet, Done green, Warning amber, Failed red),
   name, detail, duration, progress bar + percent only for the running step; rows updated in place) and a
   Log card (level chip, time, device address, message; `TaskService.GetLog`, re-read when the current
   step or the task state changes, new entries appended), built from the shared grid and chip styles; **Cancel task**
   in its footer while the task runs. Pane buttons: details, cancel, delete, a separator, **delete all** (red, with
   confirmation; running tasks are cancelled first); all work per task.

**Logs page** (rail, bottom; `Logging/LogsViewModel` + `LogsView`): the segmented switch **Server** | **Client** | **Audit**
on top of each card. **Server** (first when the role may see it, permission `logs.server` "See the server log", group "Logs";
Administrator always, Operator and Viewer off by default): the server's log files read on request by gRPC
`ServerLogService.List(offset, limit, min_level, search)` (`logs.proto`; `Oadm.Server.Logging.ServerLogGrpcService` over
`Oadm.Contracts.Logging.LogFileReader`: the files `oadm-server-*.log`, newest first, entries of the Serilog template, continuation
lines (exceptions) belong to the entry before, read shared while the logger writes, only the files a page needs; at most
500 a page), 200 entries a page (`ServerLogViewModel`), level filter and search on the server, Refresh, grid Time, Level
chip, Source (the class without namespace), Message (tooltip: source, message, exception), read again when the page
opens. **Client**: this client's log of the session (`LogStore`, newest 10,000, live), level filter and search, 200 a page.
Both page with the shared `Logging/LogPager` ("Entries 201 to 400", Newest, Newer, Older); a live client entry never moves an
older page. **Audit** (administrators): as before. Every failed event rule action is also a server log Warning ("Event rule
X: action Y on 10.0.0.48 failed: <reason>", with the exception when it was unexpected), a skipped one and a rule held back by
its limits Information (`ActionRunner.Write`, `RuleEngine.LogRule`). Fake mode: 650 sample entries
(`FakeOadmApi.ServerLog.cs`). Tests: `tests/Oadm.Server.Tests/ServerLogTests`, `tests/Oadm.Client.Tests/LogsPageTests`,
`RuleEngineTests.ActionsRunInOrderAndAFailureDoesNotStopTheNext`.

## Live view

Clicking the device icon (first grid column) of a video device opens the live video in a panel
on the right of the device card, inside the main window, so nobody has to log in to the camera
web UI.

- Panel: reusable `Controls/LiveViewPanel` in a card next to the device card (about 65 : 35,
  resizable with the vertical `paneSplitter`; the device card keeps at least 900 px, toolbar entries that do not fit go
  into the More menu, Address and Status keep minimum widths so they stay readable; the panel at least 320 px). Slides in from the right (250 ms, cubic ease-out).
  Header, one row: `IconLabel` with video icon and the model (" · <channel name>" with several channels), close button.
  Below it the shared live video. `LiveView/LiveViewViewModel` only opens, switches and closes it and remembers the chosen
  channel per device for the client session.
- **Live video component** (`LiveView/LiveVideoViewModel` + `LiveVideoView`, the one live picture of the client: side
  panel, fullscreen, later the camera grid of 16, which must stay smooth). Parts on or off per place through
  `LiveVideoOptions` (channel switch, fullscreen, quality switch, controls button, each PTZ part, zoom, focus; `MaxFps`
  25; the start quality; delays); client-wide `LiveVideoSettings` (Settings > This computer: "Show video details over the
  picture", off; "Frame rate (adaptive quality)", 20, 1..30; `ClientSettings.ShowVideoDetails` / `AdaptiveFps`; plus
  "Optics Control" of the role). The picture area shows only the picture (`Stretch=Uniform`), the `ui:ProgressRing`
  (connecting without a picture, reconnecting with the reason below it, or a new stream slow to deliver) or the error
  (`ui:StatusChip`). Over it, small plates at the edges (`Border.videoOverlay`): top left on hover fullscreen and the
  quality button (**Adaptive**: the smallest resolution of the channel's list that fills the picture's size in device
  pixels without enlarging, at the adaptive frame rate; **Full**: the largest at `MaxFps`; default Adaptive; both take
  **16:9** whenever the channel lists 16:9 resolutions (almost every monitor is 16:9, also a 4:3 sensor offers them), else
  the sensor's aspect: `LiveVideoViewModel.PreferredAspect`); top right the
  orange GPU warning icon (always, what to do as tooltip) and on hover the channel switch ("1 2 ...", tooltip = name; the
  place can turn it off and set the channel), below them the details card when the setting is on (`Border.videoDetails`,
  one row each: Codec, Resolution, Frame rate, Bit rate, Decoding GPU or Processor; `VideoDetails`; top right because the
  bottom belongs to the controls); at the bottom one row: the zoom and focus or PTZ bar over the width and at its right end
  the controls button (`Icon.sliders`, only for a camera with zoom and focus or PTZ and a role with "Optics Control"; on
  hover only while the controls are closed): it opens and closes the bar, **open from the start** (`LiveVideoOptions.ControlsOpen`,
  default true; a grid tile can start closed); the user's choice holds while the component lives and goes to fullscreen; the
  PTZ circle shows only while it is open. Hover parts: theme `Border.liveVideo:pointerover
  :is(Control).videoHover`.
  **Silent swap**: a new size (after it rests 500 ms), a new quality, a new adaptive frame rate, a finished GPU test or a
  restart after hiding opens a second stream only when its request (channel, resolution, fps) differs; the old picture
  keeps showing until the new stream's first picture, then the old stream closes; after 2 s without a picture the ring
  shows over the old picture; a new stream that fails is dropped and logged, the old one stays. Switching the channel
  clears the picture. **Visibility**: the view reports attached, effectively visible, inside the visible part of a
  scrolling parent and window not minimized (checked every 500 ms and on `EffectiveViewportChanged`); hidden for 2 s the
  stream stops and the last picture stays (state Paused); shown again it streams with the silent swap. **Fullscreen**
  (button or double-click): `LiveView/FullscreenVideoWindow` (borderless, full screen, owned by the main window) with its
  own `LiveVideoViewModel` and stream of the same channel at the screen's size; the video below counts as hidden while it
  is open; Esc, the button or a double-click close it. The resolution list comes from the server
  (`LiveViewSource.resolutions = 6`, `LiveViewResolution {width, height}`; the request asks for exactly that size).
- **Zoom and focus** (`LiveView/OpticsViewModel`, `OpticsBar`, `FocusAreaOverlay`; server `Core/LiveView/IDeviceOptics`,
  `VapixClient.Optics.cs`, Optics Control API `axis-cgi/opticscontrol.cgi`): a bar over the bottom of the picture
  (`Border.opticsBar`, overlay brush, opened with the controls button) only for a camera with remote zoom or focus (`getCapabilities`: zoom with
  `maxMagnification`, focus) and a role with "Optics Control"; other cameras show nothing, no error. Zoom slider 1 to the
  maximum, Focus slider near to far (0..1 on the device), small knobs (`Slider.optics`), no values shown; icon buttons
  Autofocus (`Icon.focus`), Autofocus area (`Icon.focusArea`: the current area drawn over the picture, dimmed around it,
  a drag draws a new one; Set sends it and runs autofocus, Cancel puts the camera's area back) and Reset focus (`reset`
  focus, then autofocus). A slider is sent once it rests for 300 ms and keeps the user's value (the camera answers while
  the lens still moves); autofocus waits for the camera (about 4 s, 45 s timeout) and its refusal shows in the bar
  ("The camera could not find focus. ..."). The server checks the fresh capabilities and clamps every value before
  sending (zoom 1..maximum, focus 0..1, the area inside the picture with at least 5 % per side); refused statuses
  without a request. Per source: the lens of the live view source's sensor (`OpticsRequest.sensor` = the 0-based
  `LiveViewSource.sensor` = opticsId, else the first optics); switching the source reads it again. Verified on the AXIS P3265-V (AXIS OS 12.11, API 1.3): set calls
  answer at once, zoom also moves focus, out-of-range values are error 2104; opt-in `OpticsHardwareWriteTests`
  (`OADM_OPTICS_WRITE=1`, puts zoom, focus and area back). Fake mode: the P3265-V models have zoom and focus.
- **PTZ** (`LiveView/PtzViewModel`, `PtzOverlay`, `PtzBar`; server `Core/LiveView/DevicePtz`, `VapixClient.Ptz.cs`, PTZ API
  `axis-cgi/com/ptz.cgi`): for a camera with `Properties.PTZ.PTZ=yes` (these have no Optics Control) and a role with
  "Optics Control", per channel (the live view's source; both channels of a bispectral camera move the same head) what
  `info=1` lists, while the controls are open: a circle with a cross in the middle of the picture: dragging from it moves the camera
  (`continuouspantiltmove`, speed by the distance, reach half the picture, 8 % dead zone, up on screen = tilt up), repeated
  every 300 ms with `timeout=1` so the camera stops by itself 1 s after the last (a lost client never leaves it turning),
  release sends a stop; moves go out one at a time with the newest speed (pointer moves in between are skipped, so a stop
  is never overtaken); only the left button drags, another button or finger is ignored; the drag also ends when the window
  loses the focus, the overlay hides or the role loses "Optics Control"; the mouse wheel zooms (`rzoom`, 400 steps a notch, sent once the wheel rests); a click elsewhere
  centers the camera on that point (`center`, thousandths of the picture). The bar (`Border.opticsBar`): Home
  (`move=home`, `Icon.home`), the presets (`query=presetposall&format=json`, choosing one = `gotoserverpresetname`) and a
  zoom slider (`zoom=1..9999`); the zoom is read again after a move. Errors are HTTP 200 "Error:..." (`PtzException`).
  Verified with an AXIS Q6411-LE of the AXIS Virtual Loan Tool (AXIS OS 13.0.88) behind port forwards; opt-in
  `PtzHardwareWriteTests` (`OADM_PTZ_URL`, `_USER`, `_PASSWORD`, `_RTSP_PORT`; also streams over the forwarded RTSP port).
  Fake mode: the Q6135-LE (10.0.0.22) is a PTZ camera with three presets.
- **RTSP port** (context menu **OADM > RTSP port**, video devices, "Optics Control"; `Devices/RtspPortWindow` +
  `RtspPortViewModel`, single operation): the port OADM connects to for live view and events of the selection, for
  cameras behind a port forward (AXIS Virtual Loan Tool, NAT); one call for the selection; empty or 554 = default,
  "Use default"; "The devices use different ports." when they differ; intro "Used for live view and events. Not set on the
  camera."; Save waits for the current port to be read (a failed
  read says so below the field). Stored in the server setting `Devices.RtspPorts` (`DeviceEventStreams`; one read and one
  write for the whole selection, `IDeviceEventStreams.GetRtspPortsAsync` / `SetRtspPortsAsync`, ports of removed devices
  dropped in the same write), read by the live view (`AxisLiveVideoSourceFactory`) and the Metadata Monitor; nothing is
  written to the devices. A saved port reopens the shown live view on a new camera connection (`LiveViewHub.Invalidate`:
  the stream key carries a generation per device, new viewers never join a connection on the old port).
- Open/close: the icon of the shown device or the close button or Escape closes the panel and
  stops the stream; another device's icon switches. Removing the shown device closes it. The icon
  cell has a hover state and the tooltip "Live preview"; devices without video are disabled
  (`LiveViewSupport.IsSupported`, a model heuristic until the device category from the server is
  available: C audio, A1/A9 door and I/O controllers, D3 sensors and T accessories have no video).
- Sources: `ListSources` returns every `Image.I<n>` that is not disabled as camera `n+1` with its
  `Image.I<n>.Name` (view areas on single-sensor cameras, sensors and quad view on multisensor
  cameras, inputs on encoders), each with every resolution it lists (all aspects; the client picks, see above). With more than one source the live
  video shows the channel switch; switching restarts the stream with `camera=<n>`; the side panel remembers the choice
  per device for the client session. The development camera (AXIS P3265-V) reports one sensor
  (`ImageSource.NbrOfSources=1`), 8 view areas, of which "View Area 1" (full frame) and
  "View Area 2" (cropped) are enabled.
- Transport: the server opens RTSP/1.0 over TCP to port 554 with RTP interleaved on the same
  connection, Digest authentication with the stored credentials (Basic is refused on plain RTSP;
  credentials never reach the client), `Blocksize: 64000`, a keyframe every second
  (`videokeyframeinterval=fps`). Own RTSP client and depacketizers (H.264 RFC 6184, H.265 RFC 7798)
  in `Oadm.Core/LiveView`. Video itself is not encrypted on the camera link (RTSP over HTTPS is a
  later option). Devices with status `CertificateChanged` are refused.
- Negotiation: H.265 > H.264 among the codecs the camera lists in `Properties.Image.Format` and
  the client can decode; when opening a codec fails the next is tried. No MJPEG. Resolution: a size the source lists is
  taken as asked (the client asks for one of the list, see the live video component); otherwise the largest of the
  source's resolutions with the sensor aspect that fits the requested box (default 1920x1080), at the requested frame rate.
- Sharing: one upstream camera connection per (device, camera, codec, resolution, fps) shared by
  all viewers, opened on the first and closed when the last viewer leaves. A joining viewer gets
  the cached GOP first (picture within one frame time). Slow viewers never buffer: a full queue
  (60 frames) is dropped and the viewer resumes at the next keyframe. Packet loss drops access
  units until the next keyframe.
- gRPC: each `LiveViewFrame` carries one Annex B access unit, the keyframe flag, parameter sets
  on keyframes, RTP timestamp, resolution and camera. Client channel max message size 32 MB.
- Client: FFmpeg decode on a background thread (low delay, slice threads), swscale to BGRA into a
  new `WriteableBitmap` (through `BgraConversion`: swscale writes into FFmpeg's own row-padded picture, then the rows are
  copied; straight into the bitmap it wrote 8 to 32 bytes past the buffer for widths not a multiple of 16, test
  `SwscaleBoundsTests`), swapped on the UI thread, old bitmaps disposed. Reconnect with backoff
  1, 2, 4, 8, 10 s; permanent errors (unknown device, credentials, no common codec, certificate
  changed) stop with the server's message. If FFmpeg cannot be loaded the picture area shows
  "Video decoder not available on this platform" (no fallback).
- **GPU decoding** (`LiveView/GpuDecoding.cs`, `FfmpegVideoDecoder`): client setting "Decode video on the graphics card"
  (`ClientSettings.GpuDecoding`, default on, Settings > **This computer**, saved at once, every user). At every start (and
  when the setting is turned on) `GpuDecodingService` runs the client itself with `--check-gpu-decoding` in a **separate
  process** (20 s timeout; a driver crash or hang only ends that process): it opens the first device type that works
  (Windows d3d11va, dxva2; macOS videotoolbox; Linux vaapi, cuda; `OADM_GPU_DEVICES` overrides the list) and decodes the
  embedded H.264 and H.265 clips on it; output `device=`, `h264=ok|failed: ...`, `h265=...`, `problem=`. Only after it
  passed does the client open the device in its own process (`HwDevice`, one per device type, shared by all streams).
  `FfmpegVideoDecoderFactory` creates GPU decoders for the codecs that passed, else processor decoders with the warning
  text. A GPU decoder that fails on a stream (no hardware format, a copy that fails, no picture after two keyframes)
  reopens on the processor and says why; decoding never throws. Linux: the bundled FFmpeg has VA-API built in with the
  driver path of its build machine, so `HwDevice.PrepareEnvironment` (start of `Main`) sets `LIBVA_DRIVERS_PATH` to the
  system's `dri` folders through libc `setenv` unless the user set it. The details say "GPU" or "Processor", and an
  orange warning icon top right over the picture (tooltip: what to do, `GpuDecodingTexts`: Linux "Install the video driver of the graphics card (Intel:
  intel-media-va-driver, AMD: mesa-va-drivers, NVIDIA: the NVIDIA driver) or turn off ...", no device, the driver stopped
  the test, a codec the card cannot decode) whenever the setting is on and the stream runs on the processor; off = no
  warning. A finished test or a changed setting opens a new stream with the right decoder (silent swap). Verified on Windows
  (NVIDIA: d3d11va, dxva2, cuda decode both codecs). Tests: `GpuDecodingTests` (every device type decodes every picture or
  says why, the separate process and a crashing one), `LiveVideoViewModelTests`.
- Tests: `LiveVideoViewModelTests` (resolution choice, Full, the silent swap, a failing or slow new stream, hidden and
  shown, channels, reconnect and errors, details, controls and options, fullscreen), `LiveVideoViewHeadlessTests`
  (hover parts, controls, ring, error, warning, fullscreen and Esc), `LiveViewViewModelTests` (the side panel).
- Fake mode replays recorded video of the development camera (H.265 or H.264) and reports two view areas for the
  P3265-V and four sensors plus quad view for the P3727-PLE.
- Measured on the development camera at 640x360, 10 fps: first frame 0.3-0.6 s after the request through the
  server; about 160-250 kbit/s for both H.264 and H.265 (scene dependent, keyframe every second).

Device grid columns, default order (Address first: installers find a device by address and model; the column chooser
and the saved per-client order cover other preferences):

| Column | Source |
|---|---|
| (icon) | category icon (camera, encoder, speaker, audio, intercom, radar, I/O module, door controller, generic), tooltip "Camera (Dome Camera)" |
| Address | IP or host name, hyperlink opens device web UI in default browser |
| Model | ProdNbr |
| Status | computed, see enum |
| Compliance | off by default (column chooser; greyed there while compliance is off): the worst compliance result ("2 problems, 1 warning" chip, "OK" plain text, failed checks as tooltip; sorted problems first); while it is on the toolbar toggle **Show each compliance check** shows one column per check instead; hidden without "See compliance". See "Compliance" |
| MAC address | SerialNumber from basicdeviceinfo |
| Firmware | Version |
| SoC | basicdeviceinfo `Soc` (status poll and full refresh), shown in Axis spelling: "Axis Artpec-8" = "ARTPEC-8", other chips as reported (`DeviceSocText`); empty until read; in the search and the export |
| Tags | the device's tags as chips (`c:TagChipList`, `Border.tagChip`: the name in the tag color on a subtle pill), "+N" when they do not fit, tooltip with every tag; sorted by "Building A; PTZ" |
| DHCP | Network.BootProto == dhcp -> Yes/No |
| HTTPS | HTTPS enabled -> Enabled/Disabled |
| Certificate expires | CertNotAfterUtc as days left: "245 days", "1 day", "Today", "Expired 3 days ago", "Never" for NotAfter 9999-12-31 (RFC 5280, e.g. the Axis device ID certificate). More than 30 days: plain text, warn chip <= 30 days, error chip expired; right-aligned (numeric column). Over HTTP read through VAPIX; empty when HTTPS is off or unreadable |
| Certificate | CertTrust: Trusted (plain text), Self-signed (warn chip), Untrusted / Expired (error chip). Empty when HTTPS is off or unreadable. Both certificate cells have a tooltip with subject, issuer and valid-until date |
| IEEE 802.1X | dot1x.Enabled -> Enabled/Disabled |

Polling: server refreshes status (basicdeviceinfo, **3 s** timeout: `StatusPollTimeout`) for every device every **60 s** by
default (`Polling.IntervalSeconds`). Full refresh (basicdeviceinfo + network parameters +
certificate info + server name) on add, after a task finishes on that device,
every **10 minutes** per device that is Ok (`Polling.FullRefreshMinutes`, checked every 30 s; devices that are not Ok
are left to the status poll), immediately when a status poll sees a device go from any non-Ok status back to Ok, and
immediately when it sees a new firmware version or model (a new API list). All full refreshes go through one deduplicating
queue, at most `RefreshParallelism` = 64 at once. Scale: a poll round starts every interval and spreads
its device starts evenly over 90 % of it (`PollRoundAsync`, at most `MaxParallelism` = 200 at once, no setting: the
spread keeps about 40 busy for 30,000 devices answering in 70 ms, the cap only matters while devices are slow or dead);
each device row is read right before its poll. A device that stops answering (timeout, refused connection) is asked once
more at the end of the round before it turns Unreachable; in an outage (more than `OutageShare` = 10 % of the round
failed) there is no second try (30,000 dead devices: one round of 7.6 minutes). More than half of a round failing logs one
warning "Most devices do not answer. Check the server's network.", and one information when they answer again; single
failed polls are Debug only. After a server start the first scheduled full refresh is staggered: the n-th of N devices
is due n/N of an interval later. A poll that changes nothing but LastSeenUtc is written but not published to watchers (no
30,000 identical updates per minute to every client). Tests `DevicePollingRoundTests`, `DevicePollingScheduleTests`.

# Device tags

Built-in feature (not a plugin). Technicians label devices ("Building A", "PTZ") and work
with the labels in the grid, the search, the export and the import; plugins read them through `IDeviceInfo.Tags`.

- **Definitions**: a name (1..64 characters, trimmed, unique case-insensitive, no ";" and no control characters) and a
  color of the fixed palette of eight: Blue, Green, Teal, Amber, Orange, Red, Pink, Violet (theme brushes
  `Oadm.Tag.<Name>Brush`, readable as text on the dark cards; `Oadm.Tag.NeutralBrush` grey for names on devices without
  a definition, e.g. older data). A tag created without a color gets the next palette color (definition count modulo 8).
  Server: `TagDefinition` / `DeviceTagStore`, gRPC `TagService` (see "gRPC services"). Roles: operators tag devices and
  create tags; rename, recolor and delete are Admin only. Every change is audited.
- **Client store**: `Tags/TagStore` (fed by `TagService.Watch` in `ServerConnection`, owned by `DeviceStore.Tags`) keeps
  one shared `TagInfo` per name (case-insensitive); device rows hold `TagChips` of those shared objects, so a recolor
  updates every chip and group header without touching 5,000 rows. `DeviceRowViewModel.Tags` / `TagsText`
  ("Building A; PTZ", sort key and export text); the device search also matches tag names.
- **Tags dialog** (`Tags/DeviceTagsWindow`, `DeviceTagsViewModel`; context menu "Tags"): title bar, one card with the
  intro ("Tags of 12 selected devices. ..."), `ui:SearchBox` + **New tag** (inline editor: Name in `ui:FormField` with the
  error below the field, Color with `c:TagColorPicker` = eight round swatches, Create tag / Cancel; created at once on the
  server and checked), the virtualized tag list (three-state check box: all / some / none of the selected devices, a
  click makes some or none all and all none; color dot, name in the tag color, "42 devices" = all devices with the tag;
  administrators also get Rename or recolor (inline editor below the list, saved at once) and Delete tag (confirmation in
  `ui:MessageWindow`: "Remove tag X from 42 devices?"), a failed server call directly below the list, footer Cancel /
  **OK**: the changed check boxes as one `SetDeviceTags` call for the whole selection; nothing changed = no call.
- **Group mode** (toolbar "Group by tag", persisted per client): the grid shows a collapsible group per tag in tag
  name order and "No tag" last, header "● Building A · 42 devices" in the tag color (theme style
  `DataGrid.tagGroups DataGridRowGroupHeader`, chevron expander `ToggleButton.groupExpander`); the Tags column is hidden
  while grouping (the group header names the tag). **A device with several tags
  appears under every one of them**: the rows are `DeviceTagRow` wrappers (device x tag, `Tags/
  DeviceTagGrouping`), grouped by Avalonia's `DataGridCollectionView` with the groups as explicit `GroupKeys` (so the group
  order stays when the user sorts a column; sorting works within the groups). Every grid column binds through
  `IDeviceGridItem.Row`, so device rows and wrappers share the columns. The grid selection goes to `SelectedGridItems`;
  `SelectedDevices` holds each device once (status line, context menu, toolbar, actions). The wrappers are rebuilt
  (one reset, rows reused) only when the shown devices, a device's tags or the tag names change; status updates reach
  the grid through bindings. Measured with 5,000 devices x up to 3 tags (8,750 rows): build 24 ms, search + clear 18 ms,
  select all 8 ms, 1,000 tag changes 92 ms (view model); headless grid: group mode on + render 0.5 s, select all 0.5 s,
  1,000 tag changes while grouped and sorted 1.9 s (DataGridCollectionView regroups).
- **Export / import**: the device export has a "Tags" column ("Building A; PTZ"); the import (add page, Import from
  file) reads an optional "Tags" (or "Tag") column, ";"-separated, and assigns those tags to the devices that get added,
  one `SetDeviceTags` call per distinct tag set; missing definitions are created by the server with the next palette
  color. A tag longer than 64 characters (CSV and ADM export) is cut to 64 (`DeviceImportFile.CutTags`), never a
  problem of the line. In the grid a chip is at most 200 px wide and ends with "…"; the tooltip names every tag.
- **Fake mode**: Building A (blue), Building C (green), PTZ (amber), Outdoor (teal) on the sample devices; every
  `TagService` call works in memory (`FakeOadmApi.Tags.cs`).
- **Tests**: `tests/Oadm.Server.Tests/TagServiceTests` + Perf `TagScaleTests` (5,000 devices: SetDeviceTags 4.4 s, rename
  2.2 s, delete 2.0 s, list 35 ms); `tests/Oadm.Client.Tests/DeviceTagsTests`, `DeviceTagsHeadlessTests`, Perf
  `TagGroupScaleTests`.

# Compliance

Built-in feature (not a plugin): which devices need attention at first glance. Core `Oadm.Core.Compliance`, server `Oadm.Server.Compliance`, gRPC `ComplianceService`, client
`Compliance/`, SDK `Oadm.Sdk.Compliance`.

- **Off by default** (server setting `Compliance.Enabled`): the page shows an empty state with **Enable Compliance Checks**
  (then a short message about the Devices column), **Turn off compliance** in its toolbar (confirmed); off = nothing is
  checked, no events, results removed, checks kept; the Devices column chooser greys Compliance out while it is off.
- **Check** = a check type + its settings + its devices (always chosen with the shared picker: All devices, tags, chosen
  devices) + Problem or Warning + on / off + (types that read devices) every 15..1440 minutes (default 60). At most 50.
  Default checks **Reachable** and **Login works**: created once per server (setting `Compliance.DefaultsCreated`), then
  ordinary checks (change, turn off, delete; New check adds them again).
- **Types**: host (cached data, never contact a device, follow every device change): Reachable, Login works, Certificate
  (expired, fewer than N days left (default 30), not trusted), Firmware (at least a version). Plugins: **Application
  running** (Applications), **Matches configuration file** (Device configuration: a `.cfg` of Create configuration file or
  ADM kept in the check; parameters, action rules and stream profiles by name; per-device values and passwords never
  compared, values the model lacks skipped), **SD card** (Edge storage: no card fails; optional free GB / %; only while Edge
  storage is on). New types: SDK `IComplianceCheckType`, no host change.
- Rules that must hold: checks are read only; devices whose status is not Ok are not contacted (Not checked with the
  reason); 32 devices at once, 15 s per check, all due checks of a device in one run sharing their reads; results kept per
  check and device (outcome, detail, checked, since) and written in batches; a changed check is checked again on every
  device; a type whose plugin is off says so and does not run. Every check is a stateful event of the event rules
  ("Compliance: <name>", on while it fails on a device). No fix actions on the page.
- **Page** (rail, right below Devices): a count card per check (a click shows the devices that fail it: the tabs grey out,
  "Failing: <check>" and **Show all devices** next to them; Edit and Delete on the card on hover), tabs All | Problems |
  Warnings (All first), the devices with a Result column and one column per check
  (the result text next to the icon, on by default, toggle; several detail lines on one line: "1 setting differs: X: 60Hz
  (file: 50Hz)"; the tooltip over the whole cell), Group by tag, Choose columns (per client), New check, Check now
  (a spinning "Checking..." per device and check until the result is in); right: Turn off compliance, Export. Dialog `ComplianceCheckWindow` (single
  operation: Name, Devices, Kind of check, its settings through the shared `SettingsFormView` of the event rules).
  **Devices grid**: the Compliance column (off by default) or, while it is on, one column per check (toggle, per client).
- Roles: "See compliance" (read, Check now), "Change compliance checks" (create, change, delete; audited).
- Scale (30,000 devices x 50 checks): scope and failing devices as sets per check, events as single changes, one
  `ComplianceInvocation` per check version (a configuration file is serialized and parsed once), results written in key
  order 5,000 per transaction, the client keeps ids and repeated texts once. Measured: server enable 5.4 s, results written in 19.5 s,
  1,000 device changes 0.6 s (`ComplianceScaleTests`); client 1.5 million results applied in 0.8 s, filter, tab, card and
  search 27 ms (`ComplianceTests`, Perf); read checks of 30,000 devices at 32 parallel take about 16 minutes a round.
- Developer guide: website Developers > Compliance checks (`dev/compliance-checks.md`); `plugins/README.md`.
- Tests: `tests/Oadm.Core.Tests/Compliance` (+ Perf `ComplianceScaleTests` 30,000 devices x 50 checks),
  `tests/Oadm.Server.Tests/ComplianceServiceTests`, the check types in their plugins' tests,
  `tests/Oadm.Client.Tests/ComplianceTests` (+ Perf 30,000 devices x 50 checks, screenshots). Fake mode:
  `FakeOadmApi.Compliance.cs`.

# Event rules

Built-in feature (not a plugin): "IF events THEN actions".
Events and actions come from plugins (SDK `Oadm.Sdk.Events`, `Oadm.Sdk.Actions`), the server keeps the rules, evaluates
them and logs what happened. Implementation `Oadm.Core.Events`, gRPC `EventService` (`events.proto`), client
`EventRules/`.

| Topic | Decision |
|---|---|
| Built-in events | Only what OADM already knows (`BuiltInEvents`, stateful per device): Device unreachable, Credentials required, Certificate changed (device status from polling), Certificate expires soon (30 days or less, fixed), Certificate expired (rechecked hourly). Each has an **Any device** twin for the server (group "Any device", `BuiltInEvents.AnyOf`): on while at least one device has it, value `count` (how many), recomputed by the engine with every device change. Every other device event comes from the opt-in plugin **Device triggers** (any event a device offers, see "Device triggers"; also the AXIS Image Health Analytics detections, the Image Health Dashboard is a live display only). |
| Kinds | Stateful (on / off per device or for the server) or stateless (one shot). Off is the default; a stateful source sends one full report (`ReplaceStates`) per type after every start ("ready"), then `SetState` per change or `ReplaceStates` again; a source that is turned off counts as everything off. |
| Simple mode | Conditions must all hold. The first is the trigger ("When": an event, devices All / chosen / tags); further ones ("And") are stateful events (on "the same device", or "any" of All / chosen / tags) or schedules (weekdays + from / to in server time, over midnight allowed). Only the trigger may be one-shot. Advanced AND / OR later on the same model. |
| Rule state | All events stateful: the rule is on per subject (the trigger's device, or the server) while all conditions hold. One-shot trigger: the rule fires while the others hold. |
| Actions | One to five per rule, run in the listed order on each edge, each on its targets in parallel (16 at once for all rules, 30 s per run); a failure is logged and the next one runs; no retry. Edges run in order per rule and subject (a "turns off" never overtakes its "turns on"; edges with actions on chosen / tag / all devices also in order among themselves); different devices never wait for each other. Targets: the device of the event, chosen devices, devices with tags, all devices. Actions on chosen / tag / all devices run when the first subject turns on and when the last one turns off; actions on the event's device and actions without devices run per subject. |
| Limits | Per rule, editable (security names): **Dead time** per device (default 10 s between two "turns on"), **Max per minute** (default 60 runs per minute for the whole rule); a suppressed "turns on" also suppresses its "turns off"; "Rule paused" logged once a minute. |
| Changes | Turning off, changing or deleting a rule that is on runs its off actions first; a changed rule is then evaluated fresh. A removed device goes off. A plugin turned off: its events go off, rules show "Not available: <plugin> is turned off". |
| Loops | Actions never fire events themselves; a device an action changes may send an event later, which the dead time keeps bounded. |
| Restart | The subjects each rule was on for are stored (`EventRuleStates`); a rule is evaluated once every event type it uses is ready (or after 2 minutes); only real differences run actions; one-shot events missed while the server was down are not replayed. |
| Roles | Administrators create, change, copy, turn on / off and delete rules (audited "Created / Changed / Deleted / Turned ... event rule"); operators read rules and the log. |
| Log | Own event log (`EventLogEntries`), never tasks: events a rule uses, rule edges, every action run with its result; 30 days or 100,000 entries (`Events.LogRetentionDays`, `Events.LogMaxEntries`, hourly). |

- **SDK**: `IEventSource` (a core plugin declares `EventTypeInfo`: id, name, group, kind, scope, data fields) and
  `ICorePluginContext.EventPublisher` (`IEventPublisher`: `SetState`, `Fire`, `ReplaceStates`, `IsWatched`; only queues).
  `ICorePluginContext.EventSubscriptions` (`IEventSubscriptions.Subscribe(handler)`, `Core.Events.EventSubscription`):
  every event the engine handles (plugins' and built-in) reaches each subscription as `PublishedEvent` (On / Off / Fired,
  values, time), starting with a snapshot of the events that are on; each subscription has its own queue (10,000, oldest
  dropped, logged once a minute) and loop, so the engine never waits; a failing handler is logged; `IsWatched` is true
  while any subscription exists; the host ends a plugin's subscriptions when it stops, fails or is turned off
  (`PluginEventSubscriptions`). `IEventSubscriptions.Types` (`SubscribedEventType`: the type with its fields, source, available)
  and `TypesChanged` (a source changed its types, a plugin turned on or off; unhooked when the plugin stops). Tests
  `EventSubscriptionTests`. First user: Dashboards (see "Dashboards").
  `EventTypeInfo.DeviceIds`: the devices that can send the event (Device triggers: each trigger's devices now, types changed
  again when they change), null = any device. `EventField.Kind` (`EventValueKind` Text, Number, YesNo) says what a value holds; `EventField.Filter` false = a value
  only (placeholder, no filter box in the rule dialog; proto `EventFieldInfo.no_filter = 4`). Device triggers declare every
  value this way, the kind from the device's declared type.
  `IActionPlugin` (standalone class with a parameterless constructor in a `*.Server.dll`, or contributed through
  `IActionSource.ActionPlugins` of a core plugin): `NeedsDevice`, `SupportsTurnOff`, declarative `Fields` (`ActionField`:
  Text, Multiline, Number, Bool, Choice, DynamicChoice through `ListChoicesAsync`, Password stored encrypted and never sent
  back, Code = a body in the code editor colored by `Language` or by the value of the field `LanguageKey` (json, xml, html,
  text, form), KeyValues = name / value rows stored as `[{"name","value"}]` (at most 50, one line each,
  `ActionInvocation.GetPairs`); `VisibleWhenValue` may list several values "basic|digest"), `UsesPlaceholders` (the rule
  dialog shows the Placeholders list), `Validate`, `Describe`,
  `RunsOn(edge)`, `ExecuteAsync(IActionContext, ActionInvocation)`; `IActionContext` gives a pre-authenticated `Vapix` for
  the target, the plugin's settings, `Log` and `Text(text)` / `Text(text, PlaceholderEscape)` (None, Json, Xml, Url: each
  value escaped for the format it goes into). The placeholders are one list,
  `Oadm.Sdk.Actions.ActionPlaceholders.Fixed` (everything OADM knows): {rule} {event} {state}
  {time} {date} {timeUtc}, device {device} {address} {hostName} {model} {serial} {mac} {firmware} {category} {tags}
  {status} {webUrl} (the action's device, else the event's device), then every value of the event by its name; filled in
  one pass (`ActionPlaceholders.Fill`: a value holding "{x}" is never filled again, unknown names stay). The client draws the settings form from `Fields` (`ActionSettings` checks
  shared by server and dialog); no client part is needed. The rule model `RuleDefinition` (JSON, camelCase) lives in
  `Oadm.Sdk.Events` and is the definition JSON of gRPC.
- **Server** (`Oadm.Core.Events`): `EventCatalog` (built-in + plugin types and actions, with availability), `RuleStore`
  (table `EventRules`: definition JSON with password values encrypted, purpose `oadm:event-rule:<rule>:<action key>:<field>`;
  `ForClient` removes them and lists `StoredSecretFields`; an empty field keeps the stored value; at most 500 rules),
  `RuleValidator`, `RuleEngine` (one loop owns all state; publishers, the device feed and rule changes only queue; built-in
  events from `DeviceChangeFeed`; ticks every 15 s for schedules and ready timeouts), `ActionRunner` (edges of one rule in
  order), `EventLog` (queued, batched every 500 ms, live watch), `EventRulesService`, `RuleTexts` (summaries). The engine
  starts after the plugin activation and before the core plugins (`OadmServerHost.StartAsync` 4b); `CorePluginHost` hands
  a publisher to every core plugin that implements `IEventSource`. Actions wait until the core plugins have started
  (`ActionRunner.PluginsStarted`, after `CorePluginHost.StartAllAsync`). Rule states are dropped from memory only after a
  successful write and written once more at stop; a saved / turned on or off / deleted rule always reaches the engine (it
  waits for room in a full queue, events are dropped). Saving a rule whose action's plugin is off runs only the shared field
  checks. The catalog is an id-indexed snapshot rebuilt when plugins or event types change; subject-independent conditions
  are evaluated once per re-evaluation; a chosen device or tag counts only while OADM manages the device. Types can change while the server runs
  (`IDynamicEventSource.EventTypesChanged`, device triggers): the registry reads them again, a new stateful type waits for
  its first report, a removed type counts as off ("Not available: an event of this rule no longer exists"), and a type a
  stored rule uses that no source declares yet waits like a source after a start (2 minutes), then counts as off.
  Placeholders also fill every value of the event by its name (`{port}`, `{total}`) after the fixed ones.
- **gRPC** `EventService`: `GetCatalog`, `ListRules`, `WatchRules` (list on subscribe and after every change, at most every
  500 ms), `GetRule`, `SaveRule`, `DeleteRule`, `SetRuleEnabled`, `DuplicateRule` ("<name> (copy)", off, passwords kept),
  `ListActionChoices` (the last five Admin), `ListLog` (newest first, limit 0 = 10,000, optional rule filter), `WatchLog`,
  `TestAction` (Admin, audited "Tested event rule action": runs an action without devices once now, outside rule states and
  limits, with "sample" (or the first listed value) for every value of the trigger and the trigger's first chosen device as
  the device of the event; empty passwords take the stored ones of the rule's action; nothing in the event log; reply ok +
  message).
- **Notifications** (bundled always-on package `oadm.notifications`,
  `plugins/Oadm.Plugins.Notifications`, server only, plugin `README.md`): **Send HTTP request** (method, URL, query and
  header rows, login None / Basic / Digest / Bearer token, body type None / JSON / XML / Text / Form in the code editor,
  "Allow untrusted certificates", timeout, success = 2xx) and **Send email** (MailKit; mail server, port, STARTTLS / TLS /
  none, optional login, "Allow untrusted certificates", From, To, Subject, Format Text / HTML; the server settings live in
  each action, no shared setting) and **Send TCP message** (host, port, Text / JSON / XML / Hex bytes, line end, optional TLS
  with "Allow untrusted certificates", "Wait for an answer" + "Answer must contain", timeout). All three run without devices,
  once each time the rule turns on or fires, optionally also when the rule turns off.
- **First actions**: **Set output** (bundled always-on package `oadm.outputs`, `plugins/Oadm.Plugins.Outputs`, server only):
  output number 1..64, modes Follow the rule (stateful rules only; inactive on the off edge), Pulse N s (1..3600), Set
  active, Set inactive, Toggle; I/O port management API (`io-port-management`: getPorts, setPorts, setStateSequence; active
  = state differs from the normal state; inputs refused; the log names the port, "Output 1 (RELAY) active") when listed,
  else `port.cgi` (`action=n:/`, `n:\`, `n:/ms\` (answers after the pulse: the action asks for the pulse + 15 s through
  `IActionPlugin.MaxRunTime`), `checkactive=n` for Toggle; `checkoutput` is refused by the A9210). Output n = port "n-1".
  Verified with the opt-in `OutputHardwareWriteTests` (`OADM_OUTPUT_WRITE_URL` / `_USER` / `_PASSWORD` / `_PORT`) on an
  AXIS A9210 (AXIS OS 12.10.73.2, io-port-management 1.1, output 1 = RELAY), every mode on both APIs. **Run VAPIX command** (contributed by VAPIX
  Commander, available while it is on): a saved command (stored values and passwords) or a library command whose fields all
  have defaults, optional second command when the rule turns off, "Allow dangerous commands" required for dangerous ones;
  device safety like a rollout (cached API list, fresh one before a write). The event's values reach the command: a text field of the saved command whose value holds placeholders ("{carID}", "{port}", "{device}")
  is filled before the field checks and the send (`RunCommandAction.FillEventValues`; passwords never). Event type
  descriptions list their values one per line ("Values:" then "• {name}"), shown in the info icon of the rule dialog.
- **Client**: rail page **Event rules** (key `HostPages.EventRules`, icon `zap`, group Automation before the plugin pages of
  that group; `EventRulesViewModel` + `EventRulesView`): rules on top, the **Event log** card below with a splitter, like the Devices
  page and its tasks (no tabs). **Rules** (toolbar New rule first, Edit, Duplicate,
  Delete (red, confirmed); search on the right; grid On check box (administrators), Name, When, Then, State chip (On ok, waiting accent, problem
  warning), Last run; double click edits; search; status "3 rules · 2 turned on · 1 active now"; live through
  `WatchRules`) and **Event log** (title, Refresh, **Delete all** (administrators, red, confirmed; gRPC `ClearLog`, audited
  "Cleared event log"), search, virtualized grid Time, Type, What, Device, Rule, Result chip + message (events and rule edges
  the blue info icon, Done ok, Failed error, Skipped warning; a skip message does not repeat "Skipped");
  newest 10,000, live through `WatchLog`, read and watched again after a drop with the rules' backoff (1, 2, 4 ... 15 s);
  errors of rule actions stand below the rules list, errors reading the log below the log). Rule dialog `RuleEditorWindow` (`RuleEditorViewModel`), a wizard with
  the steps as a segmented switch (a step opens when every step before it is complete): **1 Rule** (Name and Turned on in
  one row, Limits), **2 When** (Conditions: "When" / "And" rows, Add event, Add schedule, day toggles), **3 Then** (Actions:
  "Then" rows: action, On (targets), the settings form (code fields in `ui:CodeEditor`, name / value rows with Add and
  remove icons, every setting `FormField.wide`), move up / down, remove; actions without devices have **Send test** with
  the result chip next to it (`TestAction`); Add action up to five; right of the actions the **Placeholders** card, only while an action of the rule writes texts
  (`IActionPlugin.UsesPlaceholders`, `ActionEntry.uses_placeholders = 11`: Send email, Send HTTP request, Send TCP message; a list with copy buttons): "Event: <trigger>" with the trigger's values, then Rule and Device from
  `ActionPlaceholders.Fixed`, each "{name}" + a short description + a copy icon; window 1160 wide), **4 Overview** (one
  short line per part: Name, State, When, And, Then, Limits; Save). Footer Back / Next (disabled with the step's first
  problem as tooltip) and Save on the overview. The devices of a condition or an action have no mode drop-down: one purple
  button that says what is chosen ("Choose devices" while nothing is, else "Selected devices: All" / "Selected devices: Tag
  Building A and 3 devices", `DeviceSelectorTexts.Brief`; the addresses as tooltip; the same button in the trigger editor)
  opens the shared `DevicePickerWindow` with every option (`DevicePickerOptions.Full`): "The same device" / "The
  device of the event", All devices, or chosen devices and tags together; the event's and the action's description sit in an info icon right of their
  select; the When / And / Then rows are outlined (`Border.ruleItem`, no fill). Server: `DeviceSelectorMode.Devices` (and the older `Tags`) = the listed devices
  plus every device with one of the tags (`DeviceSelector.ChoiceMatches`, `IsEmptyChoice`: "choose at least one device
  or tag"). The limits are **Dead time (s)** and **Max per minute** with their explanation in an info icon
  (`FormField.Info`); errors below their field; a server refusal below the overview. Fake mode (`FakeOadmApi.Events.cs`): catalog, three sample rules,
  a sample log.
- **Tests**: `tests/Oadm.Core.Tests/Events` (the engine with a fake clock), `tests/Oadm.Server.Tests/EventServiceTests`,
  `tests/Oadm.Plugins.Outputs.Tests` (+ opt-in `OutputHardwareWriteTests`), `tests/Oadm.Plugins.Notifications.Tests`,
  `RunCommandActionTests` (VAPIX Commander), `tests/Oadm.Client.Tests/EventRulesTests` (5,000 devices).

# Tasks

- **A task always targets exactly one device.** Running a plugin on N selected devices creates
  N tasks (one Run, one shared BatchId, the payload shared in memory), so one failing device
  never marks the others as failed. The task state is the device state.
- States: Queued, Running, Done, Failed, Cancelled, DoneWithWarnings (the plugin called
  `ReportWarning`; a failure wins over a warning). Persisted, visible in the tasks pane, history
  kept until the user deletes it or the retention removes it (finished tasks older than
  `Tasks.RetentionDays` = 90 or beyond the newest `Tasks.MaxHistory` = 50,000; hourly, see Settings).
- **Steps: a task always tells the user exactly what it is doing.** Every device request and every
  wait of a task plugin is its own short, imperative, named step ("Read users", "Upload firmware",
  "Wait for the device to come back"); five requests = five steps. Plugins plan their steps up front
  (`PlanSteps`, shown Pending) and may add steps on the way; steps that do not apply are Skipped with a
  reason ("Keep unchanged"). Engine rules: beginning a step completes the running one; when the plugin
  returns, a running step ends Done and planned steps never reached end Skipped ("Not run."); when it
  throws, the running step (or the one its `using` just ended) ends Failed with the exception message and
  pending ones Skipped ("Not run: an earlier step failed." / "... the task was cancelled."; a cancelled
  running step is Failed "Cancelled."). The running step is also the device message ("Upload firmware -
  12 of 80 MB"). When the task succeeds (Done or Done with warnings) the engine appends a final step
  **Completed** (Done, `TaskStepList.CompletedStepName`, also for plugins without own steps), so the
  tasks pane reads just "Completed"; Failed and Cancelled tasks get none (the failed step stays
  current). Overall progress = steps with equal weights, the running one with its own progress,
  unless the plugin calls `ReportProgress` (then that value wins). Step changes go to the change feed
  immediately and are persisted with the throttled writes (max 1/s per device) plus every state
  transition. Recovery after a server restart marks a Running step Failed ("Server stopped while the
  task was running.") and Pending ones Skipped.
- **Task names: the task list says exactly what a task does** ("Add user joe", "Set static IP
  10.0.0.60", "Upgrade firmware to 12.11.77 (factory default)"), never the plugin's menu name: the engine
  names each task with `ITaskPlugin.GetTaskName(payloadJson)` once per Run (trimmed, trailing "..."
  stripped, at most `TaskPluginNames.MaxTaskNameLength` = 48 characters, longer names shortened with
  "…" and a warning logged; empty or throwing = `DisplayName`). Names never contain secrets.
- Task engine parallelism: at most `Tasks.MaxParallelPerPlugin` running tasks (= devices) per plugin, a
  **server setting** (default 16, 1..256, Settings page "Parallel tasks per plugin"). A plugin's
  `ITaskPlugin.MaxParallelDevices` can only lower it: limit = min(setting, MaxParallelDevices) (firmware
  sets 4: large uploads share the server's link). Further tasks wait in Queued (FIFO per plugin,
  `Core/Tasks/PluginSlots`) and a cancel while queued ends them as Cancelled ("Cancelled before start.").
  The engine reads the setting live (`TaskParallelismSetting` -> `TaskEngineOptions.MaxParallelTasksPerPluginSource`)
  each time a queued task could start: a change applies without a server restart to tasks that start
  afterwards (a raised limit starts queued tasks at once via `TaskEngine.RescheduleQueued`; a lowered one
  never interrupts running tasks, new ones start when fewer run). The former configuration value
  `Oadm:MaxParallelTasksPerPlugin` is no longer read. Example: a Restart on 5,000 devices at about 90 s
  each takes about 8 hours at 16, about 1 hour at 128. Cancellation via
  `CancellationToken`, exceptions become `Failed` with message (also logged as an Error entry),
  never crash the server. A Run on N devices publishes N Added changes and writes all N tasks in
  one store transaction (`ITaskStore.AddRangeAsync`) before any of them starts; the engine overhead
  is measured at about 9 ms per task with SQLite (5,000 trivial tasks in about 45 s).
- Persistence: every state transition writes the snapshot plus new log entries; a progress
  report with a message, a warning or a log entry writes at most once per second per device
  (the message survives as the device result). Payloads are never persisted or logged.
- Task log: `ctx.Log(level, message)`, warnings, failures, precondition errors and credential
  changes; live from memory while the task runs (`GetLog`), from TaskLogEntries afterwards.
- No scheduling, retry or recurrence.

# Plugin System

Two kinds of plugins, one packaging format, one loader.

- **Task plugins**: appear in the device context menu and optionally the toolbar. Run
  directly or open a dialog first. Executed on the server per device.
- **Core plugins** (first one: Snapshot report): long-running services with their
  own UI page in the navigation rail, e.g. NTP server, DHCP server, IDP. They get full access
  to the device table and VAPIX via `ICorePluginContext`. **A Core plugin can also contribute
  Task plugins**: `ICorePlugin.TaskPlugins` returns the Task plugins it owns. Example: a PKI
  Core plugin runs an internal CA with its own page, and contributes a "Deploy certificate"
  task that issues a cert for the selected cameras and installs it over VAPIX. Likewise an
  NTP Core plugin contributes "Use this NTP server". The loader registers contributed tasks
  exactly like standalone Task plugins, so the context menu does not care where a task came
  from, and a contributed task can reach its owning Core plugin's state via the context.

## SDK (Oadm.Sdk, no UI dependencies)

The interfaces with their members and comments are the code: `src/Oadm.Sdk/Plugins/Plugins.cs`,
`src/Oadm.Sdk/Plugins/TaskSteps.cs`, `src/Oadm.Sdk/Devices/IDeviceInfo.cs`; usage and samples in `plugins/README.md`.
New members are default interface members (DIM), so older plugins keep loading; a DIM that the host does not offer
returns null or throws `NotSupportedException`.

- `IPlugin`: `Id` ("oadm.restart"), `DisplayName`, `IconKey`.
- `IDeviceInfo`: the read-only device view for plugins (server and client): id, serial, address, host name, model,
  firmware, status, category, `HasVideo`, `Apis` (API list of the last full refresh), `CredentialUserName` (never a
  password), certificate end and trust, `DhcpEnabled` / `HttpsEnabled` / `Dot1xEnabled` (null = not known), `Tags`.
- `ITaskPlugin`: `Group` (context menu submenu, `TaskGroups`), `ShowInToolbar`, `RequiresDialog`, `CanRun` (cached data
  only) and `NotSupportedReason` (only when `CanRun` is false: plain language, what is missing and what to do, a device
  detail last as " (this device ...)"; empty or throwing = "Not supported on this device"; helpers `TaskSupportReasons`),
  `MaxParallelDevices` (only lowers the setting), `ShowInMenus` (false: started by its core plugin only), `GetTaskName`
  (see Tasks), `Permissions` (`PermissionDefinition` {Key, Name, Description, OperatorDefault, ViewerDefault,
  FullDeviceControl}: finer rights inside the task, "task:<id>:<key>"; empty = one "task:<id>") and
  `RequiredPermissions(payload)` (the keys one run needs; no answer = all declared), `ExecuteAsync`. Optional
  `ITaskPluginQuery.QueryAsync`: read-only reads for the dialog (`ITaskQueryContext.Devices`: every managed device).
  Declared today: Upgrade firmware (upgrade, factoryDefault, downgrade), Users (add, update, remove), Applications (install,
  downgrade, startStop, remove, license), Import configuration (import, connection), Configure devices (configure, dangerous).
- `ICorePlugin`: `TaskPlugins` (contributed tasks), `StartAsync` / `StopAsync`, `InvokeAsync` (the page backend),
  `HasPage` (false: no rail entry), `Group` (`CorePluginGroup`: the rail group), `RequiredRole` / `IsAudited` per method,
  `Permissions` (named rights "<id>:<key>"; "show" is automatic for a page, declared only to change its defaults; empty
  = "manage" for the Admin methods) and `RequiredPermission(method)` (see "Plugin methods" under Security and access).
- `ITaskExecutionContext`: `Vapix` (pre-authenticated for the device), `Logger`, `Owner` (the contributing core plugin),
  `Files` (uploads by id), steps (`PlanSteps`, `BeginStep` -> `ITaskStep` with `ReportProgress`, `Complete`, `Warn`,
  `Skip`, `Fail`; disposing without an end = Done, an escaping exception = Failed; helpers `StepAsync`, `SkipStep`;
  `TaskStepList` is the state machine, `Oadm.Sdk.Testing.StepRun` runs a plugin with the engine's end rules),
  `ReportProgress`, `ReportWarning` (Done with warnings), `Log` (never secrets), `MarkCredentialsInvalid`,
  `UpdateCredentialsAsync`, `CreateClientForAsync` (the device at another address, caller disposes),
  `UpdateDeviceAddressAsync` and `UpdateDeviceTlsAsync` (the server verifies serial and certificate first, else
  `DeviceIdentityException` and the record stays; see "Following moved devices" and the PKI tasks).
- `ICorePluginContext`: `Devices`, `Vapix` (client factory), `Tasks` (start and cancel tasks, `IsActive`: queued or running), `Settings` (namespaced per
  plugin), `Logger`, `PluginDirectory` (never `Assembly.Location`), `Secrets` (AES-256-GCM with the master key),
  `Events` (live events to the plugin's page), `TrustAnchors` (CAs the server trusts when rating device certificates),
  `EventStreams` (a device's RTSP event stream with the stored credentials; the RTSP port per device), `Firewall` (UDP
  rules, Windows service only; `FirewallRuleKeeper`), `DataDirectory` (`<datafolder>/plugin-data/<id>`: working files, never a
  shared temp folder; the plugin cleans up), `EventPublisher` (event rules), `EventSubscriptions` (every event the server
  handles, see "Event rules"), `AutoAdd` (add or follow an Axis device like
  the add page).
- Event rules: `IEventSource`, `IDynamicEventSource`, `IEventPublisher`, `IActionPlugin`, `IActionSource` (see "Event
  rules").
- Compliance: `IComplianceCheckType` (standalone in a `*.Server.dll`, or through `IComplianceSource` of a core plugin),
  `IComplianceContext` with shared `Reads`, `ComplianceResult`; `ActionField.File` and `ChoicesFromDevice` for its
  settings (see "Compliance"). Tests: `Oadm.Sdk.Testing.FakeComplianceContext`.

Task plugin names and groups: `DisplayName` is at most `TaskPluginNames.MaxDisplayNameLength` (32) characters and never
ends with "..." (dialog tasks too); it is the menu and toolbar name only, the tasks pane shows `GetTaskName`. The server
registry strips trailing "..." / "…" and shortens longer names to 31 characters + "…" (logged); empty groups become
`General`. Bundled task plugins: Restart, Upgrade firmware, Date and time, Export and Import configuration (Maintenance),
Applications (ACAP) (Applications), Users (Users), Network settings and Assign IP address (Network), the PKI tasks
(Security, contributed by `oadm.pki`); `TaskPluginNamesTests` checks every plugin deployed to `artifacts/plugins`.

## Client SDK (Oadm.Sdk.Client)

The code: `src/Oadm.Sdk.Client/ClientPlugins.cs`, `ToolbarPlugins.cs`; usage in `plugins/README.md`. Everything runs on
the UI thread.

- `ITaskPluginDialog.ShowAsync(ctx, devices, owner)` returns the payload JSON (null = cancel); `ITaskDialogContext` gives
  `QueryAsync` (TaskService.Query), `UploadAsync` (FileService.Upload with progress), `Has(permission)` (the dialog
  hides the choices the role lacks: Users modes, firmware factory default and downgrade, application actions, the
  connection settings area, dangerous parameters) and `AllDevices` (every device of the client, e.g. a source device that is
  not selected; DIM empty).
- `ICorePluginPage.CreateView(ctx)`: the rail page; `HasOwnCards` (the view lays out its own cards), `ShowTasksPane` (the
  shared tasks pane below the page). `ICorePluginClientContext`: `InvokeAsync`, `WatchEventsAsync` (PluginService.Watch),
  `Devices` + `DevicesChanged`, `SelectedDevices`, `OwnerName`, `ShowMessageAsync`, `ConfirmAsync`, `OpenAsync`, `Owner`,
  `DeviceWeb` (`IDeviceWebAccess`: a device's web interface for an embedded browser through the server, see "Web View"),
  `LiveVideo` (`ILiveVideoAccess.Create(LiveVideoPlace)` -> `ILiveVideo` {View, Start(device, camera), Close, Camera,
  CameraChosen}: the host's live video component, `Oadm.Client/LiveView/LiveVideoAccess`; the page's videos are disposed
  with the page; see "Live View"), `OpenDownloadAsync(method, payload, ct)` (a file of the server part through
  `PluginService.Download`: `PluginDownload` {Content, FileName, Length}; the page reads the stream and disposes it),
  `Has(permission)` + `PermissionsChanged` (pages hide or lock what the role lacks, e.g. Save of the NTP / DHCP server),
  `GetLocalSetting` / `SetLocalSetting` (short texts a plugin keeps on this computer, `ClientSettings.PluginSettings`,
  at most 64 KB each; e.g. a page's last device choice), `IsPluginOn(packageId)` (from the client's package list, null
  when unknown; Dashboards' empty state), `OpenAsync("plugin:" + id)` opens another plugin's page while it is on.
- `IToolbarPlugin` (`Id`, `Order`, `Group`: Add, Manage, Tasks, Plugins; `CreateControl` once; `RequiredPermission`:
  the entry hides while the role lacks it) with `IToolbarContext` (selection, devices, task plugins, `CanRunTask` /
  `CannotRunTaskReason`, `RunTaskAsync`, `OpenAsync`, `RemoveDevicesAsync`, `RefreshDevicesAsync`, `QueryAsync`,
  `UploadAsync`, `InvokePluginAsync`, `Has` + `PermissionsChanged`, messages).

### Toolbar plugins

The Devices page toolbar is made of toolbar plugins. Built in (compiled into the client,
`Devices/Toolbar/BuiltInToolbarPlugins.cs`, registered in the container like plugin parts): **Add** (`oadm.toolbar.add`,
group Add, primary, icon `add`, a `MenuFlyout` with Discovery (`HostPages.AddScan`, icon `search`), Network range
(`AddIpRange`, `range`), Add manually (`AddManually`, `add`), Import from file (`AddImport`, `download`) and Import from ADM
(`AddImportAdm`, `download`): see "Add Devices Page"), Remove (Manage; confirmation, the context menu
uses the same flow), **Refresh** (Manage, order 5, icon `refresh`, enabled with a selection; `IToolbarContext.RefreshDevicesAsync`
= one `DeviceService.Refresh` for the whole selection: the server queues a full refresh per device; the context menu entry
"Refresh" uses the same flow), **Export** (Manage, order 10, icon `export`, `HostPages.ExportDevices`, the selected devices, or every device the search shows when none is selected, the
tooltip says which; `Devices/DeviceListCsv`: header + every grid column as text (MAC address, Status, Address,
Host name, Model, Firmware, SoC, Category, Product type, DHCP, HTTPS, Certificate expires as ISO date yyyy-MM-dd,
Certificate, IEEE 802.1X) in the grid's unsorted order, RFC 4180 quoting (`Infrastructure/Csv`), CRLF, UTF-8 with
BOM, a cell starting with = + - @ gets a leading `'` (no formula injection), no password (the client has none);
save picker `IDialogService.SaveFileAsync`, default name `oadm-devices-<yyyy-MM-dd>.csv`; nothing to export or a
failed save = `ui:MessageWindow`; O(n), 5,000 devices tested), one generic plugin with a button per task plugin that declares
`ShowInToolbar` (Tasks; enabled when it can run on the whole selection, otherwise disabled with the reason as tooltip) and **AXIS OS - Release Notes** (Plugins,
order 1000, an icon-only `ui:ToolbarButton` (icon `externalLink`, class `trailing`: right of the toolbar next to Group by
tag and Columns), opens https://help.axis.com/en-us/axis-os-release-notes in the default browser). External ones come from
`*.Client.dll` like dialogs and pages. `DeviceToolbar` orders them by group, order, id, creates each
control once (failures logged, entry left out), keeps a `ToolbarSeparator` only between groups that
show something and moves the same controls into a new page view. Buttons are `ui:ToolbarButton`
(icon key + text, `IsPrimary`). The Columns button and the search box are host parts. Sample:
`tests/TestPlugins/Oadm.TestPlugins.Sample.Client` (deployed to `artifacts/test-plugins/oadm.sample`).
API guide: `plugins/README.md`.

Dialogs and pages are real Avalonia views with view models, styled by the host theme. They use
the shared controls from `Oadm.Sdk.Client.Controls` (`IconLabel`, `SearchBox`, `PasswordBox`, `OadmIcon`,
`DialogTitleBar`, `CardHeader`, `DialogFooter`, `StatusChip`, `FileRow`, `ProgressRow`, `FormField`, `StatCard`,
`ToolbarButton`, `ToolbarSeparator`, `ToolbarOverflowPanel`, `MessageWindow` (every message and confirmation popup:
`MessageWindow.ConfirmAsync` / `ShowMessageAsync`, used by the host dialog service and plugin dialogs);
usage in `plugins/README.md`) and
the theme resources (styles, colors, `Icon.*` geometries) of the host application; the icon keys
are listed in `plugins/README.md`. Plugins never talk to devices from the client; payloads go to
the server. gRPC errors reach the dialog as `RpcException` (Status.Detail is the message).

`VapixRequestOptions.Timeout` (`HttpRequestOptionsKey<TimeSpan>` "Oadm.RequestTimeout") on a
request passed to `IVapixClient.SendAsync` overrides the default 15 s timeout for that request
(firmware and ACAP uploads use minutes; plugins use the SDK constant, never their own key); request
bodies are streamed, never buffered. `VapixRequestOptions.StreamResponse` (`HttpRequestOptionsKey<bool>`
"Oadm.StreamResponse") returns the answer after its headers without buffering it (`HttpCompletionOption.ResponseHeadersRead`):
the caller reads the body as a stream (e.g. into a file in `ICorePluginContext.DataDirectory`), enforces its own size
limit and bounds the body read with its own token (the timeout covers the headers only); the 16 MB limit of buffered
answers is unchanged. Used by the System report for server reports.

## Loading and packaging

- Server scans `<app>/plugins/*/`, `<datafolder>/plugins/*/` and, in a repository checkout,
  `<repo>/artifacts/plugins/*/` (`PluginPaths.Development`) on startup, loads each
  `*.Server.dll` in its own collectible `AssemblyLoadContext`, resolves `ITaskPlugin` and
  `ICorePlugin` implementations via reflection, then registers the Core plugins' `TaskPlugins`
  too. Client does the same for `*.Client.dll` with `ITaskPluginDialog`, `ICorePluginPage` and
  `IToolbarPlugin` over the same three roots (`ClientPluginLoader.DefaultRoots`).
- Plugin development without installing (developer guide `website/src/content/docs/dev/plugin-sdk.md`: NuGet packages, templates, the `oadm` tool): server
  `--Oadm:PluginDir=<folder>` (several separated by ";", `OadmServerHost.PluginDirKey`) and client `--plugin-dir <folder>`
  (repeatable, `AppOptions.PluginDirs`) load a plugin package straight from a build output, before every other folder, so it
  wins over an installed copy of the same package (the server's later copy is a load error "already loaded from another
  folder", the client skips it); as a service the server ignores the option and logs a warning.
  `--print-folders` (server `Hosting/FolderReport`, client `Plugins/ClientFolderReport`) prints the data, logs and plugin
  folders, the deploy folder and (server) the service name as JSON and exits without opening anything. Every release
  also has the portable `OADM-Server-<version>-<rid>.zip` and `OADM-Client-<version>-<rid>.zip` (the publish folders,
  job `installers`). The `oadm` tool (`tools/Oadm.Tools`) runs them as a development server (`oadm dev start`: own data in
  `.oadm/server-data` of the plugin, `https://localhost:5090`, every `.oadm/plugins/*`, restarted when a build changes the
  files the server loads) and client, deploys into and debugs with the installed OADM (one elevation; the service
  definition is read from the service manager and the folders follow the server's own rules: **an installed program is
  never run to ask**, an older one would start on the live data), packs `.oadmplugin` files and makes license keys.
- Shared assemblies (server: `Oadm.Sdk`, `Oadm.Sdk.Client`, `Microsoft.Extensions.*`; client: also Avalonia, the MVVM
  toolkit, SkiaSharp, HarfBuzzSharp) resolve to the host's copy whatever version a plugin was built against
  (`PluginLoadContext.FromHost`, the client's own copy in `ClientPluginLoader`): `minSdkVersion` decides, so plugins built
  with the NuGet packages of another release load; one the host does not have comes from the plugin folder. Server and
  client load a plugin's main assembly from memory with its pdb (`LoadFromFileWithoutLock`, `LoadWithoutLock`), the client
  also every other assembly of the plugin's own folder (a page's reference to its server part): a build never meets a
  locked file. Windows: a console run on the service's data folder (`oadm debug --installed`) finds the service's TLS key
  in the machine key store and keeps it (`ServerTlsCertificate.OpenWithNamedKey`), never a new certificate.
- Plugin folder: `plugin.json` (id, version, minSdkVersion, displayName, description, optional `enabledByDefault`,
  default true, and `alwaysOn`, default false),
  `<Name>.Server.dll`, optional `<Name>.Client.dll`, private dependencies. SDK assemblies are shared from the
  host and never copied into the plugin folder.
- Plugin failures (load or execution) are logged and isolated; a broken plugin never
  prevents server or client from starting.

### Plugins on and off

The rail page **Plugins** lists every plugin package (one plugin folder = one row: its core
plugins, contributed and standalone tasks and client parts together) and administrators turn each on or off; the change
applies at once without a restart. Own rail page (not a Settings card); core functionality
is always on.
- Default: the manifest's `enabledByDefault` (`PluginManifest.EnabledByDefault`, `PluginOrigin.EnabledByDefault`).
  **Off by default: Action Rule Editor, Dashboards, Device triggers, Edge Recording Export, Edge storage, Hardening scan, Image Health Dashboard, I/O Monitor, Live View and Metadata Monitor**. The administrator's choices are the
  server setting `Plugins.Enabled` (JSON object package id -> on; packages without a choice use their default),
  applied before the core plugins start (`Oadm.Server.Plugins.PluginActivation.LoadAsync`).
- **Always on** (manifest `alwaysOn`, `PluginOrigin.AlwaysOn`): Restart, Upgrade firmware, Date and time, Applications
  (ACAP), Users, Network settings, PKI, System report, Device configuration, Outputs and Notifications (core functionality). Enabled whatever
  the stored choice says; `SetEnabled` throws, `SetPackageEnabled` answers FAILED_PRECONDITION "<name> is always on.".
  Switchable: Snapshot report, VAPIX Commander, Metadata Monitor, NTP server, DHCP server, Hardening scan, Image Health
  Dashboard, I/O Monitor, Device triggers, Web View, Live View, Dashboards, Edge Recording Export, Edge storage, Action Rule Editor. Third-party plugins are switchable unless their manifest says otherwise.
- Manifest `description` (`PluginOrigin.Description`): one short sentence shown on the Plugins page; every bundled
  plugin has one.
- `PluginRegistry`: `TaskPlugins`, `CorePlugins`, `TryGetTaskPlugin`, `TryGetCorePlugin` see only enabled packages
  (so ListCorePlugins, ListTaskPlugins, Run and Query skip a package that is off; the runnable cache follows the plugin
  set); `AllTaskPlugins` / `AllCorePlugins` / `Packages` (`PluginPackageState`: origin, enabled, has page, menu task
  count) see all. `SetEnabled` + `CorePluginHost.SyncEnabledAsync` (once a change starts, storing it and starting / stopping the plugins
  always finish, also when the caller cancels; a failed store undoes it; the audit entry is written either way): core plugins of a package turned off are stopped
  (trust anchors removed), turned on are started. Invoking a core plugin that is off: FAILED_PRECONDITION "The <name>
  plugin is turned off.". Tasks of the package already queued or running finish.
- gRPC `PluginService.ListPackages` (any user: clients hide toolbar entries of packages that are off) /
  `SetPackageEnabled` (Admin, audited "Turned plugin on" / "Turned plugin off", target the plugin name);
  `PluginPackageInfo.always_on = 8`, `description = 9`, `group = 10` (`PluginPackageState.Group`: the rail group of the
  package's core plugin with a page, else of its first core plugin; without a core plugin "Extensions", like the rail).
- Client: `Plugins/PluginPackageStore` (loaded on every connect; `Changed` reloads the rail pages and the task catalog
  and hides the toolbar entries of packages that are off: `DeviceToolbar.ApplyPackageStates`, the package of a client
  part from the plugin.json next to its dll, `IClientPluginRegistry.PackageOf`). Rail page **Plugins** (Admin only,
  key `HostPages.Plugins`, icon `plugin`, first of the bottom entries with a divider below it; subtitle "A plugin that is off shows no page, menu entry or
  toolbar button."; `Settings/PluginsPageView` + `PluginsViewModel`): one card with a DataGrid On (check box), Plugin,
  Description, grouped like the navigation rail (one collapsible group per rail group that has
  plugins, in rail order, the same look as the device grid's tag groups without color, theme `DataGrid.groups`; sorted by
  name inside a group; the view groups in code-behind with `DataGridCollectionView` + `PluginsViewModel.Groups`);
  always-on packages are not listed; a refused change is undone with the reason below the list. Other clients see a change
  on their next connect. Fake mode: `FakeOadmApi.Plugins.cs` (PKI, Restart, System report always on).
- Tests: `tests/Oadm.Server.Tests/PluginPackageTests` (also the bundled manifests: off by default, always on,
  descriptions), `tests/Oadm.Client.Tests/PluginListTests`, `ShellTests`.

## Plugin licenses

Commercial plugins (decision 2026-10-17): OADM checks, the plugin decides. User and developer docs:
`website/src/content/docs/dev/licenses.md`, `admin/plugins.mdx` "Licenses".
- **Manifest** `license` (`Oadm.Sdk.Licensing.PluginLicenseManifest`): `publicKey` (base64 SubjectPublicKeyInfo of an ECDSA
  P-256 key), optional `features`, `trialDays` (from the day the server first loaded the package; 0 = none). Without it the
  plugin is free. No buy link.
- **License key** (`LicenseFile`, decision 2026-10-17: one line, no raw content in the UI): `OADM1.<content>.<signature>`,
  base64url; content = JSON {plugin, server, customer, issued, expires|null, features (optional), limits (optional)}; the
  signature (ECDSA P-256, SHA-256) covers the content bytes; spaces and line breaks are ignored; a file (`.oadmlicense`)
  holds the same line; checked offline: signature, plugin id, server ID, last day (server local date). At most 16 KB.
  `LicenseFile.Peek` reads the content unchecked (the dialog's summary).
- **Server** (`Oadm.Core.Plugins.PluginLicenses`, initialized at start step 4a before any plugin runs, re-checked hourly by
  `PluginLicenseRefreshHostedService`): one live `IPluginLicense` per package (`PluginRegistry.Licenses`), handed to
  `ICorePluginContext.License`, `ITaskExecutionContext.License`, `IActionContext.License` (DIM null); `Changed` when the
  state changes. States `LicenseStatus`: NotRequired, Valid, Trial, TrialEnded, Missing, Expired, WrongServer, Invalid;
  `IsValid` = Valid, Trial or NotRequired; `Has(feature)` (empty list = every feature; always during the trial), `Limit(name)`
  (null = none or trial, 0 without a valid license). A stored license that turns invalid while the trial still runs counts
  as Trial. Only a Valid license is stored.
- **gRPC** `PluginService`: `PluginPackageList.server_id = 2`, `PluginPackageInfo.license = 11` (`PluginLicenseState`: status,
  reason, customer, issued, expires, features, limits, trial_ends, available_features); `SetPackageLicense(id, license)` and
  `RemovePackageLicense(id)` (Admin, audited "Entered plugin license" / "Removed plugin license"; INVALID_ARGUMENT with the
  reason, NOT_FOUND, FAILED_PRECONDITION for a free plugin).
- **Client**: Settings page, card Server: "Server ID" + copy, always (`ServerSettings.server_id = 23`, read only). Plugins
  page: "Server ID" + copy at the top and a **License** column, both only while a plugin needs a license: status chip
  (Licensed ok, Trial warning, else error; reason as tooltip), Enter / Change license, Remove (red, confirmed); a licensed
  package is listed even when always on. Dialog `Settings/PluginLicenseWindow` (`PluginLicenseViewModel`, single operation):
  server ID + copy, "License key" (one-line text box, mono), its hint = what the key says ("ACME GmbH, until 2027-12-31,
  reports") or where it comes from, Choose file, the format error or the server's refusal below the field.
  `ICorePluginClientContext.LicenseOf(packageId)` for pages. Fake mode: the sample package `example.site-reports` (trial,
  `FakeOadmApi.SampleLicenseKeys`, server ID `FakeOadmApi.FakeServerId`).
- **Vendor tool** `oadm license keys [--out]` (never overwrites a key; prints the project properties `OadmLicensePublicKey`,
  `OadmLicenseFeatures`, `OadmLicenseTrialDays` and the plugin.json entry) and `oadm license sign --key --plugin --server
  --customer [--expires] [--features] [--limit name=n]... [--out]` (`tools/Oadm.Tools`; `manage license ...` passes the
  arguments unchanged).
- Tests: `tests/Oadm.Server.Tests/PluginLicenseTests`, `tests/Oadm.Tools.Tests`, `tests/Oadm.Client.Tests/PluginListTests`.

## Plugin documentation

Every plugin has its own `README.md` in its folder (`plugins/<name>/README.md`): decisions, page and dialog, steps and
task names, device API and verified device behavior, settings, tests. CLAUDE.md keeps a short entry per plugin: id, what
it does, rail or menu group, on or off by default, the rules that must never break (device safety, secrets, roles,
limits) and a pointer to the README. A new plugin gets both in the same change; a changed decision updates both.

## HARD RULE: device safety for task plugins

Any plugin that changes a device (users, network, firmware, ACAP, ...) must never risk
breaking it:
1. **Compatibility first.** Declare the VAPIX API ids and minimum versions it needs. `CanRun`
   checks them against the cached `device.Apis` (from `apidiscovery.cgi getApiList`, refreshed
   on every full refresh); `ExecuteAsync` re-checks with a fresh `ctx.Vapix.GetApiListAsync()`
   and `Require(...)` BEFORE the first write. A different major version is a different API.
   Unsupported devices fail with `DeviceNotCompatibleException` ("Nothing was changed").
2. **Validate before writing.** Validate all input (addresses, masks, passwords, files)
   before touching the device. Prefer device-side dry runs or validation methods when the API
   offers them.
3. **No lock-out.** Never remove or demote the account OADM itself uses, never apply network
   settings that make the device unreachable without a clear warning in the dialog.
4. **Secrets.** Payloads are kept in memory only, never persisted, never logged.
5. **Read-only queries.** `ITaskPluginQuery` is read-only by contract.
6. **Hardware tests** for write operations are opt-in only (`Category=HardwareWrite`, never in
   `manage test hardware` or `manage test all`), and never run against a device without the developer explicitly asking.

Shared SDK pieces for this: `DeviceApi` + `DeviceApiExtensions.Supports/Require`,
`IVapixClient.GetApiListAsync`, `IDeviceInfo.Apis`, `ITaskPluginQuery` (dialog reads current
state through `ITaskDialogContext.QueryAsync`), `IUploadedFiles` (dialog uploads through
`ITaskDialogContext.UploadAsync`, task reads by id), `ReportWarning` (Done with warnings) and
`Log` (persisted per-task log).

## Task plugins

Every task plugin describes its steps, task names, dialog and device API in its own `README.md` (`plugins/<name>/`).
Common to all: every device request and every wait is a named step; every successful task ends with the engine's step
**Completed**; names follow "Tasks" (no secrets, at most 48 characters).

- **Restart** (`oadm.restart`, always on, toolbar and context menu, no dialog): `restart.cgi`, then waits until the
  device went offline and answers again (3 min); a different serial afterwards is a Warning. Task name "Restart device".
- **Users**, **Upgrade firmware**, **Date and time**, **Applications (ACAP)**, **Network settings**, **Assign IP
  address**, **Export / Import configuration**: in their READMEs. A new static address (IPv4, IPv6) is probed
  before the first write (`IAddressProbe`: ping, 2 tries x 1 s, plus TCP 80 / 443; the device's own addresses are not
  probed); an answer fails "<address> is already in use (answers ping). Nothing was changed.".

## Network settings and Assign IP address

`plugins/Oadm.Plugins.Network` (+ `.Client`), always on; details, the VAPIX decision table and the verified device
behavior: plugin `README.md`.

- **Network settings** (`oadm.network`, context menu Network): IPv4 / IPv6 / DNS / Host name sections, each "Keep
  unchanged" by default; several devices get their new addresses per row in the shared address table
  (`AddressAssignmentGrid`). **Assign IP address** (`oadm.network.assign-ip`, context menu and toolbar): a clone of ADM's
  "Assign IP address to selected devices": DHCP, or an IP range in ADM / ACS syntax (`IpRangeExpression`) with suggested
  addresses that skip the router, managed devices and addresses in use (query `checkAddresses`).
- Rules that must hold: a risky change (address, subnet, router, DHCP switch, IPv6 over IPv6) asks in `ui:MessageWindow`
  ("The devices may become unreachable"); conflicts block Apply / Finish; a new static address is probed before the first
  write; the address family OADM connects with is written last; after a new static address OADM follows the device (90 s,
  serial verified, `UpdateDeviceAddressAsync`; otherwise a Warning and the record stays); with DHCP the periodic re-find
  moves the record.

## Snapshot report (core plugin)

`plugins/Oadm.Plugins.SnapshotReport` (+ `.Client`), id `oadm.snapshot-report`, rail page **Snapshot report** (group
Reporting, icon `snapshot`), on by default, switchable. The yearly maintenance check: current snapshots of every video
source of the managed video devices, checked fast and exported as a PDF report (PDFsharp + MigraDoc, MIT). Details:
plugin `README.md`.

- Devices: the purple "Selected cameras" button (`PageDeviceChoice`: All devices, tags, chosen cameras; the Devices page selection to start with, else the last choice on this computer); the page reads only the cameras of the choice and nothing before one.
- Rules that must hold: read-only for devices (param.cgi reads and image.cgi snapshots with the stored credentials,
  server side); opening the page contacts no device (**Create snapshots** starts the first load); at most 4 device
  requests at a time, 3 s each; devices with Credentials required, Password not set or Certificate changed are one error
  tile without a request; error texts from the shared `DeviceMessages`; report jobs live in server memory (30 minutes
  after last use), the PDF is read in 2 MB chunks.
- Tests: `tests/Oadm.Plugins.SnapshotReport.Tests` (PDF parsed back with PDFsharp); hardware test through the in-process
  server writing the PDF to `OADM_SCREENSHOT_DIR`.

## VAPIX Commander (core plugin)

`plugins/Oadm.Plugins.VapixCommander` (+ `.Client`), id `oadm.vapix-commander`, rail page **VAPIX Commander** (group
Automation, icon `command`), on by default, switchable. Pick VAPIX commands from a library (161 bundled, `Library/*.json`)
or saved ones, fill their fields and roll them out to many devices, or send one to one device. Details: plugin
`README.md`; command format: `Schema/command-format.md` + `command.schema.json` (the contract).

- Rules that must hold: every value is checked before anything is sent; requests stay on the device (paths through
  `CommandValidator.IsDevicePath`); payloads stay in memory, passwords are never logged (`***` in request lines and
  bodies), saved passwords are encrypted with the master key and never listed or exported; a rollout checks every
  command first (nothing starts on an error), writes need a confirmation, dangerous commands a second one, and each
  device re-checks a fresh API list before a write ("Missing API x. Nothing was sent."). The response kind decides how
  success, errors and values are read (Axis devices answer errors with HTTP 200).
- Page: Library (search, tree Built-in / Saved; a click adds a command to the rollout set; **+** opens the request
  builder, the pen edits a saved command), Rollout set (grid with per-row icons, field form, Send with the result card)
  and Target devices (the shared device picker with All devices and tags, the Devices page selection to start with, compatibility from the
  cached API lists with only the problem devices listed, Stop on first error, Run on N devices); the tasks pane
  below the page. The request builder is its own dialog: start from scratch or from an existing command, send it to
  try, save (a saved command is replaced in place).
- Saved commands: plugin setting `savedCommands`, at most 500, shared by all clients, export / import as library files.
- Methods are Operator; `tryRequest` and `rollout` are audited. Scale: 30,000 target devices, every list action under
  200 ms (test).
- Tests: `tests/Oadm.Plugins.VapixCommander.Tests`, `.Library.Tests`; read-only hardware test `VapixCommanderHardwareTests`.

## NTP server (core plugin)

`plugins/Oadm.Plugins.NtpServer` (+ `.Client`), id `oadm.ntp-server`, rail page **NTP server** (group Servers, icon
`metronome`), on by default, switchable. Own RFC 5905 server-mode implementation (SNTPv4 compatible) on UDP
123, no library; contributes the task **Use OADM as NTP server**. Details: plugin `README.md`.

- Rules that must hold: requests are always answered at once from the current time source state, never waiting for the
  upstream; without an upstream OADM serves its own clock as stratum 10 (never "unsynchronized"); the OS clock is served,
  never set; an upstream is validated with one query before it is saved; abuse protection per client IP (burst 8, 1 per
  2 s, one Kiss-o'-Death RATE a minute) and 2,000 requests/s overall; a failed bind is retried every 30 s; status texts
  in plain words (`NtpStatusTexts`: port in use, permission, interface missing, upstream not answering).
- Roles: `save` is Admin and audited. The Windows service opens UDP 123 in the firewall while enabled.
- Tests: `tests/Oadm.Plugins.NtpServer.Tests` (loopback with random ports, never port 123 on a real interface).

## DHCP server (core plugin)

`plugins/Oadm.Plugins.DhcpServer` (+ `.Client`), id `oadm.dhcp-server`, rail page **DHCP server** (group Servers, icon
`network`), on by default, switchable, starts disabled. Own RFC 2131 / 2132 implementation, IPv4 only, one interface, no
relay agents; static leases, the lease list, and optionally adding Axis devices that get an address. Details, per-OS
setup and the manual test plan: plugin `README.md`.

- Rules that must hold: before a new address is offered it is probed (ping + TCP 80 / 443; static leases are not
  probed); another DHCP server on the network is checked on Save, every 10 minutes and at start, and enabling next to one
  needs an explicit "Enable anyway"; abuse protection per MAC and overall, pending offers capped at half the pool; mask,
  router, DNS and domain come from the interface, never asked; leases survive restarts; the automatic add uses the add
  page's pipeline on the server (Axis MAC blocks only, the Axis check before any credential, the credential list only)
  and follows managed devices to their new address also when it is off; tests never send DHCP on a real interface.
- Roles: `save`, `saveStatic`, `deleteStatic`, `makeStatic`, `release` are Admin and audited. The Windows service opens
  UDP 67 in the firewall while enabled.
- Tests: `tests/Oadm.Plugins.DhcpServer.Tests` (in-memory network and loopback with random ports).

## PKI (core plugin)

`plugins/Oadm.Plugins.Pki` (+ `.Client`), id `oadm.pki`, rail page **PKI** (group Security, icon `shield`), always on.
One certificate authority for device certificates (generated on first start or imported), the server's trust in it, and
the Security tasks of the context menu: Enable / Disable HTTPS, Enable / Disable IEEE 802.1X, Renew certificates, View,
Delete and Install certificates, Install CA certificates. Details: plugin `README.md`; device API research:
`tests/Oadm.Plugins.Pki.Tests/Fixtures/`.

- Rules that must hold: the CA key is stored only encrypted with the master key (without `Secrets` no CA is kept; a key
  that cannot be decrypted is the state "CA key cannot be read", nothing is overwritten); replacing the CA asks how many
  devices have certificates from the current one; the CA and its previous CAs are trust anchors, so OADM-issued device
  certificates rate **Trusted** without an OS change; keys of device certificates are created on the device, OADM signs
  with its own subject and SAN (never the CSR's); every task checks AXIS OS 11.11+ (cert v1 from a fresh
  `config/discover`) before the first write and fails "Nothing was changed" otherwise; Enable HTTPS pins the new
  certificate through `UpdateDeviceTlsAsync`; 802.1X checks the device clock first; only OADM's own certificates that
  nothing uses are removed; passwords of imports and backups are never stored or logged; nothing in tests touches a real
  trust store; View and Delete certificates read at most 5,000 devices per window (each device is read, 4 at a time; a
  larger selection gets "Choose at most 5,000 devices."), the other tasks take any number.
- Roles: everything but `getState` is Admin and audited; "Install in trusted root store" on the server asks with the CA's
  SHA-256 fingerprint.
- Tests: `tests/Oadm.Plugins.Pki.Tests` (every task against a stateful fake camera); opt-in `HardwareWriteTests`
  (`OADM_PKI_HARDWARE_WRITE=1`, not written yet).

## Metadata Monitor (core plugin)

`plugins/Oadm.Plugins.MetadataMonitor` (+ `.Client`), id `oadm.metadata-monitor`, rail page **Metadata Monitor** (group
Monitoring, icon `activity`), **off by default**. A port of the AXIS Metadata Monitor tool: pick one camera, Start, and
watch its event stream live (I/O ports, virtual inputs, storage, temperature, tampering, ACAP events). Details: plugin
`README.md`.

- Camera: a button that names the chosen camera and opens the shared device picker for one device (search; the former drop-down could not hold 30,000 devices).
- Rules that must hold: read-only for devices; the server opens the RTSP event stream with the stored credentials
  (Digest, never Basic on plain RTSP; SDK `ICorePluginContext.EventStreams`), passwords never reach the client; events
  only (`media.amp?video=0&audio=0&event=on`), one camera at a time, nothing saved; refused statuses without a request;
  a stream ends with Stop, a page switch or 20 s without the page's keep-alive; the client keeps the newest 10,000
  messages; XML through `DeviceXml` (1 MB, no DTD). The RTSP port of a camera behind a port forward is set in the
  device list (**OADM > RTSP port**); Start never changes it (an older client's port is ignored).
- Tests: `tests/Oadm.Plugins.MetadataMonitor.Tests` (+ Perf `ScaleTests`, hardware `MetadataHardwareTests`); Core
  `MetadataDepacketizerTests`, `RtspMetadataSourceTests`, `DeviceEventStreamsTests`.

## Image Health Dashboard (core plugin)

`plugins/Oadm.Plugins.ImageHealth` (+ `.Client`), id `oadm.image-health`, rail page **Image Health Dashboard** (group
Monitoring, icon `eye`), **off by default**. One table with the AXIS Image Health Analytics status of every chosen camera that
has the app, with five count cards (Good, Detected, Pending, Detection off, Errors). Details: plugin `README.md`.

- Devices: the purple "Selected cameras" button (`PageDeviceChoice`: All devices, tags, chosen cameras; the Devices page selection to start with, else the last choice on this computer); the page reads only the cameras of the choice and nothing before one.
- Rules that must hold: read-only for devices (`GET /local/AXISImageHealthAnalytics/v1/status` with the stored
  credentials); only on request and for the chosen cameras (the page opens, Refresh, Auto refresh every 10 s while the page is shown, off by
  default), nothing in the background and no events (event rules use the app's own events through Device triggers); 24
  cameras at a time, 3 s each; a stopped app stays as **Not running**; a detection turned off is no problem (the camera
  counts as Good); refused statuses without a request. Methods `check` and `getState` are Operator.
- Tests: `tests/Oadm.Plugins.ImageHealth.Tests` (+ read-only hardware test `ImageHealthHardwareTests`). Not in fake mode.

## I/O Monitor (core plugin)

`plugins/Oadm.Plugins.IoMonitor` (+ `.Client`), id `oadm.io-monitor`, rail page **I/O Monitor** (group Monitoring, icon
`toggle`), **off by default**. A simple, fast live display of the inputs and outputs of one device, drawn as a faceplate;
no events, no actions, no switching. Details: plugin `README.md`.

- Device: a button that names the chosen device and opens the shared device picker for one device (search; the former drop-down could not hold 30,000 devices).
- Rules that must hold: read-only (`getPorts` of `io-port-management` 1.x every 200 ms while a page shows the device, 3 s
  per request, 1 s pause after a failure); one reading per device shared by every page; stopped after 20 s without the
  page's keep-alive; at most 20 devices at once; refused statuses without a request; the VAPIX client always from the
  factory, never kept or disposed.
- Tests: `tests/Oadm.Plugins.IoMonitor.Tests` (+ read-only hardware test `IoMonitorHardwareTests`).

## Device triggers (core plugin)

`plugins/Oadm.Plugins.DeviceTriggers` (+ `.Client`), id `oadm.device-triggers`, rail page **Device triggers** (group
Automation, icon `flame`), **off by default**. A trigger is an event the chosen devices offer (I/O ports, virtual inputs,
temperature, storage, Object Analytics scenarios, other apps), optionally narrowed per source item; every trigger is an
event of the event rules, plus "<Trigger>: connection lost". Decisions, device research and details: plugin `README.md`.

- Rules that must hold: the event list is read live from the devices for every create and edit, never cached; devices
  come from the shared device picker (All devices, tags, single devices), resolved after every change and every 30 s; all
  turned-on triggers together watch at most 10,000 devices (`MaxWatchedDevices`; one permanent connection each, Windows
  has 16,384 outgoing ports by default): a save or turn-on over it is refused ("Device triggers watch at most 10,000
  devices. Choose tags or fewer devices."), a tag or All devices that grows past it keeps the devices it watches and the
  row says "More than 10,000 devices: some are not watched"; devices the status poll found not answering are not
  contacted (no connection attempts in an outage; the device connects again once a poll sees it Ok); one
  permanent WebSocket per device (session token from `wssession.cgi`, the pinned certificate), at most 16 connecting at
  once, a new filter only where triggers changed; a refused filter is a problem of its own triggers only; turned-off and
  empty triggers report off at once (rules never wait for a report that cannot come); a problem keeps the last state and
  turns "connection lost" on after 30 s; refused statuses are not contacted. Administrators change triggers (audited),
  operators see them.
- Tests: `tests/Oadm.Plugins.DeviceTriggers.Tests` (fake camera speaking the WebSocket protocol); opt-in write test
  (virtual input 64, Object Analytics test alarm; `OADM_TRIGGER_URL` / `_USER` / `_PASSWORD`, `OADM_TRIGGER_OA_SCENARIO`).

## Action Rule Editor (core plugin)

`plugins/Oadm.Plugins.ActionRules` (+ `.Client`), id `oadm.action-rules`, rail page **Action Rule Editor** (group
Automation, icon `workflow`), **off by default**, switchable. The devices' own action rules (their web interface: Events >
Rules, Recipients, Schedules) of every device in one page, grouped by what they do (same trigger and action; or by name,
action, trigger), and changed, turned on or off, copied to other devices, deleted and undone for many at once, e.g. "Send
notification through TCP" to "... while the rule is active" on 500 cameras; recipients (a change can take the rules that
send to them along; new) and schedules (new, edit, copy, delete) the same way; an edit can also change the event, the
schedule and the recipient (each device's own one of a name); **New rule** builds one rule for many devices
(an event they offered, conditions, a schedule, an action with its settings). Every settings form uses the bundled action
catalog (`Catalog/action-catalog.json`: labels, kinds, defaults from the camera's web interface). Decisions, research
and device API: plugin `README.md`.

- Devices: the purple "Selected devices" button (`PageDeviceChoice`: All devices, tags, chosen devices; the Devices page selection to start with, else the last choice on this computer); the page reads only the devices of the choice and nothing before one.
- Rules that must hold: read only on request, over SOAP (passwords in plain text stay on the server, never reach the
  client or a log, a login inside an address shows as `user:********@` and keeps each device's own password); an edit
  changes only the checked parts, everything else stays as each device has it; a rule is written
  only while it is exactly as the page read it (a version keyed with a server-only secret, so no password can be guessed
  against it), one task per device at a time; in place over REST `action-rules` v2 where the device lists
  it (AXIS OS 12; the action only when it changes, never a `**Redacted**` password), else recreated over SOAP (new rule
  first, then the old one); every write is saved for undo (before and after, 30 days, passwords encrypted with the
  master key, also values with a password inside an address) and undo puts back only what is still as the run left it,
  once no task of the run is queued or running; a hidden password inside an address is kept only for the same user and
  server; OADM never creates, changes a rule to or copies "Erase configuration";
  a new rule is checked whole on the server before any task starts and created by the same task as a copy; refused
  statuses are not contacted; a copied rule brings the schedules it uses first (OADM's own id
  `com.axis.schedules.oadm.<name>`, the same on every device); topics are written without the `//.` AXIS OS 10 appends;
  built-in schedules are never changed or deleted, a used schedule is deleted only together with its rules.
- Roles: "show" (Operator, Viewer), "Change action rules" (`manage`, Operator yes), "Delete action rules" (`delete`,
  Operator yes); every change, copy, delete and undo is audited.
- Tests: `tests/Oadm.Plugins.ActionRules.Tests` (+ Perf 5,000 devices); opt-in `RulesHardwareWriteTests` and
  `ServerHardwareWriteTests` (`OADM_ACTION_RULES_WRITE=1`, every dev camera, own rules only, also a built rule and every
  action with the catalog's defaults; passed on AXIS OS 10.12, 11.9 and 12.11).

## System report (core plugin without a page)

`plugins/Oadm.Plugins.SystemReport` (+ `.Client`), id `oadm.system-report`, always on, a toolbar button **System report**
like ADM's "Get system report": the server downloads the server report of every selected device and the client saves
one ZIP for Axis support. Details: plugin `README.md`.

- Rules that must hold: read-only for devices (`serverreport.cgi`, `zip_with_image` for video devices, else `zip`); the
  answer is streamed to disk in the plugin's data folder (`VapixRequestOptions.StreamResponse`), must start with a ZIP
  signature, at most 256 MB and 5 minutes per device, at most 4 devices at a time for all jobs; refused statuses without
  a request; jobs live 30 minutes after their last use, their files are deleted then, on `delete` and when the plugin
  stops; 1..50 devices per job (the toolbar button is disabled above 50: "Choose at most 50 devices."), status polls carry only the changed devices; methods are Operator, `start` is audited.
- Tests: `tests/Oadm.Plugins.SystemReport.Tests` (+ read-only hardware test `SystemReportHardwareTests`),
  `tests/Oadm.Server.Tests/SystemReportServerTests`.

## Hardening scan (core plugin)

`plugins/Oadm.Plugins.HardeningScan` (+ `.Client`), id `oadm.hardening-scan`, rail page **Hardening scan** (group
Security, icon `clipboardCheck`), **off by default**. One read-only scan of the chosen devices against the AXIS OS
Hardening Guide in its two levels (Basic, Extended), one icon column per check, CSV export. Check table, evidence and
decisions: plugin `README.md`.

- Devices: the purple "Selected devices" button (`PageDeviceChoice`: All devices, tags, chosen devices; the Devices page selection to start with, else the last choice on this computer); the page scans the devices of the choice (one **Scan** button) and shows only their results; nothing before a choice.
- Rules that must hold: strictly read-only (GETs and list calls only); 15 s per request, 2 min per device, 16 devices at
  once; one failed read marks only its checks; Certificate changed, Credentials required, Password not set and
  Unreachable devices are not contacted; items that cannot be checked remotely are not shown; the latest AXIS OS is
  information only; every method is Operator and not audited; results kept per device and level (at most 10,000).
- Tests: `tests/Oadm.Plugins.HardeningScan.Tests` (+ read-only hardware test).

## Web View (core plugin)

`plugins/Oadm.Plugins.WebView` (+ `.Client`), id `oadm.web-view`, rail page **Web View** (group Utilities, icon `globe`), on by
default, switchable. The web interface of the managed devices in browser tabs inside OADM, logged in with the stored
login. Decisions, the relay and tests: plugin `README.md`.

- Rules that must hold: passwords never reach the client: the embedded browser talks to a relay of its own per tab (own loopback
  address `127.1.x.y`, so the devices' login cookies never mix; macOS 127.0.0.1, `Oadm.Client/DeviceWeb/DeviceWebRelay`), the server sends every request and WebSocket to the device with the
  stored login and the pinned certificate (`DeviceWebService`); only the browser that opened the relay's one-time start
  address gets its session cookie (others 403, another Host 400); requests stay on the device; refused statuses are not
  contacted; every open is audited. Permissions "oadm.web-view:show" and "oadm.web-view:open" (full device control), both
  off for the built-in Operator by default; without "open" the page says "Your role does not allow opening devices."
- Page: left card: the page select (Web interface, Overlays, Privacy masks, Image Health Analytics, Object Analytics, License Plate Verifier,
  Custom with a path field, error below it; it shows the active tab's page, changing it opens the page in the active tab,
  new tabs use the page chosen last), the search, the devices grouped by tag (like Group by tag, "No tag" last, no
  counts; `ui:DeviceIcon` + address). Right card: one row Close all tabs, Reload, Back (browser history), the tabs (one per
  device, select-field height, scrolling sideways), then the page edge to edge. Without the OS browser runtime (WebView2,
  WebKitGTK) the page says what to install. The tree and the tabs are the shared `ui:DeviceTree` and `ui:PageTab`.

## Live View (core plugin)

`plugins/Oadm.Plugins.LiveView` (+ `.Client`), id `oadm.live-view`, rail page **Live View** (group Monitoring, icon `grid`; New view `gridAdd`),
**off by default**, switchable. Saved views of several cameras at once, like a VMS: a view is a grid of cameras, opened in a
tab. Decisions, methods and tests: plugin `README.md`.

- Rules that must hold: views live on the server, shared by every user (plugin setting `views`), every change pushed to
  every open page; a save of an older version is refused (two users); the grid is 20 x 20 cells and always fills the tab,
  a camera at least 2 x 2, never overlapping, at most 16 cameras per view and 200 views; the server checks every rule
  again (`ViewRules`, shared with the page; the grid is the shared `ui:TileGrid` with `LiveViewPluginInfo.Layout`). Roles: "show" (Operator and Viewer), "Change views" (`manage`, Operator yes,
  Viewer no; save and delete audited); the pictures need "Live view". Every camera is the host's live video component
  (`ICorePluginClientContext.LiveVideo`, controls closed in the tile, open in fullscreen); only the active tab is visible,
  so other tabs' streams rest. A removed device keeps its place and says "Removed device 1a2b3c4d".
- Page: left card (Collapse / Expand at its bottom like the rail; collapsed a strip with the views as icons) Views (New view, the list; Edit and Delete view at the right of each row) and Cameras (search, devices with video by tag, `ui:DeviceTree`); right card
  one row Close all tabs (editing: Name, Save, Cancel), the tabs (`ui:PageTab`), then the grid.
  Editing: cell lines, a frame per camera (address, X; drag inside to move, an edge or a corner to resize), a camera dragged from the tree shows a
  frame with its address where it will land: the largest free square at the drop point (up to 10 x 10), a click on a camera puts it in the first free place.
  Outside editing a click on a camera opens it alone in its own tab (filling it, never edited or saved); the camera under
  the pointer gets a thin accent frame.

## Dashboards (core plugin)

`plugins/Oadm.Plugins.Dashboards` (+ `.Client`), id `oadm.dashboards`, rail page **Dashboards** (group Monitoring, icon
`gauge`), **off by default**, switchable. Live values of OADM's events (mostly device triggers: a temperature, a plate, a
gate open) and live video on saved dashboards: a grid of widgets, each one value of one device (On / Off, Text, Number,
Gauge, Live video; a gauge has the Number settings plus Min, Max and the line width only, ticks, sizes and labels are automatic). Decisions, methods and tests: plugin `README.md`.

- Rules that must hold: the plugin never talks to a device; the values come from `ICorePluginContext.EventSubscriptions`
  and live in server memory only (latest per event type and device, at most 100,000 keys, least recently updated dropped
  first, O(1) per event; nothing kept while the plugin is off); an event offers its declared values at once (Active of an
  on / off event first; received values refine the type of a text value) and only its own devices (`EventTypeInfo.DeviceIds`); a value that may be old is dimmed with the reason (event gone, its plugin off, the
  trigger's connection lost, the device Unreachable / Credentials required / Certificate changed); pages watch only the
  widgets of their open tabs (renewed every 10 s, forgotten after 30 s), changes are pushed at most every 250 ms;
  dashboards live on the server like Live View's views (plugin setting `dashboards`, every change pushed, an older version
  refused), the grid is the shared `ui:TileGrid` (20 x 20, at least 2 x 2, never overlapping), at most 50 widgets of them
  16 live videos and 200 dashboards; colors and icons are theme names; the server checks every rule again
  (`DashboardRules`).
- Roles: "show" (Operator and Viewer), **Change dashboards** (`manage`, Operator yes, Viewer no; save and delete audited);
  video needs "Live view".
- Page: left card Dashboards and Events (the events with values, device triggers first); right card the tabs, while
  editing **Add cameras** (the picker, cameras only, at most 16 live videos), and the grid; several widgets are selected with
  a selection rectangle over empty cells, Ctrl+click or Ctrl+A, moved together and changed in a group panel (what they share,
  "Mixed" where they differ); while editing the panel of the selected widget (device through the shared picker for one device,
  value, type, widget and its settings, every change shown at once); **Copy for other devices** (the copy icon of a widget's
  edit frame or the group panel: one copy per chosen device; the picker asks the server for the event's devices, `devices`). Empty states for Device triggers off
  (`ICorePluginClientContext.IsPluginOn`), no trigger yet (**Open Device triggers**) and no dashboard.
- Tests: `tests/Oadm.Plugins.Dashboards.Tests` (+ Perf 50 widgets at 4 updates a second next to 30,000 devices; headless
  screenshots with fake-mode video). Fake mode: `FakeOadmApi.Dashboards.cs`.

## Edge Recording Export (core plugin)

`plugins/Oadm.Plugins.EdgeRecordingExport` (+ `.Client`), id `oadm.edge-recording-export`, rail page **Edge Recording
Export** (group Maintenance, icon `hardDriveDownload`), **off by default**, switchable. Choose cameras (the shared device
picker, video devices only, tags allowed), all recordings or a range in this computer's time, Search, then Download into
a chosen folder: a folder per camera ("10.0.0.48 P3265-V B8A44F631339"), the camera's file names. Decisions, device API
and tests: plugin `README.md`.

- Rules that must hold: read-only for the cameras (list, capabilities, properties, export, disks; Viewer rights on the
  device suffice); the files travel camera -> server -> client through `PluginService.Download`, never stored on the
  server, never client to camera; one recording at a time; each recording cut to the range exactly as the camera listed
  it; MP4 when offered, else Matroska; files written as `.part` and renamed when complete, existing files skipped, so a
  stopped run starts again; refused statuses are not contacted; at most 50,000 recordings per camera and search, sizes
  estimated from at most 40 recordings per camera.
- Roles: "show" (Operator and Viewer: search), "Export recordings" (`export`, Operator yes, Viewer no); every downloaded
  recording is audited with the camera and the times.
- Tests: `tests/Oadm.Plugins.EdgeRecordingExport.Tests` (+ Perf 5,000 cameras, read-only hardware test that searches and
  downloads the last two minutes of the dev cameras through the in-process server).

## Edge storage (core plugin)

`plugins/Oadm.Plugins.EdgeStorage` (+ `.Client`), id `oadm.edge-storage`, rail page **Edge storage** (group Maintenance,
icon `hardDrive`), **off by default**, switchable. The SD card of every chosen camera in one table (state, size, used, wear,
encryption, retention, five count cards) and the tools of the camera's web interface for the selected cameras: Check, Set
retention, Repair, Format, Encrypt, Turn off encryption, each a task per camera in the tasks pane below the page. SD
cards only. Decisions, device API (verified on an AXIS Q1615 Mk III) and tests: plugin `README.md`.

- Devices: the purple "Selected cameras" button (`PageDeviceChoice`: All devices, tags, chosen cameras; the Devices page selection to start with, else the last choice on this computer); the page reads only the cameras of the choice and nothing before one.
- Rules that must hold: the table is read only on request and for the chosen cameras (page opens, Refresh), read-only, refused statuses not
  contacted; the tools are hidden tasks (`ShowInMenus` false) started by the page methods `change` (check, retention) and
  `format` (format, repair, encryption), whose rights decide (an action through the other method is refused); every run
  asks first and says which recordings are lost; a fresh API list before the first write ("Nothing was changed"); format,
  check and repair unmount the card first (the camera refuses them on a mounted card) and the task always mounts it again,
  also after a failure, and ignores Cancel from the unmount on; the passphrase only in memory and the POST body, never
  stored, logged or in a URL; retention 0..7,000 days (0 = no age limit).
- Roles: "show" (Operator, Viewer), "Change storage settings" (`manage`, Operator yes), "Format and encrypt storage"
  (`format`, full device control, Operator no); both run methods audited.
- Shared code (no copies): the disk list `EdgeDisk` / `EdgeDisks` (also Edge Recording Export and Hardening scan), the
  on-request table `plugins/Shared/DeviceSweep.cs` + `SweepRows.cs` (also Image Health Dashboard), `DeviceMessages.Refusal`
  (SDK) and `PluginEventWatch.RunAsync` (client SDK).
- Tests: `tests/Oadm.Plugins.EdgeStorage.Tests` (+ Perf 5,000 cameras); opt-in `StorageHardwareWriteTests`
  (`OADM_STORAGE_WRITE_URL` / `_USER` / `_PASSWORD`, formats the card; every tool, the card ends as it started).
- Compliance check type **SD card** (`SdCardCheck`, see "Compliance"): a working card, optional free space; only while
  Edge storage is on.

## Date and time

`plugins/Oadm.Plugins.DateTime` (+ `.Client`), id `oadm.datetime`, always on, context menu (Maintenance, icon `clock`)
**Date and time**: a clone of the ADM / AXIS Camera Station "Set date and time" dialog for any number of devices (time
zone from the 313 IANA zones of AXIS OS, daylight saving, time mode: server time, NTP (DHCP, up to 5 servers, NTS from ntp
1.5) or manual). Details, the VAPIX decision table and the device research: plugin `README.md`.

- Rules that must hold: behaves exactly like ADM (no "Keep unchanged": OK writes the time zone and the chosen time mode
  to every selected device); values the device already has are skipped; every write is verified by reading again;
  devices without the Time API fail setting the clock with "Nothing was changed" (no `date.cgi`); task names say what
  changes ("Set NTP servers ...", "Sync with server time", else "Change date and time").

## Device configuration

`plugins/Oadm.Plugins.DeviceConfig` (+ `.Client`), id `oadm.device-config`, always on, context menu (Maintenance)
**Export configuration** and **Import configuration** (AXIS OS 12.5 or later: the device's own configuration export and
import, `/config/rest/$export`, `$import`, like its web interface), **Configure devices** and **Create configuration file**
(ADM's Configure Devices: any AXIS OS with `param.cgi`). Decisions, device research, steps and the write tests on hardware:
plugin `README.md`.

- Rules that must hold: merge only; the network identity (host name, addresses, router, DHCP, device names, the multicast
  addresses) and read-only values are never imported; "Connection settings" (HTTPS, ports, login method, 802.1X, IPv6,
  web interface) are their own area, off by default, with a warning, written last when chosen; the same model only
  ("Nothing was changed" otherwise); no backup by OADM (export first); the device validates and OADM shows its answer per
  area, one failed area does not stop the others; a ZIP entry must carry the device's MAC address; Export is a dialog,
  never a task.
- **Configure devices** rules that must hold: values come from the source (a device or an ADM `.cfg` file), definitions
  are read once per model and AXIS OS (one `listdefinitions` request while it stays below the 1 MB XML limit, else per
  group); internal, hidden, read-only, constant, write-only and write-level-7 parameters are never offered; each device
  checks every value against its own definition before writing and skips what it does not have (Done with warnings); the
  dangerous parameters (`DangerousParameters`: network identity and connection, incl. the web server ports and HTTP / HTTPS
  policy, HTTP/2 and the web interface) carry a warning, are confirmed, need their own right (Operator no) and are written
  last; the device's IP addresses are never written (Network settings and Assign IP address check a new address and
  follow the device); names that must differ per device are not offered when several devices are selected; the source device keeps its own rules, schedules and stream profiles (skipped there); a rule or stream profile with the same name is replaced, a schedule with the
  same name and recurrence used again (applying twice changes nothing more); ADM files are read and written byte for byte
  (format 3, known from files only, ADM is never decompiled), passwords in them like ADM; password values never reach a log
  or an error text. **Create configuration file** is a dialog for one device, never a task.
- Tests: `tests/Oadm.Plugins.DeviceConfig.Tests` (+ Perf 5,000 devices, read-only `ConfigureHardwareTests`); opt-in
  `ImportHardwareWriteTests` (`OADM_CONFIG_WRITE_URL` / `_USER` / `_PASSWORD`) and `ConfigureHardwareWriteTests`
  (`OADM_CONFIGURE_WRITE=1`, the first dev camera: writes, writes again, removes everything; passed on the P3265-V).
- Compliance check type **Matches configuration file** (`Configure/ConfigMatchCheck`, see "Compliance").

## Applications (ACAP)

`plugins/Oadm.Plugins.Acap` (+ `.Client`), id `oadm.acap`, always on, context menu (Applications) **Applications
(ACAP)**: lists the installed applications, starts, stops and removes them (remove confirmed) and installs or upgrades
an uploaded `.eap` on all selected devices (classic Application API 1.x), and installs license key files (with the package
or on their own, matched by the MAC address in the key). Decision table and research: plugin `README.md`.

- Rules that must hold: before uploading it reads the package manifest and refuses ("Nothing was changed") a wrong
  architecture, an AXIS OS outside the package's range or below its schema minimum, root apps on AXIS OS 12+ and a
  downgrade without the explicit option; a license key is sent only to the device whose MAC address it names, for the
  installed application's id and not expired (a device without a key: Done with warnings, nothing sent); activation with a
  license code is not offered (the device has no API for it, its web page asks Axis online).
- Compliance check type **Application running** (`ApplicationRunningCheck`, see "Compliance"): the application is
  installed / running, optionally at least a version and a valid license; the choices are read from one device.

# Settings

Server-side in `Setting`. Keys: `Polling.IntervalSeconds` (60, 5..86400),
`Polling.FullRefreshMinutes` (10, 1..1440, Settings page "Read device details every (min)"), `Events.LogRetentionDays` (30, 0..3650) and `Events.LogMaxEntries` (100000, 0 or
100..1000000): event log retention (server-only), `Scan.Parallelism` (32, Settings page "Addresses scanned at once"), `Scan.TimeoutMs` (1500,
Settings page "Scan timeout (ms)": range scans and the first contact of imported lines), `Devices.WaitingRetryMinutes` (15,
1..1440, Settings page "Try waiting devices every (min)", proto `optional int32 waiting_retry_minutes = 10`; see "Waiting
list"),
`Discovery.ZeroConfSeconds` (30, 5..300: a zero-conf scan of the add page ends after this time; Settings
page "Discovery duration (s)"), `Server.Name` (hostname),
`Server.ListenUrl`, `Uploads.MaxMegabytes` (2048, 1..65536), `Uploads.RetentionHours` (24,
1..8760; both server-only, not on the settings page yet), `Tasks.RetentionDays` (90, 0..3650, 0 = no age
limit) and `Tasks.MaxHistory` (50000, 0 or 100..1000000, 0 = no limit): task history retention, applied
one minute after start and then hourly by `TaskRetentionHostedService` (finished tasks only, one bulk
delete, one Removed change per task; server-only, not on the settings page yet),
`Tasks.MaxParallelPerPlugin` (16, 1..256: how many tasks (= devices) of one task plugin run at the same
time, read live by the task engine, plugin `MaxParallelDevices` can only lower it; proto
`optional int32 max_parallel_tasks_per_plugin = 9` so a partial `Set` keeps it; Settings page "Parallel
tasks per plugin" with the hint "How many devices a task runs on at the same time, e.g. restarts or
firmware updates."). The former `Devices.UseHostName` is gone: the add page chooses DNS names or IP addresses
per add (see "Add Devices Page"); proto `use_host_name = 7` is kept unused for its field number. Settings page in the client exposes them (four cards Polling, Discovery, Tasks, Server, saved
together by Save and Reload in a row of their own below the four cards; operators see the values as plain text without Save and Reload). Rail page **Credentials** (Admin only, subtitle "Passwords OADM
tries when it adds devices.", `Settings/CredentialsViewModel` + `CredentialsView`, one card without title): entries (key icon, user name, password masked as 8 bullets, eye icon button "Show password" /
"Hide password" that loads it with `RevealCredential` and masks (and forgets) it on the second click, copy icon
button "Copy password" (clipboard of the window, loaded on demand, not shown), added time, Remove as a red icon button), a separator, then the add form under the heading "Add credential" (user
name, password, "Add credential"); stored encrypted on the server (`CredentialListStore`, table
CredentialListEntries), tried on every discovered device (see "Add Devices Page"). Rail page **Plugins** (Admin only)
turns plugin packages on and off (see "Plugins on and off"). Rail page **About** (everyone,
subtitle "Version and licenses.", `Settings/AboutPageView` around the card `Settings/AboutView`, `AboutViewModel`):
terms of use (card title, a scrollable block with wider line spacing), client version, server version (`ServerSettings.server_version`), the
sentence on the Apache-2.0 license, "Show licenses" shows `THIRD-PARTY-NOTICES.txt` (next to the exe, the macOS app's
`Contents/Resources`, or `THIRD-PARTY-NOTICES.md` in a checkout) in a read-only `ui:CodeView` (Plain). The Settings page
starts with the card **This computer** (every user, saved at once in the client settings file): "Decode video on the
graphics card" with the test's result as hint, "Show video details over the picture" and "Frame rate (adaptive quality)"
(see "Live view"), "Appearance" (Dark, Light; see "Visual Style"). The page has two columns (each up to 760 px, left-aligned): This computer and the server settings left,
the card **Backup and restore** right (administrators, see "Backup and restore"); narrower than 1100 px the backup card
moves below the server settings. The server
address is set with `--server` or in the
client settings file. Client-side (local JSON in LocalApplicationData): server address, device grid column layout and group
mode (`GroupDevicesByTag`), bottom pane state, `GpuDecoding`, `ShowVideoDetails`, `AdaptiveFps`, `Appearance`.

# Backup and restore

Settings page card **Backup and restore** (Administrator only, never grantable).
Core `Oadm.Core.Backup` (`BackupFile`, `BackupArchive`), server `Oadm.Server.Backup` (`BackupManager`, `BackupGrpcService`,
`BackupScheduleHostedService`, `ServerRestart`), gRPC `BackupService` (`backup.proto`), client `Backup/` (`BackupViewModel` +
`BackupCardView`, `BackupPasswordWindow`, `RestoreWindow`).

- **File** (`.oadmbackup`, `BackupFile`): "OADMBACKUP", format byte, a plain JSON header (OADM version, server name, last
  migration, kind, PBKDF2-SHA256 600,000 iterations, salt, nonce, key check), then the body (a ZIP) in 1 MB chunks, each
  AES-256-GCM with its own nonce and the associated data SHA-256(header) | chunk number | last flag (no reordering, cutting
  or header change unnoticed). The key check names a wrong password apart from a damaged file. Passwords 8..256 characters.
- **Content** (`BackupArchive`): `manifest.json` (counts per area, plugin packages, plugin data folders), `oadm.db` (a
  consistent `VACUUM INTO` copy without the login sessions), `master.key` (only inside the encrypted body; a backup restores
  on another computer and every stored password and plugin secret stays readable), `plugin-data/**` (files in use skipped).
  Not in it: the server's TLS certificate (the target keeps its own), uploads, logs.
- **Versions**: a backup's last migration must be one of this build's: an older one is upgraded by the normal migrations
  at the next start (**migrations are never deleted**), a newer one is refused before the password ("This backup is from
  OADM x, newer than this server (y). Update OADM to x or later first."). Before every migration of an existing database the
  server copies it to `backups/before-upgrade/oadm-<time>-<last migration>.db` (newest five kept, `DatabaseInitializer`).
- **Restore**: the backup is checked and unpacked to `restore/staged` (only `oadm.db`, `master.key` and `plugin-data/...`
  entries inside the folder; a wrong password or a damaged file changes nothing), then a safety backup of the current state
  with the same password (`backups/oadm-backup-<time>-before-restore`, newest five), running tasks cancelled, audit
  "Started restore", and the server restarts (`ServerRestart`: as a service it exits with code 75 two seconds later and the
  service manager starts it again; on a console `Program` builds a new host in the process). The next start applies it in
  step 0b of `OadmServerHost.StartAsync`, before anything opens the database or the key: the server's database, key and
  plugin data move to `restore/previous-<time>` (newest three kept), the staged files take their place, the TLS key in
  `server-tls.json` (Linux, macOS) is encrypted again with the restored key; any failure puts the old files back. Step 2c
  records the report (`Backup.LastRestore`: "Devices: 4,812 restored", plugins of the backup that are not installed), ends
  every login session and audits "Restored backup" for the administrator who asked.
- **Automatic backups** (setting `Backup.Schedule`: on, time "HH:mm" server time, keep 1..365 (7), the password encrypted
  with the master key, purpose `oadm:backup-schedule`): daily, checked every 30 s, a missed day made up at the next check,
  into `<datafolder>/backups/oadm-backup-<time>-automatic.oadmbackup`; `Backup.LastAutomatic` keeps the outcome. One backup
  or restore at a time.
- **gRPC** `BackupService` (all Admin): `GetState`, `SetSchedule` (empty password keeps the stored one), `Create` and
  `Download` (stream of `DownloadChunk`: name and length first, 256 KB chunks, uncompressed), `Delete`, `Inspect`
  (`wrong_password`, `problem`, the counts), `Restore` (an upload of `FileService` or a server backup by name; names must
  match `oadm-backup-<date>-<time>-<kind>.oadmbackup`). Audited: Created backup, Downloaded backup, Deleted backup, Changed
  automatic backups, Started restore, Restored backup.
- **Client**: Back up now (password + confirm, then the save picker; the file streams to disk with the progress row), Restore
  from file (uploaded first) or a server backup's Restore (dialog: password, Check shows the counts, Restore after a
  confirmation naming the running tasks), Automatic backups (Save is secondary: one accent button per page), the list with
  Download, Restore, Delete (red, confirmed), the last automatic backup and the last restore report.
- **Tests**: `tests/Oadm.Core.Tests/Backup/BackupTests` (format, another server, an older OADM upgraded, rollback, entries
  outside the folder), `tests/Oadm.Server.Tests/BackupServiceTests` (create, check, restore with a restart, automatic,
  schedule), `tests/Oadm.Client.Tests/BackupCardTests` (+ screenshots).

# Security and access

## Secrets

- Device passwords: AES-256-GCM, key in `<datafolder>/master.key` (0600 on Unix), random nonce per record, stored as
  `nonce|ciphertext|tag` (no OS keyring). The credential list uses the same, with the entry id as associated data.
- Device credentials never leave the server; gRPC returns only "has credentials" and, for the prefill of the "Log in"
  dialog, the stored user name the selected devices share. The credential list returns ids and user names only; the one
  exception is the explicit reveal of an entry (`SettingsService.RevealCredential`, eye and copy buttons of the Credentials
  page): Admin only, every reveal in the audit log, refused while no user exists. Credentials typed for a login (RetryAuth,
  the context menu's "Log in") or a first password travel only client -> server.
- Never log passwords or digest headers.

## TLS

- On first start the server creates a self-signed certificate (ECDSA P-256, CN = server name, SAN = host name, every
  interface address and localhost, 20 years) and keeps it in `server-tls.json` (certificate PEM + private key encrypted
  with the master key, purpose `oadm:server-tls`). On Windows the key is instead one persisted named CNG key "OADM Server
  TLS <hash of the data folder>" (machine key store as a service, else the user store; not exportable; opened by name on
  every start, overwritten on regenerate) and the file holds its name. A key that cannot be read (e.g. after a master key
  replacement) creates a new certificate and logs that clients must use Forget server. `--Oadm:RegenerateTlsCertificate`
  makes a new one.
- Kestrel serves HTTP/2 over TLS on the listen URL (default `https://0.0.0.0:5080`; an `http://` URL is accepted for tests
  and logs a warning; a stored listen URL equal to the old default `http://0.0.0.0:5080` reads as the https default). The
  server binds to all interfaces so clients on other machines can connect.
- The client pins the certificate's SHA-256 per "host:port" on first connect (`Oadm.Contracts.Security.ServerCertificatePinning`,
  `client-settings.json` `PinnedServers`; `OadmChannel` creates the pinned channel and adds the token). The fingerprint
  is confirmed in the shared confirmation window ("Trust this server?", two lines of 16 pairs); a changed certificate is
  refused with "The server certificate changed ..." and **Forget server** re-pins. An address without scheme means https.

## Users, roles and sessions

Code: server `Oadm.Server.Auth` (`AuthInterceptor`, `AccessPolicy`, `CallerStreams`, `AuthGrpcService`, `UserGrpcService`,
`RoleGrpcService`, `AuditGrpcService`, `ServerTlsCertificate`, `AuthMaintenanceHostedService`,
`InProcessAccess.CreateTokenAsync` for in-process hosts), core `Oadm.Core.Auth` (`UserStore`, `RoleStore`,
`PermissionCatalog` + `HostPermissions`, `RolesUpgradeNotice`, `PasswordHasher`, `AuthTokenStore`, `LoginThrottle`,
`AuthManager`: login, first administrator, setup code; `AuditLog`; `CallerContext`), migrations `UsersAndAudit` (Users,
AuthTokens, AuditEntries) and `Roles`; client `Shell/AppShell`, `LoginViewModel`, `UserSession`, `MyRoleWindow`,
`Settings/UsersCardView` + `RolesCardView`, `Logging/AuditLogViewModel`.

- Table `Users`: Id, UserName (unique case-insensitive, 1..64), PasswordHash (PBKDF2-SHA256, 210,000 iterations, 16-byte
  salt), RoleId (table `Roles`), Disabled, CreatedUtc, LastLoginUtc. Passwords have no strength rule, only not empty.
- **First administrator**: while no user exists, the login window offers "Create the first administrator"; the server
  accepts it from a loopback client, or from a remote client with the one-time setup code the server writes to
  `<datafolder>/setup-code.txt` (admin-only file) and logs at startup.
- **Tokens**: `AuthService.Login(user, password, remember)` returns an opaque random token (32 bytes, base64url); the
  server stores only its SHA-256 with user, client address and expiry (8 h sliding, 30 days with "Remember me"; table
  `AuthTokens`, the sliding expiry is cached and written at most once a minute). Clients send `authorization: Bearer
  <token>` in the gRPC metadata. `AuthInterceptor` answers UNAUTHENTICATED without a valid token (open: `AuthService.Status`,
  `Login`, `CreateFirstAdmin`) and PERMISSION_DENIED with what the role lacks ("Your role does not allow: Delete tasks.",
  `AccessPolicy.DeniedMessage`); it sets `CallerContext` (user, role, the role's `PermissionSet`), so a task's Owner is
  "<user>@<client machine>". `Logout` revokes. Failed logins: 5 per user in 5 minutes, then 5 minutes locked, logged.
  `AuthMaintenanceHostedService` removes expired sessions and old audit entries hourly. A new password or a disabled user
  ends the sessions; a new role applies to the next call (`AuthTokenStore.SetRole`).
- **Roles and permissions.** Every user has one role (table `Roles`: Id, Name 1..64 unique case-insensitive,
  Description 0..200, Kind Administrator / Operator / Viewer / Custom, PermissionsJson = the granted keys, KnownJson = every
  key the role has seen, CreatedUtc, ChangedUtc; at most 50 custom roles). Built-in roles with fixed ids
  (`BuiltInRoles`): **Administrator** (every permission, also later ones; not editable, not deletable), **Operator**
  (editable, "Reset to default") and **Viewer** (devices, live view and the monitoring pages; editable). The migration
  `Roles` maps the old Admin to Administrator and everyone else to Operator. A permission is a key: host keys
  (`HostPermissionKeys` in the SDK: `devices.addRemove`, `devices.refresh`, `devices.login`, `devices.webInterface`,
  `devices.liveView`, `devices.opticsControl` ("Optics Control": zoom, focus and the RTSP port), `devices.export`, `tags.assign`, `tags.manage`, `tasks.cancel`, `tasks.delete`, `tasks.deleteAll`,
  `compliance.view` ("See compliance", Operator and Viewer yes), `compliance.manage` ("Change compliance checks", Operator no),
  `events.view`, `events.manage`, `events.clearLog`, `logs.server`), "<plugin id>:<key>" for core plugins (every page gets "show"
  automatically) and "task:<id>" / "task:<id>:<key>" for task plugins. `PermissionCatalog.Build` collects them from every
  loaded plugin (on or off) at start; `RoleStore.SyncCatalogAsync` gives a role a permission it has not seen once: Operator
  and Viewer by the permission's default, custom roles nothing, so a limited role never gains a right by itself. Keys a
  role has but no loaded plugin declares are kept. **Administrator only, never grantable**: users and roles, the
  credential list (list, add, remove, reveal), plugins on / off, `SettingsService.Set`, the audit log. Every gRPC method
  has a rule in `AccessPolicy` (Open, SignedIn, a permission, InCall, Admin; a test checks that every contract method is
  listed, an unlisted one is Admin): device list, tags, tasks list and settings Get need only a login; plugin calls and
  task runs are checked in the call (`InCall`). The operator default withholds the risky rights:
  Web View, remove users on devices, firmware factory default and downgrade, application downgrade and removal, connection
  settings import; event rules, tag management and Delete all stay off as before. `RolesUpgradeNotice` records what
  operators lost at the first start of an installation that had users; administrators see it as a banner with "Check
  roles" until they dismiss it (per client).
- **Open streams**: `CallerStreams` registers every streaming call and the interceptor checks the session and the role
  once more right after the registration (a change in between never leaves a stream with old rights); a role change ends
  the streams the role no longer allows (the HTTP request is aborted, the client reconnects and is refused), a user with
  another role gets all streams ended. `AuthService.WatchSession` (not ended by role changes) sends the user with role and
  permissions after every change. A logout, a new password, a disabled or deleted user end every stream of those sessions,
  the session stream too (`AuthTokenStore.SessionsEnded`); revoking deletes the token rows before the cache entry, so a call
  in between cannot bring the session back.
- **Plugin methods**: `CorePluginHost.AccessOf` resolves the permission of a page method: the plugin's
  `RequiredPermission(method)` when it names a declared key or "show"; otherwise Operator methods need "show" (a login
  for a plugin without a page) and Admin methods "manage" (automatic for a plugin that declares nothing but answers
  `RequiredRole` itself). Anything else, and a plugin that throws, is Administrator only. `PluginService.Watch` needs
  "show"; `ListCorePlugins` leaves out pages the role may not show. Calls where `ICorePlugin.IsAudited(method)` (DIM: the
  Admin methods) are audited as "Plugin action" (target the plugin, detail the method); VAPIX Commander `tryRequest`
  and `rollout` are audited; "Install in trusted root store" on the server asks with the CA's SHA-256 fingerprint and is
  audited. `TaskService.Run` needs every key `PermissionCatalog.RunKeys` names for the payload (refused before any task
  starts), `Query` any key of the task; `ListTaskPlugins` leaves out tasks without a key.
- **Audit log**: table `AuditEntries` (TimeUtc, UserName, ClientAddress, Action, Target, Detail), 365 days or 200,000
  entries. Logged: login ok / failed, logout, user changes, roles ("Created role", "Changed role" with "+Web View: Open
  devices, -Restart", "Deleted role"), settings changes, credential list add / remove / reveal, PKI
  actions, DHCP / NTP save, task runs (plugin, device count), Delete all, device remove, device login and first password
  (counts), devices added or moved automatically (user "system"), the waiting list (kept, removed), tags ("Tagged 12 devices: +Building A -PTZ"), plugins
  on / off, event rules, VAPIX Commander send / rollout, Web View ("Opened web interface", the device), zoom and focus ("Changed zoom or
  focus", "Zoom 1.50"), RTSP port ("Changed RTSP port in OADM"). The Logs page has an **Audit** tab (Admin only, newest 10,000,
  virtualized, search).

## Client: login and users

- `Shell/AppShell` shows `LoginWindow` (`LoginViewModel`) before the main window, again after Log out and when a call
  answers UNAUTHENTICATED ("Your session has ended. Log in again."). Fields: Server (editable select with the recent
  servers, default `localhost:5080`; `--server` preselects it), User name, Password (`ui:PasswordBox`), Remember me (keeps
  {user, token} per server in `RememberedLogins`, never the password; resumed with `AuthService.Me` at start), Log in;
  first-administrator mode adds Confirm password and, for remote servers, the setup code. Errors below the fields,
  connection problems below the server field; a narrow label column (`StackPanel.compactForm`), no product mark, **Quit**
  on the left; a refresh icon right of the server field ("Connect again") repeats the start without restarting the client.
  Fake mode signs in as "admin" without the window.
- The title bar shows the logged-in user (initial avatar + name, tooltip "Logged in as ... (role) on ...") with Log out; a
  click on the user opens **Your role** (`Shell/MyRoleWindow`, `MyRoleViewModel`, `RoleService.Mine`): the role's name
  and what it allows, grouped like the matrix, read-only. `UserSession` keeps role name and permission keys
  (`Has(key)`, `PermissionsChanged`), fed by Login / Me and the session stream (`ServerConnection`); a server without
  roles sends none and counts as before (Administrator everything, others everything but the Administrator-only parts).
  `UserSession.IsAdmin` (the Administrator role) hides the Users, Credentials and Plugins pages and the Audit tab and
  shows the server settings as plain text ("Only administrators can change server settings."). Everything else follows
  the permissions live: toolbar entries (`IToolbarPlugin.RequiredPermission`, `DeviceToolbar.ApplyPermissions`), the
  context menu entries, the live view click, the tasks pane's Cancel / Delete / Delete all, the Event rules rail entry
  (`events.view`) and its changes, tag administration, the rail's plugin pages and the task menu (reloaded from the
  server, which filters them). The server checks every call again.
- **Users page** (Admin only, subtitle "Who can log in to this server and what each role may do."; `Settings/UsersPageView`
  = the users card on top and the roles card below with a splitter): **+ User** above the list, then users with role
  (plain text), last login, state; row icon buttons Edit user, Change password, Disable / Enable, Delete (red; Disable
  and Delete confirmed; the own row offers only Change password); never the last enabled administrator, never yourself.
  Add user (user name, password, role), Edit user (the role; Administrator with a hint) and Change password (new +
  confirm) are one single-operation dialog `Settings/UserFormWindow` (`UsersViewModel.Form`), errors and refusals below
  the fields. **Roles
  card** (`RolesCardView`, `RolesViewModel`): the permission matrix, rows = permissions in groups (Devices, Tasks, Event
  rules, then one group per plugin in rail order; collapsible, a three-state check box per role for the whole group),
  columns = roles (built in code-behind; header with name, "3 users" and a menu Rename, Copy, Reset to default, Delete),
  the name column frozen; the permission's explanation as the tooltip of its name, an amber warning icon right after
  the name for full device control (tooltip `RolesCardView.FullDeviceControlTip`), "Plugin is off" for rights of a
  plugin that is off. Toolbar New role, Save, Discard, search; changes are collected and saved together after one
  confirmation that lists them per role; a new role, a copy and a rename open the dialog `Settings/RoleFormWindow`
  (Name, Description, Start from) and stay "Not saved" until Save.

## Automatic login on the add page

- **Axis check first**: before any credential is sent, the device must answer the anonymous `basicdeviceinfo.cgi`
  `getAllUnrestrictedProperties` with a 12-hex serial number equal to the discovered one and a ProdNbr. AXIS OS 8.x
  answers 401 there; then the realm `AXIS_<serial>` of the login challenge must name the discovered serial
  (`DiscoveryAuthenticator.JudgeRealm`, `VapixAuthenticationException.RealmSerial`). Then **HTTPS first** (Digest, then Basic), then HTTP (Digest, then Basic): such a
  verified device may get Basic over plain HTTP when it offers nothing else (`VapixConnectionOptions.AllowBasicOverHttp`,
  set only there; `DiscoveryAuthenticator.Judge`, `LoginSchemes`). Known residual risk: a device that imitates the anonymous answer or the realm passes the check.
- "Save to credential list" of the login editor is shown to administrators only and the server ignores it for other
  roles (`RetryAuth`), like everywhere the credential list is written.
- Candidates: the credential list and credentials typed on the page; passwords of managed devices are never tried on new
  devices; at most 10 rejected credentials per device. Details in "Add Devices Page".
- Tests: `AxisCheckTests`, `FastAddTests`; read-only hardware test `FastAddHardwareTests` (the development camera passes
  on both schemes).

## Master key

- A key check value is stored in `Setting` `Security.KeyCheck` (base64 HMAC-SHA256 of "OADM master key check v1" with
  the key, `CredentialProtector.ComputeKeyCheck`). `Oadm.Core.Security.MasterKeyCheck` runs in `DatabaseInitializer` after
  the migrations, before anything is served. The key is replaced (**warn and continue**) when `master.key` is missing,
  has the wrong size or does not match the stored value, or, without a stored value, when encrypted rows exist and none
  decrypts.
- An existing file is renamed to `master.key.replaced-<UTC time>`, never deleted (`MasterKeyFile.Replace`); the running
  `CredentialProtector` switches to the new key. One transaction deletes the unreadable `DeviceCredentials` and
  `CredentialListEntries` rows, sets those devices to Credentials required, stores the new check value and the notice
  `Security.KeyReplaced` ({id, replacedUtc, devices, credentialListEntries}). The error log names the counts and that
  plugin secrets of the old key (saved command passwords, CA keys, the TLS key) cannot be read either; the PKI shows "CA
  key cannot be read".
- `ServerSettings.key_replaced = 20` (`KeyReplacedNotice`) in every `SettingsService.Get` / `Set` reply; the client shows
  a warning banner below the connection banner ("The server's key was replaced: 3 devices need their credentials
  again." + ", 1 credential list entry was removed.", `Border.banner.warning`) with Dismiss; dismissed ids are kept per client
  (`DismissedKeyNotices`), so a later replacement shows again.

## Limits

- Device HTTP answers at most 16 MB (`VapixClient.MaxResponseBytes` = HttpClient `MaxResponseContentBufferSize`; over it
  `VapixResponseTooLargeException` "The device answer is larger than 16 MB and was not read."). Streamed answers (`VapixRequestOptions.StreamResponse`) go to disk with the caller's own limit (System
  report: 256 MB per device). Upload requests (firmware, ACAP) are streamed and not limited.
- XML from a device only through `Oadm.Sdk.Vapix.DeviceXml.Parse` / `ParseElement`: 1 MB of characters, DTD prohibited,
  no resolver; callers cannot lift the limits. `CodeView` keeps its own 512 K plain-text limit.
- RTSP / video access units at most 8 MB (`NalDepacketizer.MaxAccessUnitBytes`: a larger one is dropped like a damaged
  one and counted in `OversizedAccessUnits`, the stream resumes at the next keyframe).
- Uploads: 10 GB for all uploads together and at least 1 GB free disk (`UploadStore.QuotaBytes` / `MinFreeDiskBytes`): a
  new upload reserves its declared size and removes the oldest finished uploads until both fit, else
  `UploadRejectedException { TooLarge }` = RESOURCE_EXHAUSTED;
  a single upload may be up to `Uploads.MaxMegabytes`. Free space unknown (network share): only the quota applies.
- Requests stay on the device: `VapixClient.SendAsync` refuses (before sending) an absolute URI with another scheme, host
  or port or with user info, and a relative URI with "\". VAPIX Commander paths must pass `CommandValidator.IsDevicePath`
  (one leading "/", no "\", "//", "..", "@" before the query, spaces or control characters; "The path must start with /
  and stay on the device: no "..", "//", "\" or host name.", `CommandValidator.PathProblem`), checked again after the
  fields are filled in. Tests `DeviceLimitsTests`, `RendererTests` (`/\/host`, `//host/x`, an absolute URL).

## Crash handling (client)

- Plugin dialogs, pages and toolbar entries run inside try/catch: "The <name> dialog failed: <message>" (nothing runs,
  `TaskPluginRunner`), a page shows "The <name> page failed: <message>" and its rail entry stays (the other pages load,
  `MainWindowViewModel.SyncCorePluginPages`), a failing toolbar entry is left out ("The toolbar entry <id> failed:
  <message>", `DeviceToolbar`).
- `Infrastructure/CrashHandling` (installed by Program only: the process handlers before Avalonia starts, the UI handler
  in `AfterSetup`; handlers for `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` and
  `Dispatcher.UIThread.UnhandledException`) logs every unhandled error with the client log file path
  (`<datafolder>/logs/client-yyyyMMdd.log`); a UI thread error is handled and shown once
  at a time in `ui:MessageWindow` ("Something went wrong", the message, "OADM keeps running. Details are in the client
  log: <path>"); fatal errors (out of memory, access violation, invalid program, bad image) are logged as Critical and
  end the app; unobserved task errors are logged and observed.

## Service: firewall and folders

- **Firewall (Windows service only)**: the MSI opens TCP 5080 (Domain, Private). The NTP and DHCP plugins open their UDP
  rule ("OADM Server (NTP, UDP 123)", "OADM Server (DHCP, UDP 67)", Domain and Private, for the server exe) when enabled
  and remove it when disabled: SDK `ICorePluginContext.Firewall` (`IFirewallRules`, null outside the Windows service) and
  `FirewallRuleKeeper` (the first sync always applies, so a rule left by a crash is removed at start; errors logged,
  retried on the next change; `FirewallRule.ForService("NTP", Udp, 123)`); server `NetshFirewallRules` (`netsh
  advfirewall`: `delete rule name=...`, which ignores "No rules match", then `add rule ... dir=in action=allow
  protocol=UDP localport=<port> program=<exe> profile=domain,private`) through `ICommandRunner`.
- **Folders** (the service runs as SYSTEM / root): at start (step 0 of `OadmServerHost.StartAsync`, service mode only)
  the data folder, the runtime extraction folder and every plugin folder must be writable only by administrators / root
  (`Oadm.Server.Hosting.FolderGuard` over `IFolderPermissions`: `WindowsFolderPermissions`, `UnixFolderPermissions`). Windows: owner and every entry SYSTEM / Administrators / TrustedInstaller, no
  write, append, delete, change permissions or take ownership for anyone else (CREATOR OWNER allowed); fixed by owner
  Administrators and a protected ACL SYSTEM + Administrators full control (+ Users read and execute for plugin folders).
  Unix: owner root, no group or world write (`find <dir> ! -type l ( ! -user 0 -o -perm -0020 -o -perm -0002 )`; fixed
  by `chown -R 0:0`, `chmod -R go-w`). An unfixable data folder stops the start, an unfixable plugin folder is skipped
  with a load error (`PluginLoader.FolderCheck`). The development plugin folder
  (`artifacts/plugins`) is used only outside service mode.

## Tests

`tests/Oadm.Server.Tests`: `AuthTests` (every service per role, the role table against the contracts, plugin method
roles, first administrator, sessions, throttling, audit), `TlsPinningTests` (real Kestrel with TLS), `MasterKeyCheckTests`,
`MasterKeyFileTests`, `KeyReplacedNoticeTests`, `DeviceLimitsTests`, `UploadStoreTests`; `MethodRoleTests` in the DHCP,
NTP, PKI and VAPIX Commander test projects; `tests/Oadm.Client.Tests`: `LoginTests`, `CrashHandlingTests`.
`TestServerHost` calls as the administrator "admin" (`InvokerFor(token)` / `InvokerForUserAsync(name, role)` for others,
`createAdmin: false` for an empty user table, PBKDF2 with 1,000 iterations for speed).

# Wording

HARD RULE for every UI text and every doc. We write for installers who know Axis devices and come from ADM.

- Short and plain. Intros and subtitles: one short sentence, or none. Never explain what a visible control already shows.
- Tooltips only when they add something, at most about 8 words. Never repeat the label.
- No protocol, API or internal names in UI text (mDNS, Bonjour, zero-conf, fwmgr 1.x, stratum, ASCII, passphrase
  policy, full refresh, device record, ports, internal limits). Say what the user can do instead.
- One phrase per concept, everywhere, kept as a shared constant: statuses, refusal and HTTP error texts, the removed
  device, "Default router", "Discovery".
- Only point to actions that exist, by their real name.
- "Optional" once per field. No "(s)" plurals: write the count or rephrase.
- Step names follow one pattern: imperative, with the article ("Wait for the device to come back").
- No filler: no "easily", "seamlessly", "simply", "this allows you to", "please note", em-dash asides, "not X but Y".
- Docs use the same style. README and user docs are for users. Developer docs (CONTRIBUTING, plugins/README, plugin
  READMEs, specs) keep the technical detail developers need, in short sentences, without repetition. TERMS.md is fixed
  by the user and is never edited.

# Coding Rules

- `Directory.Build.props`: Nullable enable, ImplicitUsings enable, TreatWarningsAsErrors,
  LangVersion latest, analyzers on. `.editorconfig`: file-scoped namespaces, 4 spaces,
  `var` when the type is obvious.
- Async all the way, `CancellationToken` on every I/O method, no `.Result` / `.Wait()`.
- MVVM with CommunityToolkit (`[ObservableProperty]`, `[RelayCommand]`). No logic in
  code-behind. View models testable without Avalonia.
- Dependency injection everywhere (Microsoft.Extensions.DependencyInjection), also in the
  client.
- Organize by feature (`Devices/`, `Discovery/`, `Tasks/`, `Plugins/`), not by layer.
- **HARD RULE, reuse UI components.** Anything that appears in more than one place is one
  shared control, never a copy: in `src/Oadm.Sdk.Client/Controls` (namespace
  `Oadm.Sdk.Client.Controls`, XAML prefix `ui`) when plugin dialogs need it too, else in
  `src/Oadm.Client/Controls`. Existing shared ones: `IconLabel` (every icon + text row: navigation
  rail, dialogs), `ToolbarButton` (every toolbar button) and `ToolbarSeparator`, `ToolbarOverflowPanel` (every row of
  toolbar buttons: what does not fit goes behind its own More button; the search box stays outside it), `SearchBox`
  (every search field), `OadmIcon`, and for
  dialogs `DialogTitleBar`, `CardHeader`, `DialogFooter`, `StatusChip` (status chip),
  `FileRow` (chosen file + "Choose file"; `IsSingleButton`: one wide button that shows the chosen file's name, the
  compliance check form), `ProgressRow` (progress bar + status text), `LoadingOverlay` (a dialog that waits for a read: steps and cards hidden, ring and text in the middle, Cancel stays usable),
  `MessageWindow` (every message box and confirmation popup, host and plugins; dialogs never show
  inline "I understand" risk sections; line breaks start a new line, text between `**` is bold), `PasswordBox` (every password field: TextBox with bullet mask and eye button "Show password" /
  "Hide password", icons `Icon.eye` / `Icon.eyeOff`; never a `TextBox` with `PasswordChar`), `CodeView`
  (every read-only code / response display: JSON and XML pretty-printed with 2 spaces and highlighted,
  param.cgi `key=value` highlighted with `# Error` lines red, `Language` Auto/Json/Xml/KeyValue/Plain with
  Auto from the content type then the text, `IsFormatted` false = exact text; unparsable text plain, above
  512 K characters plain; monospace `Oadm.FontFamilyMono`, no wrapping, selectable and copyable; colors
  `Oadm.Code.*Brush`; logic in `CodeText` without UI), `CodeEditor` (every editable code field, a monospace text box for
  typing, caret, selection and undo with the same text drawn colored on top, aligned and scrolled with it; `Language`
  Json / Xml / KeyValue / Plain; never re-indented; the coloring of `CodeView` through `CodeHighlight`, also for JSON and
  XML that do not parse yet (`CodeText.Prepare(colorUnparsed: true)`: typing, an HTML body with several elements)), `FormField` (every labeled form row: label,
  input, error or hint below the input; classes `wide` and `inline`), `StatCard` (every monitoring count card: status icon +
  title, large count, small detail line; `IsOk` / `IsWarning` / `IsError` color icon and count, set only while the count
  needs attention; a card that filters a list sits in `Button.statCardButton`, the chosen one with `selected`), `DevicePickerWindow` + `DevicePickerViewModel` (every "choose devices" dialog, every managed device in address order, search over address, model, MAC and host name, Select all / Select none for what the
  search shows, the count "12 of 4,812 devices chosen", chosen devices can be unchosen, devices that cannot be used greyed
  with the reason; virtualized, 5,000 devices tested; `DevicePickerWindow.ShowAsync` returns the ids or null; `ShowOneAsync` (`DevicePickerOptions.SingleDevice`: checking a device
  unchecks the one before, no Select all / none) returns one id; with
  `DevicePickerOptions` a left column offers the device of the event, All devices, or Chosen devices and tags with the tag
  list (the devices of a chosen tag show checked with "Tag <name>"), `ShowChoiceAsync` / `DevicePickerViewModel.Choice`
  return a `DeviceChoice` {Kind All / EventDevice / Chosen, DeviceIds, Tags}; with tags it lists the devices' tags, every
  tag of the initial choice and `DevicePickerOptions.TagNames` (the rule editor passes the defined tags), count 0 when no
  device has it; summary "1 tag: 42 of 5,000 devices" / "2 tags and 3 devices: ...") and
  `PageDeviceChoice` (the device choice of every page that reads many devices: the purple "Selected devices" button text,
  All devices / tags / chosen devices resolved now, the Devices page selection to start with, the last choice kept on this
  computer; Action Rule Editor, Edge storage, Image Health Dashboard, Hardening scan, Snapshot report) and
  `DeviceIcon` (every device icon in a list: the category icon `Icon.device.*`, neutral when OK, amber for Credentials
  required / Password not set / Certificate changed, red for Unreachable, `ShowStatusToolTip` = the status message on
  hover; device grid, Web View, Live View), `DeviceTree` + `DeviceTreeModel` (every devices-by-tag tree: tag groups in
  name order with a chevron, "No tag" last, a device under each of its tags, sorted by address, search over address,
  model, MAC, host name and tags, optional filter, one reset per rebuild; Web View, Live View), `PageTab` (every tab of a
  page with tabs: title, close button, the active one underlined; theme `Border.browserTab`), `TileGrid` + `TileFrame` (every
  grid of tiles that fills its page: cells of `Oadm.Sdk.Layout.TileLayout` (size, smallest tile, drop size, most tiles;
  the same rules on the server), a frame per `ITileGridItem` of an `ITileGridSource`, while editing the cell lines and a
  frame per tile to move, resize and remove it, the selected tile (`ITileGridItem.IsSelected`) marked, `FrameClass` a theme class of the frame (`valueTile`: no video background), `CopyCommand` a copy icon left of the X (shown while it can run; the X is red, `Button.toolbar.danger`), `ShowPreview` where a
  dragged item lands, `ShowSelectionArea` a selection rectangle, `TileLayout.PlaceSized` the first free place of a size,
  `ITileGridSource.CopyText` the copy icon's tooltip; theme `Panel.tile`, `Border.tileEdit`, `Border.tileHover`, `Panel.tile.selected`, `Panel.tile.valueTile`, `Border.dropPreview`;
  Live View, Dashboards), `InfoIcon` (every "what does this mean" explanation: an info icon whose text shows on hover and click; `FormField.Info`
  puts one right of the input instead of a long hint), `WizardSteps` + `WizardStepItem` (the step
  row of every wizard: numbered dot and name, thin line between, current accent, done with a check, a step opens when
  every step before it is complete; theme `Button.wizardStep`, `Border.wizardDot`, `TextBlock.wizardLabel`,
  `Border.wizardLine`), `GroupedChoice` with `ComboBox.grouped` (every grouped select field: grey group headers that cannot be chosen, the
  choices indented below them, like the event list of a device). Before
  writing new XAML, check both `Controls/` folders and reuse; if a second place needs something
  that exists only inline, extract it into a control first.
- Plugin projects copy their output to `artifacts/plugins/<id>/` after every build. A running
  client or server keeps those files open; build or test with `-p:OadmSkipPluginDeploy=true` (e.g.
  together with `--artifacts-path`) to skip the copy while the apps run.
- **HARD RULE, validation errors directly below the field.** On every input of the client and of every plugin, a
  validation error appears directly below that input, like Vuetify: small red text (`Oadm.StatusErrorBrush`,
  `Oadm.FontSizeSmall`) aligned with the input's left edge plus a red input border, no space reserved without an
  error, the label stays level with the input. Never collected elsewhere: no error lists at the bottom of dialogs, no
  summary lines under cards (row errors stay in the row's Status column, a whole-table error directly below the
  table). Labeled rows are `ui:FormField` (label, input, hint or error below the input); the error itself is Avalonia's
  `DataValidationErrors` styled in `OadmTheme.axaml` from `INotifyDataErrorInfo`. Form view models derive from
  `Oadm.Sdk.Client.Validation.ValidatingViewModel` (rules per property on its `FormValidator`: `Rule`, `Rules`,
  `ShowAll`, `Reset`, `SetServerError`); errors show once the field was edited, the user left it (`FormField` on lost focus tells
  `IFormFieldVisits.FieldLeft` the bound property, an empty required field says so) or a submit was
  tried, never on an untouched form; submit buttons stay disabled while any error exists and their tooltip says why. Usage:
  `plugins/README.md` "Validation".
- **HARD RULE, loading feedback.** Everything that can take longer than 1 s at 30,000 devices shows that it is working
  from the first moment: a page or dialog that waits for a read `ui:LoadingOverlay` (ring and text, Cancel stays usable),
  a list or card that reloads `ui:ProgressRow` or a `ui:ProgressRing` in place, a button that started the work disabled
  while its row shows progress. The window never freezes without one: work that would block the UI thread for longer
  runs elsewhere or shows the ring first (the device grid: `DevicesView.RunBusyAsync`, a ring over the grid before a
  sort or regroup of a large selection). A new feature checks its waits at 30,000 devices.
- **HARD RULE, scale to thousands of devices.** A server manages up to 30,000 devices. Every list of
  devices, discovered devices, tasks, sources or tiles is virtualized (DataGrid, virtualizing
  ListBox/ItemsRepeater; never an ItemsControl/StackPanel creating one control per item), search,
  filter, sort and select-all are O(n) without per-item UI work, server calls are batched (one call
  for many devices, bounded parallelism on the server), per-device work is lazy for visible rows
  or summarized ("4,812 compatible, 188 missing API"), and images/snapshots load only for visible
  tiles. Every feature with a device list has a test with at least 30,000 fake devices (the device limit) that keeps
  filtering/selection/summary fast. Target size: **30,000 devices and 50,000 tasks in the history**.
  Concretely:
  - Collections bound to a grid are `RangeObservableCollection` (`Oadm.Sdk.Client.Collections`, plugin pages too): a
    filter, select-all, snapshot or batch is one `ReplaceAll`/`AddRange`/`InsertRange`/`RemoveAll`
    (one Reset), never one event per item. `GridSelection` hands a multi-item selection change to the
    view model collection in one step (`IResettableList`).
  - No `List.Contains`/`FirstOrDefault`/`Count(predicate)` over all rows inside a per-row loop or a
    per-event handler: index by id (`Dictionary`, `HashSet`) and keep counters up to date per change
    (add page summary, `TaskStore.ActiveCount`).
  - Stores apply a change in O(1) (`DeviceStore.ApplyBatch`, `TaskStore.ApplyBatch`), raise one
    `Changed` per batch with the affected ids, and an update that changes nothing raises nothing.
    Derived data follows only the affected ids (task device labels by device id).
  - Server-to-UI streams go through `ChangeBatcher` (one dispatcher post per burst).
  - A plugin page that reads or changes devices starts from the user's choice (`PageDeviceChoice` first in its toolbar,
    nothing read before a choice; one device: `DevicePickerWindow.ShowOneAsync`), never from every device by itself
    (`plugins/README.md` "Choose devices first").
  - Server: per-device work has bounded parallelism and is spread over time (polling); writes for many
    devices or tasks are one transaction (`RemoveManyAsync`, `SetManyAsync`, `AddRangeAsync`,
    `DeleteManyAsync`); lists that can grow without bound are paged or limited (task history, Watch
    snapshot) and have a retention; derived per-device results are cached per device-table version.
  - gRPC: never one message whose size grows with the device or task count when a stream or a page
    can carry it; sets of ids are sent in their shorter form (compact runnable sets).
  - Tests: `[Trait("Category", "Perf")]` for scale tests that take longer than about a second; they run
    with `manage test perf` (not in `manage test unit`), each with a generous but meaningful time
    budget, and write their measured times to the test output.
  - **HARD RULE, tests that never flake** (decision 2026-10-17: the former `Timing` category and the CI retry are gone;
    every unit test runs in CI once). A test never depends on how fast the machine is or what runs next to it:
    - Waiting for another thread: `Oadm.Sdk.Testing.Eventually.TrueAsync(condition, "what")` (checks every 10 ms, 30 s
      ceiling, returns as soon as it holds), never a fixed `Task.Delay` / `Thread.Sleep` before an assert. "Nothing else
      happens" checks wait first for a positive sign that the work ended; `Eventually.StaysTrueAsync` only when none exists.
    - Time in the code (timeouts, intervals, retries, debounce): a `TimeProvider`, and the test passes a
      `FakeTimeProvider` and advances it. Real timeouts in tests are generous (30 s); an upper bound only proves "it did not
      hang". Concurrency limits are tested with gates that hold the work, not with overlapping delays.
    - Speed is checked only in `Perf` tests (a budget about 10x the measured time, the measured time in the message);
      the functional part of such a test is a normal test.
    - Shared state: static knobs are restored after the test (better: per-instance settings); tests that set process-wide
      environment variables share one collection with `DisableParallelization`; one Avalonia headless session per test
      process (`Oadm.Client.Tests` runs without test parallelization: Avalonia's UI dispatcher is process-wide); temp
      folders, ports and file names are unique per test; no comparison with the wall clock that breaks at midnight.
    - The repository is found through `tests/Shared/TestRepository.cs` (the `RepositoryRoot` assembly attribute of
      `tests/Directory.Build.props`), never by walking up from the build output.
- Core plugin pages never repeat the page title in a card heading: the description goes into
  the host page header via `ui:PageHeader.Subtitle` (2 px top margin, theme); the status never sits top right in the page
  header but directly left of the Save button it reports on (or next to the value it describes, e.g. the CA validity on the
  PKI page); the card starts with
  the form. User-facing texts use plain language (no protocol jargon such as stratum, DISCOVER/OFFER).
- Dialogs with a single operation (one form: back up, import, generate, static lease, export) have no card: the form sits
  in `Border.dialogBody` (theme, padded like `ui:MessageWindow`) between title bar and footer, and the window sizes to its
  content (`SizeToContent="Height"`, fixed width). Cards only in dialogs with several sections or a list.
- Button and menu labels never end with "..." / "…", also when they open a dialog or a file picker ("Back up", "Import CA",
  "Choose file"). Actions inside a card are filled buttons (`Button.secondary` with `ui:IconLabel`, primary for the main
  one); the flat `ui:ToolbarButton` is only for toolbars.
- One accent button per view (exception, the purple device choice button: "Selected devices" of the event rule and
  trigger dialogs and of VAPIX Commander's target devices, "Selected cameras" of Edge Recording Export): while an inline editor is open, its button (Retry, Create tag) is the primary one and the
  footer's main button turns secondary. A disabled primary keeps its role: the accent at about 35 % with dimmed text.
- **Destructive actions are red**: `Button.danger` (red fill, white text) instead of primary for
  buttons that delete or remove (Remove users, Delete certificates), `Button.toolbar.danger` for flat toolbar and icon
  buttons (Delete all, Delete tag, credential Remove, user Delete), `Button.link.danger` for row links (DHCP Delete and
  Release, PKI Remove). `ui:MessageWindow` makes its confirm button red when the confirm text starts with Delete, Remove
  or Release (`MessageWindow.IsDestructive`, `IsDanger` overrides). Destructive entries sit apart from everyday ones
  (separator, last in a menu).
- `ui:DialogFooter` with no visible action puts its lone Close / Cancel on the right.
- Grids whose status text explains a problem (add page, address tables, firmware, certificate install, system report,
  hardening detail) have the class `wrapRows`: rows grow and `ui:StatusChip` shows up to two lines (`MaxLines`).
  Numeric columns (days, offsets, scores, durations) are right-aligned: `CellStyleClasses="number"` plus a header
  `TextBlock.numberHeader`. Empty lists say in one line what to do first.
- **HARD RULE, no style differences.** Same kind of element, same look, everywhere: one
  style per element type in `src/Oadm.Theme/OadmTheme.axaml`, no local overrides of font size,
  weight, color, padding or alignment in views. No special cases such as a bold selected rail
  item or a greyed toggle unless the spec says so. When a mismatch is found, fix the shared
  style or control, not the single view.
- Tests never assert exact positions, sizes, pixel offsets or column widths of controls (overkill); headless screenshots are for looking, assertions check behaviour, content and rules.
- UI is verified only with Avalonia headless tests rendering offscreen (screenshots via
  `OADM_SCREENSHOT_DIR`). Never automate the real desktop: no simulated clicks or drags, no
  capturing real windows on a developer machine.
- Tests accompany every non-trivial class. VAPIX parsing tested from recorded fixtures; RTP
  depacketizers from recorded RTP (`tests/Oadm.Core.Tests/Fixtures/LiveView`, re-record with
  `RtpRecorderTests` and `OADM_RECORD_RTP_DIR`).
  Discovery and task engine tested with fakes, no network in unit tests.
- **EF migrations are never deleted or squashed**: a backup of any older OADM is restored by running them (see "Backup
  and restore"); a schema change is always a new migration.
- Commits: conventional commits (`feat:`, `fix:`, `chore:`), small and focused. No `Co-Authored-By` or `Claude-Session`
  lines in commit messages.
- This file is the single source of truth for the spec. When a decision changes, change it
  here in the same PR.

# Sites (in development, branch `feature/sites`)

Spec and decisions: `docs/sites.md`. Built so far (2026-10-19): the contracts and the link (`Oadm.Sites`), routing
(see "Device Communication"), Site offline (see "Data Model"), the node program (`Oadm.SiteNode*`) and its installers, and
the server side:
- `Oadm.Server.Sites`: `SiteManager` (`ISiteStates`, `ISiteTransport`, `ISiteLargeTransfers`; `SiteOptions`: offline after
  30 s without an answer, online after 60 s stable, after a start "starting" until the node settles or 2 minutes, while
  starting no site events, compliance results stay and devices' status events start from `Device.StoredStatus`),
  `SiteListener` (TCP `Sites.ListenUrl`, default 0.0.0.0:5081, only while `Sites.Accept`; handshake timeout, 64
  handshakes at once, 10 new unknown connections a minute per address, 50 waiting nodes, dropped after 24 h),
  `SiteSession` (control link with Watch and a 10 s Hello heartbeat and the round trip, up to 8 worker links for Dial,
  a new worker when every one carries 2,000 dials), `SiteIdentityStore` (`Sites.Identity`, purpose `oadm:site-identity`;
  dropped with a replaced master key and named in the notice), `SiteAdmin` (Remove with or without devices, Replace),
  `SiteGrpcService`. At most 2 large transfers per site (streamed bodies over 1 MB or of unknown size, streamed answers;
  the wait does not count against the device timeout).
- Permission `sites.view` "See sites" (Devices, Operator and Viewer yes); every SiteService change is Administrator only.
  Audit: Turned on / off site nodes, Bound site, Wrong pairing code, Renamed site, Changed site address, Replaced site,
  Removed site, Site offline / online (system). Backup: "Sites: N restored".
- Client: rail page **Sites** (group Servers, `HostPages.Sites`, icon `server`, `Sites/SitesViewModel`, one dialog
  `SiteFormWindow` for Bind / Rename / Change address / Add manually / Replace / Remove), device column **Site**
  (`Device.site_name = 31`, search and export); fake mode Building B (online), Warehouse (offline) and a waiting node.
- Plugins at sites (stream 6): `IDeviceNetwork` (`Oadm.Core.Sites.DeviceNetworks`: `LocalDeviceNetwork` on the server's
  own network, `SiteDeviceNetwork` over `ISiteNetwork`) in `ITaskExecutionContext` / `ITaskQueryContext.Network` and
  `ICorePluginContext.Network` (`PluginNetworkScope`: listeners bound by the host with the Windows firewall rule, a port in
  use retried every 30 s). `ICorePluginContext.Sites` = relays over the node's Relay call on worker links (`SiteRelay`,
  states with `SiteTexts.RelayStateText`, reopened after a reconnect); the host disposes a plugin's listeners and relays
  when it stops or is turned off. `siteSupport` (manifest; missing = none; `ITaskPlugin` / `IComplianceCheckType.SiteSupport`
  override) is enforced by the host: tasks greyed "Not available for devices at a site", runs and queries refused,
  compliance Not checked, VAPIX clients and event streams of core plugins (`SiteSupportGuard`) and actions refuse site
  devices. The plugin project SDK writes `siteSupport` (`OadmSiteSupport`, default full) and `sitePorts` (`OadmSitePorts`
  "udp:123:NTP server"); bundled: DHCP none, NTP full + UDP 123, the others full. NTP "Serve at sites" (one relay per site,
  limits per site and client); DHCP page "The DHCP server works only on this server's own network."; Assign IP address
  refuses addresses outside the site's networks (`IDeviceNetwork.Reaches`). The Plugins page has a Sites column
  (`PluginPackageInfo.site_support = 12`, `site_ports = 13`). `Oadm.Sdk.Testing`: `FakeDeviceNetwork`, `FakeSites`,
  `FakePluginNetwork`, `InMemoryUdpRelay` / `InMemoryTcpRelay`. SDK 0.2.0. Developer page `dev/sites.md`.
- Discovery at sites (stream 5): every add mode asks the server's own network and every online site (`ISiteNetwork`
  over the control link, `SiteManager.Network`; `Reaches` from the node's Watch). Results carry their route
  (`DiscoveredDevice.site_id = 21`, `site_name = 22`, `other_sites = 23`, `problem = 24`); the same MAC address through
  several routes is one row through the site, the others in the Site tooltip; different devices at one address through
  different routes are separate rows. Commit stores `Device.SiteId`. Import from file reads a Site column (by name); a line
  without a site that reaches different devices lists all, unchecked, with "Answers through Building A and Building B.";
  an ADM line only uses the route where its MAC answers. The waiting list is per (site, address)
  (`WaitingDeviceStore.Normalize(address, siteId)`, `WaitingDevice.site_name = 15`, column Site). Moves: Unreachable
  devices and devices of a site offline for more than an hour are looked for through every route and moved with route and
  address together (audited "Device moved", "from Building B 192.168.0.20 to This server 10.0.0.20"); a device answering
  on its route never moves, names never move, a DHCP lease moves to the server's network. The address conflict check of
  Network settings / Assign IP address lists only devices on the same route. The address link and Open web interface of a
  device at a site open it in Web View (`ICorePluginClientContext.OpenDeviceRequested`), disabled with "Turn on Web View to
  open devices at a site" while it is off. Texts `Oadm.Contracts.Sites.SiteDiscoveryTexts`.
- Not built yet: identity rotation over gRPC, "Waiting for the site link" on
  task steps, the HTTP helper on `IStreamListener`.

# Not built yet

**Sites** (designed 2026-10-15 as "Agents", re-evaluated and renamed 2026-10-19, spec `docs/sites.md`; first refactor
on branch `feature/agents`): an OADM Site Node (service + desktop app, MSI and .deb) in a network the server cannot reach;
it connects out to the server (no inbound port at the site), is bound with a pairing code from its app, opens only allowed ports, and the server sends all device traffic through it, TLS end to end; status and events
"Site offline"; plugins work unchanged unless they open their own sockets.

SSDP and WS-Discovery, scheduled and repeated tasks (retry, recurrence), warranty and replacement data
from Axis online services, localization, installer signing (checksums instead), OS keyring for the master key,
least-privilege service accounts.

# Resources

- ADM user manual: https://help.axis.com/en-us/axis-device-manager
- VAPIX library: https://developer.axis.com/vapix/
