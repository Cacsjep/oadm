#!/usr/bin/env bash
# Builds the OADM Site Node RPM packages (Fedora, RHEL and its rebuilds) from the published service and app (called by
# "manage package site-node" after the .deb packages). Same content and layout as build-site-node-deb.sh.
# Usage: build-site-node-rpm.sh <linux-x64|linux-arm64> <version> <node-dir> <app-dir> <out-dir>
# Needs rpmbuild only (package rpm on Debian and Ubuntu, rpm-build on Fedora).
#
# Two packages (docs/spec/packaging.md):
#   oadm-site-node-<version>-1.<arch>.rpm      /opt/oadm/site-node/oadm-site-node (+ license texts),
#                                              /usr/bin/oadm-site-node -> it (the command line),
#                                              /usr/lib/systemd/system/oadm-site-node.service, scriptlets
#                                              rpm/oadm-site-node.* (system user oadm-site-node, data folder
#                                              /var/lib/oadm-site-node 0700)
#   oadm-site-node-app-<version>-1.<arch>.rpm  /opt/oadm/site-node-app/oadm-site-node-app (+ license texts),
#                                              /usr/bin/oadm-site-node-app -> it, oadm-site-node.desktop + icon;
#                                              Requires oadm-site-node of the same version
# Each has /usr/share/doc/<package>/{TERMS.md,LICENSE.txt,NOTICE.txt,THIRD-PARTY-NOTICES.txt}. No LGPL part.
set -euo pipefail

if [ $# -ne 5 ]; then
  echo "usage: $0 <linux-x64|linux-arm64> <version> <node-dir> <app-dir> <out-dir>" >&2
  exit 2
fi
RID="$1"; VERSION="$2"; NODE_DIR="$3"; APP_DIR="$4"; OUT_DIR="$5"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
FILES="$HERE/site-node"
# shellcheck source=rpm/rpm-common.sh
. "$HERE/rpm/rpm-common.sh"
rpm_setup "$RID" "$VERSION"
for f in "$NODE_DIR/oadm-site-node" "$APP_DIR/oadm-site-node-app" "$NODE_DIR/THIRD-PARTY-NOTICES.txt" "$APP_DIR/THIRD-PARTY-NOTICES.txt"; do
  [ -f "$f" ] || { echo "error: $f not found (manage package site-node for $RID)" >&2; exit 1; }
done

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
DOCS="TERMS.md LICENSE.txt NOTICE.txt THIRD-PARTY-NOTICES.txt"

# --- oadm-site-node (the service and its command line)
ROOT="$WORK/oadm-site-node"
mkdir -p "$ROOT/opt/oadm" "$ROOT/usr/bin" "$ROOT/usr/lib/systemd/system"
cp -R "$NODE_DIR" "$ROOT/opt/oadm/site-node"
rm -f "$ROOT/opt/oadm/site-node/web.config" # IIS hosting file of the web SDK
ln -s /opt/oadm/site-node/oadm-site-node "$ROOT/usr/bin/oadm-site-node"
tr -d '\r' < "$FILES/oadm-site-node.service" > "$ROOT/usr/lib/systemd/system/oadm-site-node.service"
# shellcheck disable=SC2086
rpm_docs "$ROOT" oadm-site-node "$NODE_DIR" $DOCS
rpm_permissions "$ROOT" opt/oadm/site-node/oadm-site-node
# shadow-utils: useradd in pre.
build_rpm oadm-site-node "$ROOT" "OADM Site Node" \
  "Requires: $RPM_RUNTIME_REQUIRES
Requires(pre): shadow-utils" \
  "Lets an OADM server reach the Axis devices of a network it cannot reach
itself. This package is the service oadm-site-node.service (its own user,
no root; data in /var/lib/oadm-site-node) and the command line
oadm-site-node (status, pairing-code, server, networks, ports)." \
  "%dir /opt/oadm
/opt/oadm/site-node
/usr/bin/oadm-site-node
/usr/lib/systemd/system/oadm-site-node.service
/usr/share/doc/oadm-site-node" \
  "$HERE/rpm/oadm-site-node" "$OUT_DIR"

# --- oadm-site-node-app (the window)
ROOT="$WORK/oadm-site-node-app"
mkdir -p "$ROOT/opt/oadm" "$ROOT/usr/bin" "$ROOT/usr/share/applications"
cp -R "$APP_DIR" "$ROOT/opt/oadm/site-node-app"
ln -s /opt/oadm/site-node-app/oadm-site-node-app "$ROOT/usr/bin/oadm-site-node-app"
tr -d '\r' < "$FILES/oadm-site-node.desktop" > "$ROOT/usr/share/applications/oadm-site-node.desktop"
# Its own icon name: oadm.png belongs to the oadm-client package.
rpm_icons "$ROOT" oadm-site-node "$REPO_ROOT"
# shellcheck disable=SC2086
rpm_docs "$ROOT" oadm-site-node-app "$APP_DIR" $DOCS
rpm_permissions "$ROOT" opt/oadm/site-node-app/oadm-site-node-app
# polkit (pkexec): changes and the pairing code run through an elevated copy of the app.
build_rpm oadm-site-node-app "$ROOT" "OADM Site Node app" \
  "Requires: oadm-site-node = %{version}-%{release}, $RPM_RUNTIME_REQUIRES, $RPM_DESKTOP_REQUIRES
Recommends: polkit" \
  "The window of the OADM Site Node: the server, the pairing code, networks,
ports and the connection log. Start it from the application menu or as
oadm-site-node-app." \
  "%dir /opt/oadm
/opt/oadm/site-node-app
/usr/bin/oadm-site-node-app
/usr/share/applications/oadm-site-node.desktop
/usr/share/icons/hicolor/*/apps/oadm-site-node.png
/usr/share/doc/oadm-site-node-app" \
  "$HERE/rpm/desktop" "$OUT_DIR"
