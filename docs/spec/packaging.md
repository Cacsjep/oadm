# Packaging, CI and website

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

The file layout of every installer and the user-facing install notes are in `packaging/README.md`.

## Publish

- Server and client publish as one self-contained single-file exe per platform (`PublishSingleFile`,
  `IncludeNativeLibrariesForSelfExtract`, `PublishReadyToRun`), see `manage publish`. Every plugin project is published
  once into `artifacts/publish/plugins/<plugin id>/` (`<OadmPluginId>`, server and client part together) and copied next
  to both exes in `plugins/<plugin id>/`, so a new plugin project is packaged without further changes.
- The client gets the offline docs of Help (`docs/spec/help.md`) as `docs/` next to its exe: `manage publish` copies
  `artifacts/docs-offline/` (`manage build docs-offline`; in CI the job `docs`) when it exists, else it warns. The MSI and
  the .deb carry it in the client folder, the .pkg in `OADM.app/Contents/Resources/docs`. The server and the OADM Site
  Node carry no docs.
- `--version` (default `0.1.0-dev`, a leading `v` is removed) goes to every assembly (`-p:Version`). In CI the version
  comes from the tag (`v1.2.0` -> `1.2.0`); MSI and .pkg use the numeric part.
- Native AOT and trimming are never used for the host apps: AOT cannot load managed plugin assemblies at runtime and
  trimming removes framework APIs plugins rely on. Small helper tools without plugin loading may use Native AOT.
- Code never uses `Assembly.Location` (empty in single-file); use `AppContext.BaseDirectory`.
- **FFmpeg:** the client bundles the decoder libraries (avcodec, avutil, swscale, swresample; FFmpeg 9, LGPL-2.1 build
  from the `DevEnvy.FFmpeg.Binaries.LGPLv2.Runtime.<rid>` package, bindings `FFmpeg.AutoGen`). They are native shared
  libraries embedded in the single-file exe and extracted by the .NET host at startup (`DOTNET_BUNDLE_EXTRACT_BASE_DIR`);
  the publish folder contains only the exe. They add about 57 MB to the win-x64 exe. `OADM_FFMPEG_DIR` loads a
  user-supplied build instead (LGPL replaceability), `Oadm.Client --check-decoder` verifies the decoder without a window.
  Never add GPL or nonfree FFmpeg builds.

## Notices and terms of use

- After the publish `manage publish` runs `tools/Oadm.Notices` (generator, never shipped): it reads the published
  `*.deps.json` files (server and client from `obj/Release/<tfm>/<rid>/`, every plugin folder) and writes
  `THIRD-PARTY-NOTICES.txt` next to each exe: the hand-written `THIRD-PARTY-NOTICES.md` (FFmpeg, Inter, .NET; licenses and
  source offer), every NuGet package and runtime pack with license, copyright, project and where it is used (server,
  client, plugin x), the full license texts from `packaging/notices/licenses/` (MIT, Apache-2.0, BSD-2/3-Clause, OFL-1.1,
  LGPL-2.1; Apache-2.0, LGPL-2.1 and OFL-1.1 always) and the license and notice files the packages ship (root
  `LICENSE*` / `NOTICE*` / `THIRD-PARTY-NOTICES*`, the nuspec license file, `legal/**`), deduplicated by content; a
  license without a text is a warning.
