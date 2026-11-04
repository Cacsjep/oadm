# OADM installers

`manage package <windows|linux|macos> [--rid RID] [--version V]` runs `manage publish all` for the RID and builds the
installer into `artifacts/packages/`. The publish step writes self-contained single-file exes, every plugin project in
`plugins/<plugin id>/` next to each exe, and `TERMS.md`, `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` and `LGPL-2.1.txt`
next to each exe. The client also gets the offline docs of Help as `docs/` when `artifacts/docs-offline/` exists
(`manage build docs-offline`, needs Node.js; in CI the job `docs`). Add-on plugins (`<OadmAddOn>true</OadmAddOn>`) are
never in an installer or portable ZIP (see "Add-on ZIPs").

CI: one workflow, `.github/workflows/release.yml`, only on release tags `v*.*.*` (e.g. `v0.0.1`). Each job runs only
when the jobs it needs passed (details in `docs/spec/packaging.md` "CI"):
1. `tests`: build and unit tests on Windows, Linux and macOS (30 min timeout, hang detection after 5 min).
2. `installers`: every installer (the .deb packages for arm64 on a native ARM runner), the OADM Site Node packages,
   the portable ZIPs `OADM-Server-<version>-<rid>.zip` and `OADM-Client-<version>-<rid>.zip` (the publish folders) and
   the add-on ZIPs of the platform. The .deb packages are installed, checked and removed on the runner, as "Server and
   client", as "Client only" and the site node; the .pkg is checked without installing it; no installer or portable ZIP
   may hold an add-on folder.
3. `smoke-windows` and `smoke-windows-node`: the MSI (both ways) and the site node MSI installed, checked and removed,
   each on a runner of its own.
4. `nuget-pack` (after `tests`): the plugin SDK packages; every template is built and tested with them.
5. `release`: the GitHub release of the tag with the installers, the ZIPs, the add-on ZIPs and `SHA256SUMS.txt`, the
   release text from `CHANGELOG.md`. A tag with a pre-release part (`v1.2.0-rc.1`) makes a pre-release, never the latest release.
6. `nuget`: the plugin SDK packages on nuget.org (Trusted Publishing); needs the repository variable `NUGET_USER`,
   skipped for a pre-release.

Installers are not signed; check downloads against `SHA256SUMS.txt`.

Every installer offers **"Server and client"** (default) or **"Client only"**:
- MSI: page "Choose what to install" (features `Server` and `Client`; the client is always installed). Silent:
  `msiexec /i OADM-<version>-win-x64.msi /qn` installs both, `... ADDLOCAL=Client /qn` the client only. Change in Apps
  and Features switches between the two.
- Linux: two packages, `oadm-server` and `oadm-client`: `sudo apt install ./oadm-server_*.deb ./oadm-client_*.deb`
  (server and client), `sudo apt install ./oadm-server_*.deb` (server only) or `sudo apt install ./oadm-client_*.deb`
  (client only). Both replace an installed package `oadm` (the former single package and metapackage); apt removes it on
  the upgrade.
- macOS: **Customize** lists "OADM client" (always) and "OADM server" (clear it for the client only). From the command
  line: `installer -applyChoiceChangesXML` with `com.oadm.choice.server` deselected.

Version: `--version` (tag `v1.2.0` gives `1.2.0`), default `0.1.0-dev`. It goes to every assembly (`-p:Version`) and
to the installers: MSI ProductVersion and the .pkg use the numeric part (`1.2.0`); the .deb writes a pre-release as
`1.2.0~rc.1` so it sorts before `1.2.0`.

