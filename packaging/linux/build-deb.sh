#!/usr/bin/env bash
# Builds oadm_<version>_<arch>.deb from the published server and client (called by "manage package linux").
# Usage: build-deb.sh <linux-x64|linux-arm64> <version> <server-dir> <client-dir> <out-dir>
# Needs dpkg-deb only (Debian, Ubuntu or a container); no root, file owners are set with --root-owner-group.
#
# Layout:
#   /opt/oadm/server/Oadm.Server (+ plugins/)     /usr/lib/systemd/system/oadm-server.service
#   /opt/oadm/client/Oadm.Client (+ plugins/)     /usr/bin/oadm-client -> /opt/oadm/client/Oadm.Client
#   /usr/share/applications/oadm.desktop          /usr/share/icons/hicolor/{256x256,512x512}/apps/oadm.png
#   /usr/share/doc/oadm/{copyright,THIRD-PARTY-NOTICES.md}
# Data folder /var/lib/oadm (systemd StateDirectory, 0700), created by postinst too.
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
for f in "$SERVER_DIR/Oadm.Server" "$CLIENT_DIR/Oadm.Client"; do
  [ -f "$f" ] || { echo "error: $f not found (publish for $RID first)" >&2; exit 1; }
done
command -v dpkg-deb >/dev/null 2>&1 || { echo "error: dpkg-deb not found" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
ROOT="$WORK/oadm"

mkdir -p "$ROOT/DEBIAN" "$ROOT/opt/oadm" "$ROOT/usr/bin" "$ROOT/usr/lib/systemd/system" \
  "$ROOT/usr/share/applications" "$ROOT/usr/share/icons/hicolor/256x256/apps" \
  "$ROOT/usr/share/icons/hicolor/512x512/apps" "$ROOT/usr/share/doc/oadm"

cp -R "$SERVER_DIR" "$ROOT/opt/oadm/server"
cp -R "$CLIENT_DIR" "$ROOT/opt/oadm/client"
# Not used on Linux (IIS hosting file of the web SDK).
rm -f "$ROOT/opt/oadm/server/web.config"
ln -s /opt/oadm/client/Oadm.Client "$ROOT/usr/bin/oadm-client"

cp "$HERE/oadm-server.service" "$ROOT/usr/lib/systemd/system/oadm-server.service"
cp "$HERE/oadm.desktop" "$ROOT/usr/share/applications/oadm.desktop"
cp "$REPO_ROOT/packaging/icons/oadm-256.png" "$ROOT/usr/share/icons/hicolor/256x256/apps/oadm.png"
cp "$REPO_ROOT/packaging/icons/oadm.png" "$ROOT/usr/share/icons/hicolor/512x512/apps/oadm.png"
{
  echo "Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/"
  echo "Upstream-Name: OADM"
  echo "Source: Open AXIS Device Management"
  echo
  echo "Files: *"
  echo "Copyright: OADM contributors"
  echo "License: Apache-2.0"
  echo " Bundled third-party components (FFmpeg LGPL-2.1, .NET, Avalonia, ...): see"
  echo " /usr/share/doc/oadm/THIRD-PARTY-NOTICES.md."
  echo " ."
  tr -d '\r' < "$REPO_ROOT/LICENSE" | sed -e 's/^$/./' -e 's/^/ /'
} > "$ROOT/usr/share/doc/oadm/copyright"
cp "$REPO_ROOT/THIRD-PARTY-NOTICES.md" "$ROOT/usr/share/doc/oadm/THIRD-PARTY-NOTICES.md"

for s in postinst prerm postrm; do
  tr -d '\r' < "$HERE/$s" > "$ROOT/DEBIAN/$s"
done

# Permissions: directories 0755, files 0644, executables and maintainer scripts 0755.
find "$ROOT" -type d -exec chmod 0755 {} +
find "$ROOT" -type f -exec chmod 0644 {} +
chmod 0755 "$ROOT/opt/oadm/server/Oadm.Server" "$ROOT/opt/oadm/client/Oadm.Client" \
  "$ROOT/DEBIAN/postinst" "$ROOT/DEBIAN/prerm" "$ROOT/DEBIAN/postrm"

INSTALLED_SIZE="$(du -sk --exclude=DEBIAN "$ROOT" | cut -f1)"
# .NET runtime needs: glibc, libstdc++, zlib, OpenSSL (HTTPS to devices), ICU (globalization).
# The client (Avalonia on X11) needs fontconfig and the X11 libraries: recommended, so a headless server
# install can skip them with --no-install-recommends.
ICU="libicu80 | libicu79 | libicu78 | libicu77 | libicu76 | libicu75 | libicu74 | libicu73 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66"
cat > "$ROOT/DEBIAN/control" <<EOF
Package: oadm
Version: $DEB_VERSION
Architecture: $ARCH
Section: net
Priority: optional
Maintainer: OADM contributors <noreply@oadm.invalid>
Installed-Size: $INSTALLED_SIZE
Depends: libc6, libgcc-s1 | libgcc1, libstdc++6, zlib1g, libssl3t64 | libssl3 | libssl1.1, $ICU
Recommends: libfontconfig1, libx11-6, libice6, libsm6, libxrandr2, libxi6, libxcursor1
Description: Open AXIS Device Management (server and client)
 OADM manages Axis network devices: discovery, credentials, firmware, users,
 network settings and more, with a gRPC server and a desktop client.
 .
 The server runs as the systemd service oadm-server.service (TCP 5080, data in
 /var/lib/oadm). The client starts from the application menu or as oadm-client.
EOF

mkdir -p "$OUT_DIR"
DEB="$OUT_DIR/oadm_${DEB_VERSION}_${ARCH}.deb"
dpkg-deb -Zxz --root-owner-group --build "$ROOT" "$DEB"
echo "built $DEB"
