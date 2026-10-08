#!/usr/bin/env bash
# Builds the OADM Debian packages from the published server and client (called by "manage package linux").
# Usage: build-deb.sh <linux-x64|linux-arm64> <version> <server-dir> <client-dir> <out-dir>
# Needs dpkg-deb only (Debian, Ubuntu or a container); no root, file owners are set with --root-owner-group.
#
# Three packages (installer choice "Server and client" = oadm, "Client only" = oadm-client):
#   oadm-server_<version>_<arch>.deb  /opt/oadm/server/Oadm.Server (+ plugins/, license texts),
#                                     /usr/lib/systemd/system/oadm-server.service, maintainer scripts
#   oadm-client_<version>_<arch>.deb  /opt/oadm/client/Oadm.Client (+ plugins/, license texts),
#                                     /usr/bin/oadm-client -> /opt/oadm/client/Oadm.Client,
#                                     /usr/share/applications/oadm.desktop, /usr/share/icons/hicolor/{256x256,512x512}/apps/oadm.png
#   oadm_<version>_<arch>.deb         metapackage: depends on oadm-server and oadm-client of the same version
# Each has /usr/share/doc/<package>/{copyright,THIRD-PARTY-NOTICES.txt,LGPL-2.1.txt}.
# Data folder /var/lib/oadm (systemd StateDirectory, 0700), created by the server's postinst too.
# The former single package "oadm" (before 0.0.3) is replaced: oadm-server and oadm-client take over its files.
set -euo pipefail

if [ $# -ne 5 ]; then
  echo "usage: $0 <linux-x64|linux-arm64> <version> <server-dir> <client-dir> <out-dir>" >&2
  exit 2
fi
RID="$1"; VERSION="$2"; SERVER_DIR="$3"; CLIENT_DIR="$4"; OUT_DIR="$5"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"

case "$RID" in
  linux-x64) ARCH=amd64 ;;
  linux-arm64) ARCH=arm64 ;;
  *) echo "error: unsupported RID '$RID' (linux-x64 or linux-arm64)" >&2; exit 2 ;;