| | Windows (MSI) | Linux (.deb) | macOS (.pkg) |
|---|---|---|---|
| File | `OADM-<version>-win-x64.msi` | `oadm-server_<version>_<amd64\|arm64>.deb`, `oadm-client_<version>_<amd64\|arm64>.deb` | `OADM-<version>-osx-x64.pkg` |
| Built with | WiX Toolset 6 (`WixToolset.Sdk` from NuGet, `windows/Oadm.Installer.wixproj`), on Windows | `dpkg-deb` (`linux/build-deb.sh`), on Linux or in a container | `pkgbuild` + `productbuild` (`macos/build-pkg.sh`), on macOS |
| Server | `C:\Program Files\OADM\Server` | `/opt/oadm/server` | `/Library/Application Support/OADM/server` |
| Client | `C:\Program Files\OADM\Client`, Start menu "OADM" | `/opt/oadm/client`, `/usr/bin/oadm-client`, `oadm.desktop` + icon | `/Applications/OADM.app` |
| Service | Windows service "OADM Server" (`OadmServer`), automatic, LocalSystem, restart on failure | systemd `oadm-server.service`, root, `Type=notify`, `Restart=on-failure` | LaunchDaemon `com.oadm.server`, root, KeepAlive on failure |
| Data folder | `%ProgramData%\OADM` (`--Oadm:DataDir`), SYSTEM + Administrators only | `/var/lib/oadm` (`OADM_DATA_DIR`, StateDirectory 0700) | `/Library/Application Support/OADM` (`OADM_DATA_DIR`, 0700) |
| Native lib extraction | `%ProgramData%\OADM\runtime` | `/var/cache/oadm` | `/Library/Application Support/OADM/runtime` |
| Firewall | TCP 5080 for `Oadm.Server.exe`, profiles Domain and Private (MSI); the NTP / DHCP plugins add UDP 123 / 67 (Domain, Private) while enabled and remove it when disabled or stopped | not touched | not touched (the application firewall asks) |
| Help (offline docs) | `Client\docs` | `/opt/oadm/client/docs` | `OADM.app/Contents/Resources/docs` |
| License texts | `TERMS.md`, `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt`, `LGPL-2.1.txt` in `Server` and `Client`; no license page | same in `/opt/oadm/server`, `/opt/oadm/client` and `/usr/share/doc/<package>/` (no acceptance in the package) | same in the server folder and `OADM.app/Contents/Resources`; no license page |
| Uninstall | Apps and Features / `msiexec /x`; data kept | `apt remove oadm-server oadm-client` (data kept, also on purge) | `sudo "/Library/Application Support/OADM/server/uninstall-oadm.sh"` (client only: `OADM.app/Contents/Resources/uninstall-oadm.sh`); data kept |

Notes:
- The server runs as root / LocalSystem because the NTP (UDP 123) and DHCP (UDP 67) plugins need privileged ports and
  interface binding.
- As an installed service (Windows service, systemd, or launchd with `OADM_SERVICE=launchd` from the plist) the server
  checks at start that the data folder, the extraction folder and every plugin folder are writable only by
  administrators / root and resets them if not (`docs/spec/security.md` "Service: firewall and folders"). The service
  never uses the repository's `artifacts/plugins`. Console runs (`manage run`) and tests are not checked.
- License texts and terms of use: `manage publish` runs `tools/Oadm.Notices` and writes `THIRD-PARTY-NOTICES.txt`
  (`docs/spec/packaging.md` "Notices and terms of use"). A license without a text is a warning: add it as
  `notices/licenses/<SPDX id>.txt`. The terms are accepted on the first start of the client, never in an installer.
- Service definitions always pass the data folder. Without it the server would use the service account's
  LocalApplicationData (`C:\Windows\System32\config\systemprofile\AppData\Local\Oadm`, `/root/.local/share/Oadm`).
- The single-file server extracts its native libraries (SQLite) at start. Services set `DOTNET_BUNDLE_EXTRACT_BASE_DIR`
  to a folder only the service account can write, never a shared temp folder.
- Linux overrides without editing the unit: `/etc/default/oadm-server` (`EnvironmentFile`), e.g.
  `Oadm__ListenUrl=https://0.0.0.0:5090`. The package also works without systemd (containers); postinst then prints
  how to start the server by hand.
- macOS packages are unsigned (executables ad hoc signed); Developer ID signing (`productsign`,
  `codesign --options runtime`) and notarization (`notarytool`) are not built yet. Open the .pkg with right-click > Open.
