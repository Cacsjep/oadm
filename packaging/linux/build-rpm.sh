#!/usr/bin/env bash
# Builds the OADM RPM packages (Fedora, RHEL and its rebuilds) from the published server and client (called by "manage
# package linux" after the .deb packages). Same content and layout as build-deb.sh.
# Usage: build-rpm.sh <linux-x64|linux-arm64> <version> <server-dir> <client-dir> <out-dir>
# Needs rpmbuild only (package rpm on Debian and Ubuntu, rpm-build on Fedora); no root, the files belong to root in the
# package.
#
# Two packages (installer choice "Server and client" = both files in one dnf install, "Client only" = oadm-client):
#   oadm-server-<version>-1.<arch>.rpm  /opt/oadm/server/Oadm.Server (+ plugins/, license texts, oadm-rpm.txt),
#                                       /usr/lib/systemd/system/oadm-server.service, scriptlets rpm/oadm-server.*
#   oadm-client-<version>-1.<arch>.rpm  /opt/oadm/client/Oadm.Client (+ plugins/, license texts, oadm-rpm.txt),
#                                       /usr/bin/oadm-client -> /opt/oadm/client/Oadm.Client,
#                                       /usr/share/applications/oadm.desktop, /usr/share/icons/hicolor/<size>/apps/oadm.png
# Each has /usr/share/doc/<package>/{TERMS.md,LICENSE.txt,NOTICE.txt,THIRD-PARTY-NOTICES.txt,LGPL-2.1.txt}.
# oadm-rpm.txt tells self update that dnf installed the folder (docs/spec/self-update.md, Installation.RpmMarker).
# Data folder /var/lib/oadm (systemd StateDirectory, 0700), created by the server's %post too.
set -euo pipefail

if [ $# -ne 5 ]; then
  echo "usage: $0 <linux-x64|linux-arm64> <version> <server-dir> <client-dir> <out-dir>" >&2
  exit 2
fi
RID="$1"; VERSION="$2"; SERVER_DIR="$3"; CLIENT_DIR="$4"; OUT_DIR="$5"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
# shellcheck source=rpm/rpm-common.sh
. "$HERE/rpm/rpm-common.sh"
rpm_setup "$RID" "$VERSION"
for f in "$SERVER_DIR/Oadm.Server" "$CLIENT_DIR/Oadm.Client" "$SERVER_DIR/THIRD-PARTY-NOTICES.txt" "$CLIENT_DIR/THIRD-PARTY-NOTICES.txt"; do
  [ -f "$f" ] || { echo "error: $f not found (manage publish for $RID first)" >&2; exit 1; }
done

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
DOCS="TERMS.md LICENSE.txt NOTICE.txt THIRD-PARTY-NOTICES.txt LGPL-2.1.txt"
MARKER="Installed by the RPM package %s. Self update installs the next version with dnf."

# --- oadm-server
ROOT="$WORK/oadm-server"
mkdir -p "$ROOT/opt/oadm" "$ROOT/usr/lib/systemd/system"
cp -R "$SERVER_DIR" "$ROOT/opt/oadm/server"
rm -f "$ROOT/opt/oadm/server/web.config" # IIS hosting file of the web SDK
# shellcheck disable=SC2059
printf "$MARKER\n" oadm-server > "$ROOT/opt/oadm/server/oadm-rpm.txt"
tr -d '\r' < "$HERE/oadm-server.service" > "$ROOT/usr/lib/systemd/system/oadm-server.service"
# shellcheck disable=SC2086
rpm_docs "$ROOT" oadm-server "$SERVER_DIR" $DOCS
rpm_permissions "$ROOT" opt/oadm/server/Oadm.Server
build_rpm oadm-server "$ROOT" "Open AXIS Device Management server" \
  "Requires: $RPM_RUNTIME_REQUIRES" \
  "OADM manages Axis network devices: discovery, credentials, AXIS OS upgrades, users,
network settings and more. This package is the server: the systemd service
oadm-server.service (gRPC on TCP 5080, data in /var/lib/oadm)." \
  "%dir /opt/oadm
/opt/oadm/server
/usr/lib/systemd/system/oadm-server.service
/usr/share/doc/oadm-server" \
  "$HERE/rpm/oadm-server" "$OUT_DIR"

# --- oadm-client
ROOT="$WORK/oadm-client"
mkdir -p "$ROOT/opt/oadm" "$ROOT/usr/bin" "$ROOT/usr/share/applications"
cp -R "$CLIENT_DIR" "$ROOT/opt/oadm/client"
# shellcheck disable=SC2059
printf "$MARKER\n" oadm-client > "$ROOT/opt/oadm/client/oadm-rpm.txt"
ln -s /opt/oadm/client/Oadm.Client "$ROOT/usr/bin/oadm-client"
tr -d '\r' < "$HERE/oadm.desktop" > "$ROOT/usr/share/applications/oadm.desktop"
rpm_icons "$ROOT" oadm "$REPO_ROOT"
# shellcheck disable=SC2086
rpm_docs "$ROOT" oadm-client "$CLIENT_DIR" $DOCS
rpm_permissions "$ROOT" opt/oadm/client/Oadm.Client
build_rpm oadm-client "$ROOT" "Open AXIS Device Management client" \
  "Requires: $RPM_RUNTIME_REQUIRES, $RPM_DESKTOP_REQUIRES
Recommends: webkit2gtk4.1" \
  "OADM manages Axis network devices. This package is the desktop client; it
connects to an OADM server on this or another computer. Start it from the
application menu or as oadm-client." \
  "%dir /opt/oadm
/opt/oadm/client
/usr/bin/oadm-client
/usr/share/applications/oadm.desktop
/usr/share/icons/hicolor/*/apps/oadm.png
/usr/share/doc/oadm-client" \
  "$HERE/rpm/desktop" "$OUT_DIR"