esac
# Debian version: a pre-release sorts before the release (1.2.0~rc.1 < 1.2.0).
DEB_VERSION="$(printf '%s' "${VERSION#v}" | sed 's/-/~/')"
for f in "$SERVER_DIR/Oadm.Server" "$CLIENT_DIR/Oadm.Client" "$SERVER_DIR/THIRD-PARTY-NOTICES.txt" "$CLIENT_DIR/THIRD-PARTY-NOTICES.txt"; do
  [ -f "$f" ] || { echo "error: $f not found (manage publish for $RID first)" >&2; exit 1; }
done
command -v dpkg-deb >/dev/null 2>&1 || { echo "error: dpkg-deb not found" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT_DIR"

MAINTAINER="OADM contributors <oadm@acs-dev.org>"
# .NET runtime needs: glibc, libstdc++, zlib, OpenSSL (HTTPS to devices), ICU (globalization).
ICU="libicu80 | libicu79 | libicu78 | libicu77 | libicu76 | libicu75 | libicu74 | libicu73 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66"
RUNTIME_DEPENDS="libc6, libgcc-s1 | libgcc1, libstdc++6, zlib1g, libssl3t64 | libssl3 | libssl1.1, $ICU"
# The client (Avalonia on X11) needs fontconfig and the X11 libraries.
CLIENT_DEPENDS="$RUNTIME_DEPENDS, libfontconfig1, libx11-6, libice6, libsm6, libxrandr2, libxi6, libxcursor1"

# /usr/share/doc/<package>: copyright (Debian format, Apache-2.0 text), the generated notices and the LGPL text.
write_docs() {
  local root="$1" package="$2" from="$3" doc="$1/usr/share/doc/$2"
  mkdir -p "$doc"
  {
    echo "Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/"
    echo "Upstream-Name: OADM"
    echo "Source: Open AXIS Device Management"
    echo
    echo "Files: *"
    echo "Copyright: OADM contributors"
    echo "License: Apache-2.0"
    echo " Bundled third-party components (FFmpeg LGPL-2.1, .NET, Avalonia, ...) and every"
    echo " NuGet package with its license text: /usr/share/doc/$package/THIRD-PARTY-NOTICES.txt."
    echo " ."
    tr -d '\r' < "$REPO_ROOT/LICENSE" | sed -e 's/^$/./' -e 's/^/ /'
  } > "$doc/copyright"
  cp "$from/THIRD-PARTY-NOTICES.txt" "$from/LGPL-2.1.txt" "$doc/"
}

# Permissions (directories 0755, files 0644, the given executables 0755), Installed-Size, control file, build.
build_package() {
  local root="$1" package="$2" depends="$3" description="$4" extra="$5"
  shift 5
  find "$root" -type d -exec chmod 0755 {} +
  find "$root" -type f -exec chmod 0644 {} +
  local x
  for x in "$@"; do chmod 0755 "$root/$x"; done
  local size
  size="$(du -sk --exclude=DEBIAN "$root" | cut -f1)"
  {
    echo "Package: $package"
    echo "Version: $DEB_VERSION"
    echo "Architecture: $ARCH"
    echo "Section: net"
    echo "Priority: optional"
    echo "Maintainer: $MAINTAINER"
    echo "Installed-Size: $size"
    echo "Depends: $depends"
    if [ -n "$extra" ]; then printf '%s\n' "$extra"; fi
    printf '%s\n' "$description"
  } > "$root/DEBIAN/control"
  local deb="$OUT_DIR/${package}_${DEB_VERSION}_${ARCH}.deb"
  dpkg-deb -Zxz --root-owner-group --build "$root" "$deb"
  echo "built $deb"
}

# Files of the former all-in-one package "oadm" move to oadm-server / oadm-client.
TAKES_OVER="Replaces: oadm (<< 0.0.3~)
Breaks: oadm (<< 0.0.3~)"

# --- oadm-server
ROOT="$WORK/oadm-server"
mkdir -p "$ROOT/DEBIAN" "$ROOT/opt/oadm" "$ROOT/usr/lib/systemd/system"
cp -R "$SERVER_DIR" "$ROOT/opt/oadm/server"
rm -f "$ROOT/opt/oadm/server/web.config" # IIS hosting file of the web SDK
cp "$HERE/oadm-server.service" "$ROOT/usr/lib/systemd/system/oadm-server.service"
write_docs "$ROOT" oadm-server "$SERVER_DIR"
for s in postinst prerm postrm; do
  tr -d '\r' < "$HERE/$s" > "$ROOT/DEBIAN/$s"
done
build_package "$ROOT" oadm-server "$RUNTIME_DEPENDS" "Description: Open AXIS Device Management server
 OADM manages Axis network devices: discovery, credentials, firmware, users,
 network settings and more. This package is the server: the systemd service
 oadm-server.service (gRPC on TCP 5080, data in /var/lib/oadm)." "$TAKES_OVER" \
  opt/oadm/server/Oadm.Server DEBIAN/postinst DEBIAN/prerm DEBIAN/postrm

# --- oadm-client
ROOT="$WORK/oadm-client"
mkdir -p "$ROOT/DEBIAN" "$ROOT/opt/oadm" "$ROOT/usr/bin" "$ROOT/usr/share/applications" \
  "$ROOT/usr/share/icons/hicolor/256x256/apps" "$ROOT/usr/share/icons/hicolor/512x512/apps"
cp -R "$CLIENT_DIR" "$ROOT/opt/oadm/client"
ln -s /opt/oadm/client/Oadm.Client "$ROOT/usr/bin/oadm-client"
cp "$HERE/oadm.desktop" "$ROOT/usr/share/applications/oadm.desktop"
cp "$REPO_ROOT/packaging/icons/oadm-256.png" "$ROOT/usr/share/icons/hicolor/256x256/apps/oadm.png"
cp "$REPO_ROOT/packaging/icons/oadm.png" "$ROOT/usr/share/icons/hicolor/512x512/apps/oadm.png"
write_docs "$ROOT" oadm-client "$CLIENT_DIR"
build_package "$ROOT" oadm-client "$CLIENT_DEPENDS" "Description: Open AXIS Device Management client
 OADM manages Axis network devices. This package is the desktop client; it
 connects to an OADM server on this or another computer. Start it from the
 application menu or as oadm-client." "$TAKES_OVER" \
  opt/oadm/client/Oadm.Client

# --- oadm (metapackage: server and client)
ROOT="$WORK/oadm"
mkdir -p "$ROOT/DEBIAN"
write_docs "$ROOT" oadm "$SERVER_DIR"
build_package "$ROOT" oadm "oadm-server (= $DEB_VERSION), oadm-client (= $DEB_VERSION)" "Description: Open AXIS Device Management (server and client)
 OADM manages Axis network devices: discovery, credentials, firmware, users,
 network settings and more. This metapackage installs the server
 (oadm-server) and the desktop client (oadm-client); install oadm-client
 alone for a client that connects to a server elsewhere." ""
