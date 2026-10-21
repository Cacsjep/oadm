# OADM installers

`manage package <windows|linux|macos> [--rid RID] [--version V]` runs `manage publish all` for the RID and builds the
installer into `artifacts/packages/`. The publish step writes self-contained single-file exes, every plugin project in
`plugins/<plugin id>/` next to each exe, and `TERMS.md`, `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` and `LGPL-2.1.txt`
next to each exe.

CI: one workflow, `.github/workflows/release.yml`, only on release tags `v*.*.*` (e.g. `v0.0.1`). Each job runs only
when the jobs before it passed:
1. `tests`: build and unit tests on Windows, Linux and macOS (30 min timeout, hang detection after 5 min).
2. `installers`: the x64 installers, plus the .deb packages for arm64 (built on a native ARM runner), and the portable
   ZIPs `OADM-Server-<version>-<rid>.zip` and `OADM-Client-<version>-<rid>.zip` (the publish folders). The .deb packages
   are installed, checked and removed on the runner, as "Server and client" and as "Client only"; the .pkg is checked
   without installing it.
3. `smoke-windows`: the MSI installed, checked and removed, as "Server and client" and as "Client only".
4. `nuget-pack`: the plugin SDK packages; every template is built and tested with them.
5. `release`: the GitHub release of the tag with the installers, the ZIPs and `SHA256SUMS.txt`. A tag with a
   pre-release part (`v1.2.0-rc.1`) makes a pre-release, never the latest release.
6. `nuget`: the plugin SDK packages on nuget.org (Trusted Publishing); needs the repository variable `NUGET_USER`.

Installers are not signed; check downloads against `SHA256SUMS.txt`.

Every installer offers **"Server and client"** (default) or **"Client only"**:
- MSI: page "Choose what to install" (features `Server` and `Client`; the client is always installed). Silent:
  `msiexec /i OADM-<version>-win-x64.msi /qn` installs both, `... ADDLOCAL=Client /qn` the client only. Change in Apps
  and Features switches between the two.
- Linux: two packages, `oadm-server` and `oadm-client`: `sudo apt install ./oadm-server_*.deb ./oadm-client_*.deb`
  (server and client), `sudo apt install ./oadm-server_*.deb` (server only) or `sudo apt install ./oadm-client_*.deb`
  (client only). Both replace the former single package `oadm` (before 0.0.3). The metapackage `oadm` of
  0.0.3..1.0.0 is gone; apt removes an installed one on the next upgrade.
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
| License texts | `TERMS.md`, `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt`, `LGPL-2.1.txt` in `Server` and `Client`; no license page | same in `/opt/oadm/server`, `/opt/oadm/client` and `/usr/share/doc/<package>/` (no acceptance in the package) | same in the server folder and `OADM.app/Contents/Resources`; no license page |
| Uninstall | Apps and Features / `msiexec /x`; data kept | `apt remove oadm-server oadm-client` (data kept, also on purge) | `sudo "/Library/Application Support/OADM/server/uninstall-oadm.sh"` (client only: `OADM.app/Contents/Resources/uninstall-oadm.sh`); data kept |

Notes:
- The server runs as root / LocalSystem because the NTP (UDP 123) and DHCP (UDP 67) plugins need privileged ports and
  interface binding.
- As an installed service (Windows service, systemd, or launchd with `OADM_SERVICE=launchd` from the plist) the server
  checks at start that the data folder, the extraction folder and every plugin folder are writable only by
  administrators / root (Windows ACL: owner SYSTEM / Administrators / TrustedInstaller, no write entry for anyone else;
  Unix: owner root, no group or world write). A folder that fails is reset (Windows: protected ACL SYSTEM +
  Administrators, plus Users read for plugin folders; Unix: `chown -R 0:0`, `chmod -R go-w`). If that fails too, the
  plugin folder is skipped with an error, and an insecure data folder stops the start. The service never uses the
  repository's `artifacts/plugins`. Console runs (`manage run`) and tests are not checked.
- License texts: `manage publish` runs `tools/Oadm.Notices`. It reads the published `*.deps.json` files (server,
  client, every plugin) and writes `THIRD-PARTY-NOTICES.txt`: the hand-written part (`THIRD-PARTY-NOTICES.md`: FFmpeg,
  Inter, .NET), every NuGet package with license, copyright, project and where it is used, the full license texts from
  `notices/licenses/` (MIT, Apache-2.0, BSD-2/3-Clause, OFL-1.1, LGPL-2.1) and the license and notice files the
  packages ship (FFmpeg `legal/`, runtime packs), deduplicated. A license without a text is a warning: add it as
  `notices/licenses/<SPDX id>.txt`. The client shows the file on the About page.
- Terms of use (`/TERMS.md`): accepted on the first start of the client on each computer (`Shell/TermsWindow`, stored
  with the version of the text in the client settings; changed terms ask again). The installers show no license page:
  silent and command-line installs skipped it and Debian packages never ask, so the client is the one place every user
  passes. `TERMS.md` also lies next to every published exe, in `/usr/share/doc/<package>/` and at the top of the About
  page.
- Service definitions always pass the data folder. Without it the server would use the service account's
  LocalApplicationData (`C:\Windows\System32\config\systemprofile\AppData\Local\Oadm`, `/root/.local/share/Oadm`).
- The single-file server extracts its native libraries (SQLite) at start. Services set `DOTNET_BUNDLE_EXTRACT_BASE_DIR`
  to a folder only the service account can write, never a shared temp folder.
- Linux overrides without editing the unit: `/etc/default/oadm-server` (`EnvironmentFile`), e.g.
  `Oadm__ListenUrl=https://0.0.0.0:5090`. The package also works without systemd (containers); postinst then prints
  how to start the server by hand.
- macOS packages are unsigned (executables ad hoc signed). Developer ID signing (`productsign`,
  `codesign --options runtime`) and notarization (`notarytool`) come later; until then open the .pkg with right-click >
  Open.
- Icons: `icons/make-icons.py` builds `oadm.ico` (also the client exe icon), `oadm.icns` and the PNGs from
  `/icon/oadm-app-icon-<size>.png`; the client uses `oadm-256.png` as window icon.
- Building the .deb on Windows: in Docker, e.g. `docker run` with `mcr.microsoft.com/dotnet/sdk:10.0` and
  `./manage.sh package linux --rid linux-x64` inside (the image has `dpkg-deb`).

## OADM Site Node

`manage package site-node [--rid RID] [--version V]` (docs/spec/packaging.md) publishes the node service
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
`smoke-windows` does the same with the node MSI (`windows/smoke.ps1 -Mode SiteNode`); `release` uploads them with the
others and lists them in `SHA256SUMS.txt`.
