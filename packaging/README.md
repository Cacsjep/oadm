# OADM installers

`manage package <windows|linux|macos> [--rid RID] [--version V]` publishes server and client for the RID
(`manage publish all`: self-contained single-file exes, every plugin project in `plugins/<plugin id>/` next to
each exe) and builds the installer into `artifacts/packages/`. CI: `.github/workflows/package.yml` (only release
tags `v*.*.*`, e.g. `v0.0.1`) builds all six packages and installs, checks and removes the
native one on each runner.

Version: `--version` (a tag `v1.2.0` gives `1.2.0`), default `0.1.0-dev`. It goes to every assembly
(`-p:Version`) and to the installers: MSI ProductVersion and .pkg versions use the numeric part (`1.2.0`), the
.deb writes a pre-release as `1.2.0~rc.1` so it sorts before `1.2.0`.

| | Windows (MSI) | Linux (.deb) | macOS (.pkg) |
|---|---|---|---|
| File | `OADM-<version>-win-x64.msi` | `oadm_<version>_amd64.deb` | `OADM-<version>-osx-x64.pkg` |
| Built with | WiX Toolset 6 (`WixToolset.Sdk` from NuGet, `windows/Oadm.Installer.wixproj`), on Windows | `dpkg-deb` (`linux/build-deb.sh`), on Linux or in a container | `pkgbuild` + `productbuild` (`macos/build-pkg.sh`), on macOS |
| Server | `C:\Program Files\OADM\Server` | `/opt/oadm/server` | `/Library/Application Support/OADM/server` |
| Client | `C:\Program Files\OADM\Client`, Start menu "OADM" | `/opt/oadm/client`, `/usr/bin/oadm-client`, `oadm.desktop` + icon | `/Applications/OADM.app` |
| Service | Windows service "OADM Server" (`OadmServer`), automatic, LocalSystem, restart on failure | systemd `oadm-server.service`, root, `Type=notify`, `Restart=on-failure` | LaunchDaemon `com.oadm.server`, root, KeepAlive on failure |
| Data folder | `%ProgramData%\OADM` (`--Oadm:DataDir`), SYSTEM + Administrators only | `/var/lib/oadm` (`OADM_DATA_DIR`, StateDirectory 0700) | `/Library/Application Support/OADM` (`OADM_DATA_DIR`, 0700) |
| Native lib extraction | `%ProgramData%\OADM\runtime` | `/var/cache/oadm` | `/Library/Application Support/OADM/runtime` |
| Firewall | TCP 5080, UDP 123, UDP 67 for `Oadm.Server.exe` | not touched | not touched (application firewall asks) |
| Uninstall | Apps and Features / `msiexec /x`; data kept | `apt remove oadm` (data kept, also on purge) | `sudo "/Library/Application Support/OADM/server/uninstall-oadm.sh"`; data kept |

Notes:
- The server runs as root / LocalSystem because the NTP (UDP 123) and DHCP (UDP 67) server plugins need
  privileged ports and interface binding.
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
