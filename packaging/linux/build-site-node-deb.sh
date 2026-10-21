#!/usr/bin/env bash
# Builds the OADM Site Node Debian packages from the published service and app (called by "manage package site-node").
# Usage: build-site-node-deb.sh <linux-x64|linux-arm64> <version> <node-dir> <app-dir> <out-dir>
# Needs dpkg-deb only (Debian, Ubuntu or a container); no root, file owners are set with --root-owner-group.
#
# Two packages (CLAUDE.md "# Sites", "Packaging"):
#   oadm-site-node_<version>_<arch>.deb      /opt/oadm/site-node/oadm-site-node (+ license texts),
#                                            /usr/bin/oadm-site-node -> it (the command line),
#                                            /usr/lib/systemd/system/oadm-site-node.service, maintainer scripts
#                                            (system user oadm-site-node, data folder /var/lib/oadm-site-node 0700)
#   oadm-site-node-app_<version>_<arch>.deb  /opt/oadm/site-node-app/oadm-site-node-app (+ license texts),
#                                            /usr/bin/oadm-site-node-app -> it, oadm-site-node.desktop + icon;
#                                            Depends oadm-site-node (the app is only a window on the service)
# Each has /usr/share/doc/<package>/{copyright,TERMS.md,NOTICE.txt,THIRD-PARTY-NOTICES.txt}. No LGPL part, so no
# LGPL-2.1.txt.
set -euo pipefail

if [ $# -ne 5 ]; then
  echo "usage: $0 <linux-x64|linux-arm64> <version> <node-dir> <app-dir> <out-dir>" >&2
  exit 2
fi
RID="$1"; VERSION="$2"; NODE_DIR="$3"; APP_DIR="$4"; OUT_DIR="$5"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
FILES="$HERE/site-node"

case "$RID" in
  linux-x64) ARCH=amd64 ;;
  linux-arm64) ARCH=arm64 ;;
  *) echo "error: unsupported RID '$RID' (linux-x64 or linux-arm64)" >&2; exit 2 ;;
