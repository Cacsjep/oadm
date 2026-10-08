# OADM installers

`manage package <windows|linux|macos> [--rid RID] [--version V]` publishes server and client for the RID
(`manage publish all`: self-contained single-file exes, every plugin project in `plugins/<plugin id>/` next to
each exe, `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` and `LGPL-2.1.txt` next to each exe) and builds the installer into
`artifacts/packages/`.

CI: one workflow, `.github/workflows/release.yml`, only on release tags `v*.*.*` (e.g. `v0.0.1`): job `tests` (build and
unit tests on Windows, Linux, macOS, 30 min timeout, hang detection 5 min), then job `installers` (the three x64
installers; the MSI and the .deb packages are installed, checked and removed on the runner, as "Server and client" and
as "Client only"; the .pkg is checked without installing it), then job `release` (the GitHub release of the tag with
the installers and `SHA256SUMS.txt`). Each job runs only when the jobs before it passed. Installers are not signed
(user decision); check downloads against `SHA256SUMS.txt`.

Every installer offers **"Server and client"** (default) or **"Client only"**:
- MSI: page "Choose what to install" (features `Server` and `Client`; the client is always installed). Silent:
  `msiexec /i OADM-<version>-win-x64.msi /qn` installs both, `... ADDLOCAL=Client /qn` the client only. Change in Apps
  and Features switches between the two.
- Linux: three packages, `oadm-server`, `oadm-client` and the metapackage `oadm` (depends on both, same version):
  `sudo apt install ./oadm_*.deb ./oadm-server_*.deb ./oadm-client_*.deb` or `sudo apt install ./oadm-client_*.deb`.
  They replace the former single package `oadm` (before 0.0.3).
- macOS: "Customize" in the installer lists "OADM client" (always) and "OADM server" (uncheck for the client only);
  `installer -applyChoiceChangesXML` with `com.oadm.choice.server` deselected does the same from the command line.

Version: `--version` (a tag `v1.2.0` gives `1.2.0`), default `0.1.0-dev`. It goes to every assembly
(`-p:Version`) and to the installers: MSI ProductVersion and .pkg versions use the numeric part (`1.2.0`), the
.deb writes a pre-release as `1.2.0~rc.1` so it sorts before `1.2.0`.

| | Windows (MSI) | Linux (.deb) | macOS (.pkg) |
|---|---|---|---|
| File | `OADM-<version>-win-x64.msi` | `oadm-server_`, `oadm-client_`, `oadm_<version>_amd64.deb` | `OADM-<version>-osx-x64.pkg` |
| Built with | WiX Toolset 6 (`WixToolset.Sdk` from NuGet, `windows/Oadm.Installer.wixproj`), on Windows | `dpkg-deb` (`linux/build-deb.sh`), on Linux or in a container | `pkgbuild` + `productbuild` (`macos/build-pkg.sh`), on macOS |
| Server | `C:\Program Files\OADM\Server` | `/opt/oadm/server` | `/Library/Application Support/OADM/server` |
| Client | `C:\Program Files\OADM\Client`, Start menu "OADM" | `/opt/oadm/client`, `/usr/bin/oadm-client`, `oadm.desktop` + icon | `/Applications/OADM.app` |
| Service | Windows service "OADM Server" (`OadmServer`), automatic, LocalSystem, restart on failure | systemd `oadm-server.service`, root, `Type=notify`, `Restart=on-failure` | LaunchDaemon `com.oadm.server`, root, KeepAlive on failure |
| Data folder | `%ProgramData%\OADM` (`--Oadm:DataDir`), SYSTEM + Administrators only | `/var/lib/oadm` (`OADM_DATA_DIR`, StateDirectory 0700) | `/Library/Application Support/OADM` (`OADM_DATA_DIR`, 0700) |
| Native lib extraction | `%ProgramData%\OADM\runtime` | `/var/cache/oadm` | `/Library/Application Support/OADM/runtime` |
| Firewall | TCP 5080 for `Oadm.Server.exe`, profiles Domain and Private (MSI); the NTP / DHCP plugins add UDP 123 / 67 (Domain, Private) while enabled and remove it when disabled or stopped | not touched | not touched (application firewall asks) |
| License texts | `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt`, `LGPL-2.1.txt` in `Server` and `Client` | same in `/opt/oadm/server`, `/opt/oadm/client` and `/usr/share/doc/<package>/` | same in the server folder and `OADM.app/Contents/Resources` |
| Uninstall | Apps and Features / `msiexec /x`; data kept | `apt remove oadm oadm-server oadm-client` (data kept, also on purge) | `sudo "/Library/Application Support/OADM/server/uninstall-oadm.sh"` (client only: `OADM.app/Contents/Resources/uninstall-oadm.sh`); data kept |

Notes:
- The server runs as root / LocalSystem because the NTP (UDP 123) and DHCP (UDP 67) server plugins need
  privileged ports and interface binding.
- As the installed service (Windows service, systemd, launchd with `OADM_SERVICE=launchd` from the plist) the server
  checks at start that the data folder, the extraction folder and every plugin folder are writable only by
  administrators / root (Windows ACL: owner SYSTEM / Administrators / TrustedInstaller, no write entry for anyone else;
  Unix: owner root, no group or world write). A folder that is not is reset (Windows: protected ACL SYSTEM +
  Administrators, plus Users read for plugin folders; Unix: `chown -R 0:0`, `chmod -R go-w`), else a plugin folder is
  skipped with an error and an insecure data folder stops the start. The repository's `artifacts/plugins` is never used
  by the service. Console runs (`manage run`) and tests are not checked.
- License texts: `manage publish` runs `tools/Oadm.Notices`, which reads the published `*.deps.json` files (server,
  client, every plugin) and writes `THIRD-PARTY-NOTICES.txt`: the hand-written part (`THIRD-PARTY-NOTICES.md`: FFmpeg,
  Inter, .NET), every NuGet package with license, copyright, project and where it is used, the full license texts from
  `notices/licenses/` (MIT, Apache-2.0, BSD-2/3-Clause, OFL-1.1, LGPL-2.1) and the license and notice files shipped in
  the packages (FFmpeg `legal/`, runtime packs), deduplicated. A license without a text there is a warning: add the
  text to `notices/licenses/<SPDX id>.txt`. The client shows the file on Settings > About and licenses.
- Service definitions always pass the data folder explicitly; without it the server would use the service
  account's LocalApplicationData (`C:\Windows\System32\config\systemprofile\AppData\Local\Oadm`, `/root/.local/share/Oadm`).
- The single-file server extracts its native libraries (SQLite) at start. Services point
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR` at a folder only the service account can write, never at a shared temp folder.
- Linux overrides without editing the unit: `/etc/default/oadm-server` (`EnvironmentFile`), e.g.
  `Oadm__ListenUrl=http://0.0.0.0:5090`. The package works without systemd (containers): postinst then prints
  how to start the server by hand.
- macOS packages are unsigned (executables ad hoc signed). Signing with a Developer ID (`productsign`,
  `codesign --options runtime`) and notarization (`notarytool`) are a later step; until then open the .pkg with
  right click > Open.
- Icons: `icons/make-icons.py` builds `oadm.ico` (also the client exe icon), `oadm.icns` and the PNGs from the logo in
  `/icon` (`oadm-app-icon-<size>.png`); the client uses `oadm-256.png` as window icon.
- Building the .deb on Windows: in Docker, e.g. `docker run` with `mcr.microsoft.com/dotnet/sdk:10.0` and
  `./manage.sh package linux --rid linux-x64` inside (the image has `dpkg-deb`).