- Icons: `icons/make-icons.py` builds `oadm.ico` (also the client exe icon), `oadm.icns` and the PNGs from
  `/icon/oadm-app-icon-<size>.png`; the client uses `oadm-256.png` as window icon.
- Building the .deb on Windows: in Docker, e.g. `docker run` with `mcr.microsoft.com/dotnet/sdk:10.0` and
  `./manage.sh package linux --rid linux-x64` inside (the image has `dpkg-deb`).

## Add-on ZIPs

`manage package addons [--rid RID] [--version V]` (`docs/spec/packaging.md` "Add-on ZIPs", `docs/spec/add-ons.md`)
publishes every add-on plugin, server and client part together, for one platform and zips it as
`oadm-addon-<plugin id>-<version>-<rid>.zip` into `artifacts/packages/`. RIDs: `win-x64`, `linux-x64`, `linux-arm64`,
`osx-x64`; without `--rid` all four. Builds on any system.

- Content: the plugin folder at the top of the ZIP (`plugin.json`, `*.Server.dll`, `*.Client.dll`, their deps files,
  native libraries and models of that platform only). The server installs it into `<datafolder>/addons/<plugin id>/`;
  by hand, unpack it there.
- Build it before the server: its SHA-256 goes into the server's publish of that version and platform
  (`docs/spec/add-ons.md` rule 2); `artifacts/publish/addons/<rid>/addon-hashes-<version>.txt` carries it. A server published
  without that file cannot install optional plugins.
- Limits: the ZIP at most 1 GB; the client part (`plugin.json`, top-level `*.Client.dll`, `client/`) at most 24 MB,
  no native libraries in it.
- Staging folder: `artifacts/publish/addons/<rid>/<plugin id>/`.
- Checks: `addons/check-addon-zip.sh <zip> <plugin id> <rid>` (also in CI); `addons/check-server-hash.sh <publish folder>
  <zip> <plugin id>` (the server holds the ZIP's hash, in CI); `addons/addon-ids.sh` lists the add-on ids.

## Self update: manual test

Self update (`docs/spec/self-update.md`) runs these installers silently; the tests never run one. Before a release with
changes to self update or to an installer, test a real update on each system, once with "Server and client" and once
with "Client only", from the previous stable release to a release that is newer (a test repository or a local release
with the same file names and `SHA256SUMS.txt`):

1. Install the older version. Log in as an Administrator, start no task.
2. "Server and client": the new version dialog shows **Install now**; click it. The steps run (Download the update,
   Check the download, Install the update), the banner says "Updating the server to V<new>", the client on this computer
   closes and opens again with "Server updated to V<new>". Check: Settings > About shows the new version, the service
   runs, `update/state.json` says done, `update/oadm-<old>.db` exists, the Logs page's Audit tab shows "Started server
   update" and "Updated server", "Server and client" is still installed (MSI: Apps and Features > Change; .pkg: the
   LaunchDaemon `com.oadm.server` exists).
3. A failing installer: lock the package system first (Windows: another `msiexec` waiting; Linux: `sudo apt-get install`
   waiting at its prompt), then Install now: "The update failed. The server runs V<old>." with "Another installation is
   running. Try again later.", the old server keeps running, tasks start again.
4. "Client only" on another computer (Linux with `pkexec`): connect to the updated server; the dialog offers **Update
   client**. The system's prompt asks for the password, the installer runs, OADM opens again with "OADM updated to
   V<new>"; cancel the prompt once: "The update failed" with "The update was cancelled.", the old client keeps running.
   "Client only" stays client only (MSI: no service; macOS: no LaunchDaemon).
5. A ZIP (portable) server or client offers only Open download page.

| System | Server and client | Client only |
|---|---|---|
| Windows 10/11 x64 (MSI) | Install now, client closes and returns | Update client with the UAC prompt (`/passive`) |
| Debian / Ubuntu (amd64, arm64) | Install now; the client is its own package: Update client afterwards | Update client with `pkexec` |
| macOS (x64 .pkg) | Install now, client closes and returns | Update client with the administrator prompt; no server installed afterwards |