esac
# Debian version: a pre-release sorts before the release (1.2.0~rc.1 < 1.2.0).
DEB_VERSION="$(printf '%s' "${VERSION#v}" | sed 's/-/~/')"
for f in "$NODE_DIR/oadm-site-node" "$APP_DIR/oadm-site-node-app" "$NODE_DIR/THIRD-PARTY-NOTICES.txt" "$APP_DIR/THIRD-PARTY-NOTICES.txt"; do
  [ -f "$f" ] || { echo "error: $f not found (manage package site-node for $RID)" >&2; exit 1; }
done
command -v dpkg-deb >/dev/null 2>&1 || { echo "error: dpkg-deb not found" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT_DIR"

MAINTAINER="OADM contributors <oadm@acs-dev.org>"
# .NET runtime needs: glibc, libstdc++, zlib, OpenSSL (TLS to the server), ICU (globalization).
ICU="libicu80 | libicu79 | libicu78 | libicu77 | libicu76 | libicu75 | libicu74 | libicu73 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66"
RUNTIME_DEPENDS="libc6, libgcc-s1 | libgcc1, libstdc++6, zlib1g, libssl3t64 | libssl3 | libssl1.1, $ICU"
# The app (Avalonia on X11) needs fontconfig and the X11 libraries, like the OADM client.
APP_DEPENDS="oadm-site-node (= $DEB_VERSION), $RUNTIME_DEPENDS, libfontconfig1, libx11-6, libice6, libsm6, libxrandr2, libxi6, libxcursor1"

# /usr/share/doc/<package>: copyright (Debian format, Apache-2.0 text), the terms of use and the generated notices.
write_docs() {
  local root="$1" package="$2" from="$3" doc="$1/usr/share/doc/$2"
  mkdir -p "$doc"
  {
    echo "Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/"
    echo "Upstream-Name: OADM Site Node"
    echo "Source: Open AXIS Device Management"
    echo
    echo "Files: *"
    echo "Copyright: 2026 The OADM contributors"
    echo "License: Apache-2.0"
    echo " Terms of use (no warranty, use at your own risk, not affiliated with Axis):"
    echo " /usr/share/doc/$package/TERMS.md."
    echo " Bundled third-party components (.NET, Avalonia, ...) and every NuGet package"
    echo " with its license text: /usr/share/doc/$package/THIRD-PARTY-NOTICES.txt."
    echo " ."
    tr -d '\r' < "$REPO_ROOT/LICENSE" | sed -e 's/^$/./' -e 's/^/ /'
  } > "$doc/copyright"
  cp "$from/TERMS.md" "$from/NOTICE.txt" "$from/THIRD-PARTY-NOTICES.txt" "$doc/"
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

# --- oadm-site-node (the service and its command line)
ROOT="$WORK/oadm-site-node"
mkdir -p "$ROOT/DEBIAN" "$ROOT/opt/oadm" "$ROOT/usr/bin" "$ROOT/usr/lib/systemd/system"
cp -R "$NODE_DIR" "$ROOT/opt/oadm/site-node"
rm -f "$ROOT/opt/oadm/site-node/web.config" # IIS hosting file of the web SDK
ln -s /opt/oadm/site-node/oadm-site-node "$ROOT/usr/bin/oadm-site-node"
tr -d '\r' < "$FILES/oadm-site-node.service" > "$ROOT/usr/lib/systemd/system/oadm-site-node.service"
write_docs "$ROOT" oadm-site-node "$NODE_DIR"
for s in postinst prerm postrm; do
  tr -d '\r' < "$FILES/$s" > "$ROOT/DEBIAN/$s"
done
# passwd: useradd in postinst (essential on Debian and Ubuntu, named for minimal images).
build_package "$ROOT" oadm-site-node "$RUNTIME_DEPENDS, passwd" "Description: OADM Site Node
 Lets an OADM server reach the Axis devices of a network it cannot reach
 itself. This package is the service oadm-site-node.service (its own user,
 no root; data in /var/lib/oadm-site-node) and the command line
 oadm-site-node (status, pairing-code, server, networks, ports)." "" \
  opt/oadm/site-node/oadm-site-node DEBIAN/postinst DEBIAN/prerm DEBIAN/postrm

# --- oadm-site-node-app (the window)
ROOT="$WORK/oadm-site-node-app"
mkdir -p "$ROOT/DEBIAN" "$ROOT/opt/oadm" "$ROOT/usr/bin" "$ROOT/usr/share/applications" \
  "$ROOT/usr/share/icons/hicolor/256x256/apps" "$ROOT/usr/share/icons/hicolor/512x512/apps"
cp -R "$APP_DIR" "$ROOT/opt/oadm/site-node-app"
ln -s /opt/oadm/site-node-app/oadm-site-node-app "$ROOT/usr/bin/oadm-site-node-app"
tr -d '\r' < "$FILES/oadm-site-node.desktop" > "$ROOT/usr/share/applications/oadm-site-node.desktop"
# Its own icon name: oadm.png belongs to the oadm-client package.
cp "$REPO_ROOT/packaging/icons/oadm-256.png" "$ROOT/usr/share/icons/hicolor/256x256/apps/oadm-site-node.png"
cp "$REPO_ROOT/packaging/icons/oadm.png" "$ROOT/usr/share/icons/hicolor/512x512/apps/oadm-site-node.png"
write_docs "$ROOT" oadm-site-node-app "$APP_DIR"
# pkexec: changes and the pairing code run through an elevated copy of the app.
build_package "$ROOT" oadm-site-node-app "$APP_DEPENDS" "Description: OADM Site Node app
 The window of the OADM Site Node: the server, the pairing code, networks,
 ports and the connection log. Start it from the application menu or as
 oadm-site-node-app." "Recommends: pkexec | policykit-1" \
  opt/oadm/site-node-app/oadm-site-node-app