- A plugin folder with its own `LICENSE`, or a folder of a plugin with one (PKI's `Est/` and `Radius/`, the EST and
  802.1X servers), is named at the top ("Except the part Est of the plugin oadm.pki: it has its own license
  (plugins/oadm.pki/Est/LICENSE)").
- `LICENSE.txt`, `NOTICE.txt` (the root `NOTICE`: "Copyright 2026 The OADM contributors"; `LICENSE` keeps the Apache
  appendix template unchanged), `LGPL-2.1.txt` and `TERMS.md` are written next to each exe too (MSI folders, the .pkg
  app's `Contents/Resources`, .deb `/usr/share/doc/<package>/`); the About page starts its license sentence with the same
  copyright line.
- **Terms of use** (`/TERMS.md`: plain-language disclaimer, "as is", no liability for device changes, not affiliated with
  Axis; text fixed by the user) are accepted on the **first start of the client** on each computer, before the splash
  and any connection (`Oadm.Theme.Shell.TermsWindow` + `TermsDialogViewModel`, shared with the site node app; the
  client's `Shell/TermsViewModel`: the terms and the license below them, Quit / Accept, the X quits;
  `ClientSettings.AcceptedTerms` = SHA-256 of the text, so changed terms ask again). The installers show no license page
  (silent and command-line installs skip it, Debian packages never ask; every user runs the client). TERMS.md also
  stands at the top of the client's About page (embedded resource `Oadm.Client.TERMS.md`), in `/usr/share/doc/<package>/`
  and in every publish folder and installed app folder.

## Installers

`manage package <windows|linux|macos> [--rid] [--version]` into `artifacts/packages/`. Every installer offers **"Server
and client"** (default) or **"Client only"** and ships `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` and `LGPL-2.1.txt` with
each app.

- **Windows MSI** (WiX Toolset 6 via the `WixToolset.Sdk` MSBuild SDK, `packaging/windows/`, not in Oadm.sln; never WiX
  7, it requires the OSMF EULA), x64 only, per machine: `Program Files\OADM\Server` and `\Client`, Windows service "OADM
  Server" (`OadmServer`, automatic, LocalSystem, restart on failure, `--Oadm:DataDir="%ProgramData%\OADM"`), Start menu
  shortcut, firewall rule for `Oadm.Server.exe` TCP 5080, profiles Domain and Private (`Profile="[OADM_FW_PROFILE]"` = 3);
  TCP 5081 for site nodes is the server's own rule while Accept site nodes is on (`docs/spec/sites.md`). Fixed
  UpgradeCode + MajorUpgrade (same version reinstall allowed). Features `Client` (always) and `Server` (service, firewall
  rule, data folder); own UI `WixUI_Oadm` (welcome, page "Choose what to install" with the radio buttons "Server and
  client" / "Client only" = AddLocal / Remove of `Server`, ready, progress, finish; Change in Apps and Features opens the
  same page, upgrades keep the choice through MigrateFeatureStates); silent `ADDLOCAL=Client` = client only.
  `%ProgramData%\OADM` is created for SYSTEM and Administrators only and never removed. Files are harvested from the
  publish folders.
- **Linux .deb** (`dpkg-deb`, `packaging/linux/build-deb.sh`, amd64 and arm64, built on Linux or in a container), two
  packages: `oadm-server` (`/opt/oadm/server`, systemd unit `oadm-server.service` (root, `Type=notify`,
  `Restart=on-failure`, `OADM_DATA_DIR=/var/lib/oadm`, StateDirectory 0700, optional `/etc/default/oadm-server`);
  postinst enables and starts it like dh_installsystemd and works without systemd; prerm stops it; data kept even on
  purge) and `oadm-client` (`/opt/oadm/client`, `/usr/bin/oadm-client`, `oadm.desktop` + hicolor icon, X11 libraries as
  Depends, `libwebkit2gtk-4.1-0` as Recommends for the Web View). Both files in one `apt install` = "Server and client",
  `oadm-client` alone = "Client only". No metapackage. Both take over the files of the former single package `oadm`
  (`Replaces: oadm (<< 0.0.3~)`, `Breaks: oadm`, which also removes the metapackage `oadm` of 0.0.3..1.0.0 on upgrade).
  Each has `/usr/share/doc/<package>/{copyright,THIRD-PARTY-NOTICES.txt,LGPL-2.1.txt}`. Pre-release versions are written
  `1.2.0~rc.1`. No .rpm.
- **macOS .pkg** (`pkgbuild` + `productbuild`, `packaging/macos/build-pkg.sh`, x64 (Intel) only, built on macOS only):
  `/Applications/OADM.app` (Info.plist, icns, license texts and plugins in `Contents/Resources`), server in
  `/Library/Application Support/OADM/server`, LaunchDaemon `com.oadm.server` (root, `OADM_DATA_DIR=/Library/Application
  Support/OADM`, `OADM_SERVICE=launchd`, folder 0700), `uninstall-oadm.sh` next to the server and in the app's Resources.
  Choices (`customize="allow"`): `com.oadm.choice.client` "OADM client" (always) and `com.oadm.choice.server` "OADM
  server" (LaunchDaemon + server; uncheck for "Client only", or `installer -applyChoiceChangesXML`). Unsigned
  (executables ad hoc signed); Developer ID signing and notarization are not built yet.
- **OADM Site Node** (`manage package site-node [--rid win-x64|linux-x64|linux-arm64] [--version]`):
  `OADM-SiteNode-<version>-win-x64.msi` (WiX 6, `packaging/windows/site-node/`, its own project: the main installer
  project does not pick up its .wxs files; `Program Files\OADM Site Node` on the system PATH, service "OADM Site Node"
  `OadmSiteNode` automatic, its own virtual account `NT SERVICE\OadmSiteNode`, restart on failure (also after a stop with
  an error: a part of the node that fails ends it with exit code 1), data folder `%ProgramData%\OADM Site Node` for SYSTEM
  and the service SID only and kept, Start menu shortcut, silent `SERVER=<address>`), `oadm-site-node_<version>_<arch>.deb`
  (service + `/usr/bin/oadm-site-node`, user `oadm-site-node`, `CAP_NET_BIND_SERVICE`, `RuntimeDirectory`,
  `NoNewPrivileges`, `/var/lib/oadm-site-node` 0700) and `oadm-site-node-app_<version>_<arch>.deb` (Depends
  oadm-site-node of the same version, Recommends pkexec, icon `oadm-site-node.png`); amd64 and arm64; notices
  `tools/Oadm.Notices --product site-node` (hand-written part `packaging/notices/site-node.md`, no LGPL part); no macOS
  package, no portable ZIP. The node only connects out: no firewall rule on either system.
- **Plugin folders a version no longer ships** (`oadm.est` and `oadm.radius`, parts of PKI, `docs/spec/pki.md` "One
  plugin") are removed on upgrade: the MSI removes the old version first (MajorUpgrade `afterInstallValidate`), dpkg
  removes the old package's files and the server and client postinst remove what is left in those two folders, the .pkg's
  server and client preinstall remove them (an installer upgrade keeps files the new package lacks). Server and client
  skip such a folder anyway ("Merged into PKI").
- **Service:** the server runs as root / LocalSystem (NTP and DHCP plugins need UDP 123 and 67). As a service the host
  integrates with the SCM and systemd (`Oadm.Server.Hosting.ServiceHosting`: `AddWindowsService`, `AddSystemd`, content
  root = app folder; no-ops on a console). `ServiceHosting.IsRunningAsService()` (Windows service, systemd, or macOS with
  `OADM_SERVICE=launchd`; `OadmServerHostOptions.ServiceMode` overrides it, tests) switches on the service hardening of
  `docs/spec/security.md` "Service: firewall and folders": folder checks (`FolderGuard`), netsh firewall rules for
  plugins, no development plugin folder. Services set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a folder only the service
  account can write (`%ProgramData%\OADM\runtime`, `/var/cache/oadm`, `/Library/Application Support/OADM/runtime`), never
  a shared temp folder.
- **Icons:** the app icon is `icon/oadm-app-icon-<size>.png` (16-1024); `packaging/icons/make-icons.py` builds `oadm.ico`
  (client exe, MSI, shortcuts), `oadm.icns` (macOS), `oadm.png` / `oadm-256.png` (Linux, window icon of every client
  window) from it.

## CI

One release workflow, `.github/workflows/release.yml`, only on release tags `v*.*.*` (e.g. `v0.0.1`); a job starts only
when the jobs it needs passed:

- `tests` (Windows, Linux, macOS, 30 min): `dotnet test` with the unit filter
  `Category!=Hardware&Category!=Perf&Category!=HardwareWrite`, one run without a retry, `--blame-hang-timeout 5m
  --blame-crash`: a crashed test host leaves a dump and its test sequence, uploaded as the artifact `test-results-<os>`
  when the step fails.
- `installers` (needs tests and docs, 60 min): MSI, the .deb packages for amd64 and arm64 (on `ubuntu-24.04-arm`), .pkg,
  the OADM Site Node MSI and .deb packages, and the portable ZIPs `OADM-Server|Client-<version>-<rid>.zip` (their own
  artifact `portable-<version>-<rid>`). Takes the offline docs of the job `docs` into the client. Checks the license
  texts and the client's `docs/index.html` next to the published exes; installs, checks and removes the .deb packages as
  "Server and client", as "Client only" and the site node; checks the .pkg's choices and payload without installing.
- `docs` (15 min, next to the tests): `npm run build:offline` in `website/` once, uploaded as the artifact `docs-offline`.
- `smoke-windows` (needs installers, 30 min): the MSI installed, checked and removed both ways by
  `packaging/windows/smoke.ps1`, which prints the time on every line and a heartbeat with free memory and the busiest
  processes while a program runs. Its own job because the Windows runner sometimes stops answering there, so a re-run
  repeats only this job; removals run with Restart Manager off, so they never close a program of the runner, and a failed
  one prints the failed MSI action. `smoke-windows-node` (needs installers, 15 min): the site node MSI the same way
  (`smoke.ps1 -Mode SiteNode`) on a runner of its own.
- `nuget-pack` (needs tests): `manage package nuget` with the tag's version, every template created, built and tested
  against only these packages (`packageSourceMapping` `Oadm.*` to the local folder), uploaded as the artifact
  `nuget-packages`.
- `release` (needs all of them): the GitHub release of the tag with the installers, the portable ZIPs and
  `SHA256SUMS.txt`; the release text is the tag's section of `CHANGELOG.md` (GitHub's generated notes without one). A
  re-run of a moved tag first deletes the release's assets it did not build, so the release holds exactly what
  `SHA256SUMS.txt` lists. A tag with a pre-release part (`v3.5.0-rc.1`) makes a GitHub pre-release that never becomes the
  latest release, so the website's download buttons (`/releases/latest`) stay on the last real release.
- `nuget` (needs release and nuget-pack): `dotnet nuget push` with NuGet Trusted Publishing (`NuGet/login`, `id-token:
  write`, no API key in GitHub); skipped without the repository variable `NUGET_USER` and for a pre-release tag (nuget.org
  keeps every version forever).

Other workflows: `pr.yml` builds and unit-tests every pull request on Linux (check "tests ubuntu-latest", the same filter
and blame options, artifact `test-results`); changes only in `website/`, `docs/`, Markdown files (`*.md`) or the release
and Pages workflows skip CodeQL and the .NET build and tests (`pr.yml` still reports its required check, with the steps
skipped). `codeql.yml` runs CodeQL (C#) on pushes to main, pull requests and weekly. `pages.yml` deploys the website.
Plugin SDK packaging: `docs/spec/plugins.md` "Loading and packaging" and `website/src/content/docs/dev/plugin-sdk.md`.

## Supply chain and security

- Every GitHub Action is pinned by its full commit hash (the version as a comment). `.github/dependabot.yml` opens weekly
  pull requests for the Actions (new hashes) and the NuGet packages (grouped: Avalonia, gRPC, Microsoft, Serilog, test
  packages; ignored: JsonSchema.Net 9+ and WixToolset 7+, whose license moved to the OSMF EULA) and the npm group
  `website`.
- Vulnerable transitive npm packages of the website are pinned with `overrides` entries in `website/package.json`
  (postcss-selector-parser 7.1.6; katex 0.18.9 under mermaid 11) until their parents update; npm versions are pinned
  exactly.
- Dependabot alerts and security updates, private vulnerability reporting and secret scanning with push protection are
  on; `SECURITY.md` says how to report.
- Rulesets on `main`: no force push or deletion (everyone), changes through a pull request with the passing check "tests
  ubuntu-latest" (administrators bypass). Minimal issue templates (bug, feature) and pull request template (one line).
- Checkout runs with `persist-credentials: false`; workflow permissions are `contents: read` except the release job
  (`contents: write`), the nuget job (`id-token: write`) and the Pages deploy job (`pages: write`, `id-token: write`).

## Website

Landing page and user docs in `website/` with Astro Starlight, own domain https://oadm.app/ (DNS at world4you: four A
records to GitHub Pages, `www` CNAME `cacsjep.github.io`; custom domain and Enforce HTTPS in the Pages settings; the
cacsjep.github.io/oadm addresses redirect). Texts follow "Wording".

- **Deploy:** `pages.yml` on pushes to main that change `website/`, `CHANGELOG.md`, `samples/`, the SDK or the API docs
  generator, and after every successful `release` run (`workflow_run`, so the static download links match the new
  release; the browser also re-reads the latest release with `cache: 'no-store'`). The same job builds and checks the
  offline docs (`npm run build:offline`, not published).
- **Screenshots** in `website/src/assets/screens/` come from the headless tests: the tests write them to
  `OADM_SCREENSHOT_DIR`, `npm run screens -- <folder>` (`website/scripts/screens.mjs`) copies them by the map
  `website/scripts/screens.json` (website name to test output name; every image of the folder must be in it); they are
  committed by hand after a look, never in CI; click to enlarge; dark only (no theme switch).
- **Top bar** of the landing page (`TopBar.astro`, instead of the docs header): GitHub, LinkedIn, Discord and YouTube left
  (the docs header the same); a PRO badge (to `#pro`), **Activate** (to `/activate/`), Support Forum
  (https://support.oadm.app/), Documentation and Bug report right (a menu: Support Forum for users, GitHub issue for
  developers); phones narrower than 40rem see icons only; fixed, no border.
- **Download** (`Downloads.astro`): one accent button "Download <version>" that opens a dialog with every file of the
  latest release: tabs Windows, Linux x64, Linux ARM64, macOS (the visitor's system first, each icon in the system's
  color), each a plain list of its files without group headings (Windows "Server and Client Installer", "Site Node
  Installer"; Linux: Server, Client, Site Node, Site Node App; the site node has no macOS build), and a tab Portable with
  the server and client ZIPs of every system under a heading per system; one row per file with its name, file name and
  size; a group or row whose file the release lacks is hidden. Below them How to install, Release notes (the changelog
  page), Checksums (`SHA256SUMS.txt`) and All files on GitHub.
- Below the button: the intro video (`IntroVideo.astro`, `public/intro.mp4` with the preview image `intro-poster.jpg` and
  a play button in the middle; loaded on click, with sound and controls), then one quiet row of grey links without boxes
  or headings (`.dl-links`): **Support Forum**, **Newsletter** (an email field that posts to the support forum; double
  opt-in by mail, no forum account), **YouTube** (https://www.youtube.com/@oadm-ca) and **Documentation**
  (`start/install`).
- **Forum check:** the page checks the forum on every visit (`src/lib/forum.ts`: `GET /api/health`, 2 s; any answer
  counts) and while it does not answer greys out the Support Forum links, the newsletter and the Bug report menu's
  Support Forum entry with "Currently not available. We are working on it.".
- **Tools:** "Explore the tools", one card per page with a screenshot (`PluginCards.astro`, Compliance first, then the
  plugins in rail order; EST server and 802.1X server with a PRO badge next to the name; folded by `Reveal.astro`: about
  one row fading out, "Show all N tools", without JavaScript all). Then Security (three points) and "Extend OADM with
  plugins".
- **OADM PRO** (`OadmPro.astro`, `#pro`): its own panel with a violet glow; below its lead **Start 30-day trial** (accent,
  to the License section of the Settings docs: the trial starts in OADM itself) and **Activate license** (to
  `/activate/`); icon cards; a plan lights up on hover (none recommended); no buy button: "Estimated launch: end of
  November 2026"; commercial support with an SLA, feature requests with a higher priority, the enterprise plugin EST
  server.
  - Plans per two years (the maintenance period of project offers): Individual (support for one plugin on one server,
    for a project offer) from €440, up to 500 devices per server €940, up to 30,000 per server €4,870, Unlimited (a
    license key for each server of one company, any number of devices) €9,740 (prices end in 40, 50 or 70, never 99 or
    round), without VAT; every plan with SLA support, the enterprise plugins and a 30-day trial. Below the plans:
    renewal before the end 15% less, three years for tenders on request (quote form) (planned, final at launch).
  - Then "Custom plugins" (built to order with support; the customer chooses Open source, In OADM PRO or Only for you;
    price on request), "Support with an SLA" (`#sla`, linked from the plans' "Support with an SLA" lines: help within 1
    to 3 working days from the person who builds OADM, no fixed times per level; covered / not covered; planned terms),
    last "Need something else?" ("Send us a message" opens the quote form with "Something else").
  - A click on a plan (or "Request a custom plugin") opens the quote form (plan, name, company, email, optional phone,
    country, devices, message; a honeypot) that posts to the support forum's quote endpoint (the forum mails it to the
    maintainer; its rules are documented in the forum's own repository); while the forum does not answer or the request
    fails, the form says so and links contact@acs-dev.org with the request filled into the mail.
- **Search:** the landing title names "AXIS Device Manager alternative"; the page `start/axis-device-manager-alternative`
  compares with ADM (the same work, more in OADM, "More in OADM" names Sites; "Not in OADM yet": firmware lookup,
  warranty data; no claims about ADM); `StructuredData.astro` (SoftwareApplication JSON-LD).
- **Changelog** (Project > Changelog, `project/changelog`) is written from `CHANGELOG.md` by
  `website/scripts/changelog.mjs` before every build and dev start (npm `prebuild` / `predev`, git-ignored).
- **SDK reference** (Developers > SDK reference, `/api/`): `tools/Oadm.ApiDocs` reads the built `Oadm.Sdk` and
  `Oadm.Sdk.Client` with Roslyn (their doc comment files: `GenerateDocumentationFile` in both projects, CS1591 off, never
  published: `PublishReferencesDocumentationFiles` false) and writes one page per public type (signature with
  nullability, members, XAML properties of controls, enum values, links between types and to the .NET docs, "Used by",
  the guide page) into `website/src/content/docs/api/` and the sidebar into `website/src/generated/api-sidebar.json` (both
  git-ignored; the site builds without them). `manage build docs` locally, `pages.yml` before every deploy. Overrides,
  record and record struct plumbing are left out; a partial property of a source generator gets its written comment from
  the source. Tests `tests/Oadm.ApiDocs.Tests`: every public type has a page, **every type, member and XAML property has a
  description** (a new SDK member without a comment fails), every link and anchor resolves.
- **Diagrams:** ```mermaid blocks render in the browser (`astro-mermaid` 2.1.0 + mermaid 11, before Starlight in the
  integrations, theme `base` dark with the site colors); used for the architecture, plugin kinds, task runs, event rules
  and the tutorials.
- **Developer pages** also cover debugging (`dev/debugging.md`) and SDK versions (`dev/compatibility.md`); the admin page
  Plugins has "Plugins from others" (plugins run with the server's rights). Developers > **Develop with the .NET SDK**
  (`dev/plugin-sdk.md`): the packages, the templates, the project SDK's properties, the development server, debugging,
  deploy into the installed OADM, the `.oadmplugin` file, every `oadm` command.
- **Tutorials** (Developers: "Tutorial: Task plugin", "Plugin page", "Event rule action", `dev/tutorial-*.mdx`): one page
  each with every file, self-contained (no jumping to other pages), NuGet first (`dotnet new oadm-task|page|action`, the
  `oadm` tool; the checkout's `manage new` in an aside); the code is imported raw from `samples/` (`?raw` + Starlight
  `Code`; `vite.server.fs.allow` includes the repository) and shown as the templates write it
  (`website/src/components/templateCode.ts`: no `Oadm.Samples.` prefix, `Sdk="Oadm.Plugin.Sdk/x.y.z"`), so the pages
  always show code that builds and passes its tests. A change of a sample changes its tutorial, what `manage new` creates
  and the templates: keep the tutorial text in line.

## Activation page

`/activate/` (`src/content/docs/activate.mdx`, splash with a hidden hero like the landing page, the landing top bar and
footer, not in search; `Activate.astro`): "Activate OADM PRO", "Find the server ID in OADM under Settings > License.", one
card **Activate license** (Server ID, License code), below it "No license yet? In OADM, open Settings > License and click
Start 30-day trial. Once per server.". `?server=<id>` fills the server ID (the link of OADM's License card). Checks in the
browser, each error directly below its field: the server ID is a GUID, the code `OADM-XXXX-XXXX-XXXX` (typed in any case,
with or without dashes and the OADM prefix). It posts to the support forum; a license comes back as a file and is
downloaded at once as that file, with **Download again** and "In OADM, open Settings > License and click Import
license.". A refusal shows the forum's text; no answer (forum down, network error) says the server looks offline and
links contact@acs-dev.org with the request in the mail.

## Developer commands

- One entry point per shell at the repo root, `./manage.sh` (bash) and `.\manage.ps1` (PowerShell 5.1/7), verb + target
  (`build`, `run`, `test`, `publish`, `package`, `new`, `license`, `clean`, `info`), `manage <verb> help` at every level.
  Both read their help texts from `scripts/manage-help.txt`; any new verb, target or option goes into both scripts and
  that file.
- The plugins (`build`, `run` server / client) are built in one MSBuild run in parallel through a generated solution
  filter `artifacts/plugins.slnf` (every `plugins/*/*.csproj`, which must be in `Oadm.sln`).
- `manage package nuget [--version]` writes the plugin SDK packages to `artifacts/nuget` (templates generated into
  `artifacts/templates` first); `manage license` runs `oadm license` of `tools/Oadm.Tools`.
- **`manage new task|page|action --name "<name>" [--id <id>]`** (`tools/Oadm.NewPlugin`): copies that sample into
  `plugins/Oadm.Plugins.<Name>(.Client)` and `tests/Oadm.Plugins.<Name>.Tests` with the sample's name, namespaces, id
  (default `community.<kebab>`) and display name replaced, test references pointed at `plugins/`, a
  `Directory.Build.props` / `.targets` in each plugin project folder that imports the plugin project SDK from source
  (`src/Oadm.Plugin.Sdk/targets/Oadm.Plugin.Repo.*`), and adds the projects to Oadm.sln (with the dotnet the script
  found); existing folders are never overwritten; names that would clash in code (Task, Plugin, ...) are refused.
  `Oadm.NewPlugin --templates <folder> --version <v>` (`Templates.cs`) writes the content of the template package from
  the same samples (with a `README.md`, an `AGENTS.md` with the plugin rules for AI assistants and a one-line `CLAUDE.md`
  `@AGENTS.md` per template). Tests `tests/Oadm.NewPlugin.Tests` (in Oadm.sln, so CI runs them; `ScaffoldTests`,
  `PluginSdkTests`: the templates, and `Oadm.Plugin.Versions.props` against `SdkInfo.Version` and `Oadm.Client.csproj`).