## OADM Site Node

`manage package site-node [--rid RID] [--version V]` (`docs/spec/packaging.md`) publishes the node service
(`src/Oadm.SiteNode`, `oadm-site-node`) and the app (`src/Oadm.SiteNode.App`, `oadm-site-node-app`) self-contained
single-file into `artifacts/publish/site-node/<rid>` and `artifacts/publish/site-node-app/<rid>`, writes the notices
(`tools/Oadm.Notices --product site-node`: hand-written part `notices/site-node.md`, no LGPL part, so no
`LGPL-2.1.txt`), then builds the packages into `artifacts/packages/`. RIDs: `win-x64` (on Windows), `linux-x64` and
`linux-arm64` (with `dpkg-deb`). No macOS package, no portable ZIP.

| | Windows (MSI) | Linux (.deb) |
|---|---|---|
| File | `OADM-SiteNode-<version>-win-x64.msi` | `oadm-site-node_<version>_<amd64\|arm64>.deb`, `oadm-site-node-app_<version>_<amd64\|arm64>.deb` |
| Built with | WiX Toolset 6 (`windows/site-node/Oadm.SiteNode.Installer.wixproj`) | `dpkg-deb` (`linux/build-site-node-deb.sh`, files in `linux/site-node/`) |
| Program | `C:\Program Files\OADM Site Node` (service and app), on the system PATH | `/opt/oadm/site-node` + `/usr/bin/oadm-site-node`; app `/opt/oadm/site-node-app` + `/usr/bin/oadm-site-node-app`, `oadm-site-node.desktop` + icon (package `oadm-site-node-app`, Depends `oadm-site-node`) |
| Service | "OADM Site Node" (`OadmSiteNode`), automatic, virtual account `NT SERVICE\OadmSiteNode`, restart on failure and after a stop with an error (exit code 1) | systemd `oadm-site-node.service`, user `oadm-site-node` (created by postinst), `Type=notify`, `AmbientCapabilities=CAP_NET_BIND_SERVICE` (relays below 1024), `RuntimeDirectory=oadm-site-node` (`/run/oadm-site-node/admin.sock`), `Restart=on-failure` |
| Data folder | `%ProgramData%\OADM Site Node`, SYSTEM + the service SID (`NT SERVICE\OadmSiteNode`) only | `/var/lib/oadm-site-node` (StateDirectory 0700, owned by the service user) |
| Native lib extraction | `%ProgramData%\OADM Site Node\runtime` | `/var/cache/oadm-site-node` |
| Firewall | none: the node only connects out to the server (TCP 5081 by default) and never listens | not touched |
| Server address | silent: `msiexec /i OADM-SiteNode-<version>-win-x64.msi SERVER=oadm.example.com /qn` (runs `oadm-site-node server <address> --timeout 60` after the service started; a refused address fails the install) | `sudo oadm-site-node server oadm.example.com` |
| License texts | `TERMS.md`, `LICENSE.txt`, `NOTICE.txt`, `THIRD-PARTY-NOTICES.txt` next to the exes | same in `/opt/oadm/site-node*` and `/usr/share/doc/<package>/` |
| Uninstall | Apps and Features / `msiexec /x`; data kept | `apt remove oadm-site-node-app oadm-site-node`; data and user kept, also on purge |

The server side: the OADM MSI opens only TCP 5080. The installed Windows service adds "OADM Server (Site nodes, TCP 5081)"
(Domain and Private) itself while Accept site nodes is on and removes it when it is off. The .deb and .pkg of the server
change no firewall.

CI (`release.yml`): job `installers` builds the node packages next to the others on Windows and on both Linux runners,
installs the .deb packages, checks the service (`oadm-site-node status` answers "Not bound") and removes them;
`smoke-windows-node` does the same with the node MSI (`windows/smoke.ps1 -Mode SiteNode`); `release` uploads them with
the others and lists them in `SHA256SUMS.txt`.
