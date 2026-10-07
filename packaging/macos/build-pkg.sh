#!/usr/bin/env bash
# Builds OADM-<version>-<rid>.pkg from the published server and client (called by "manage package macos").
# Usage: build-pkg.sh <osx-x64|osx-arm64> <version> <server-dir> <client-dir> <out-dir>
# Needs macOS (pkgbuild, productbuild, codesign, plutil). The package is unsigned; signing with a Developer ID
# and notarization are a later step (packaging/README.md).
#
# Components (installed in this order):
#   com.oadm.pkg.client   /Applications/OADM.app (Contents/MacOS/Oadm.Client + plugins/, Resources/OADM.icns)
#   com.oadm.pkg.launchd  /Library/LaunchDaemons/com.oadm.server.plist
#   com.oadm.pkg.server   /Library/Application Support/OADM/server (Oadm.Server + plugins/, uninstall-oadm.sh);
#                         preinstall stops, postinstall (re)loads the daemon
# Data folder: /Library/Application Support/OADM (root only, 0700).
set -euo pipefail

if [ $# -ne 5 ]; then
  echo "usage: $0 <osx-x64|osx-arm64> <version> <server-dir> <client-dir> <out-dir>" >&2
  exit 2
fi
RID="$1"; VERSION="${2#v}"; SERVER_DIR="$3"; CLIENT_DIR="$4"; OUT_DIR="$5"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"

case "$RID" in
  osx-x64) HOST_ARCHS="x86_64,arm64" ;;   # Rosetta runs x64 on Apple silicon
  osx-arm64) HOST_ARCHS="arm64" ;;
  *) echo "error: unsupported RID '$RID' (osx-x64 or osx-arm64)" >&2; exit 2 ;;
esac
# Bundle and package versions are numeric (1.2.0-rc.1 -> 1.2.0).
PKG_VERSION="$(printf '%s' "$VERSION" | sed -E 's/^([0-9]+\.[0-9]+\.[0-9]+).*/\1/')"
for f in "$SERVER_DIR/Oadm.Server" "$CLIENT_DIR/Oadm.Client"; do
  [ -f "$f" ] || { echo "error: $f not found (publish for $RID first)" >&2; exit 1; }
done
for tool in pkgbuild productbuild codesign plutil; do
  command -v "$tool" >/dev/null 2>&1 || { echo "error: $tool not found (macOS only)" >&2; exit 1; }
done

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Executables must carry a signature on Apple silicon; the SDK signs ad hoc when publishing on macOS,
# this covers builds where it did not.
ensure_signed() {
  if ! codesign --verify "$1" >/dev/null 2>&1; then
    codesign --force --sign - "$1"
  fi
}

# --- client: OADM.app
APP="$WORK/client/OADM.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$CLIENT_DIR"/. "$APP/Contents/MacOS/"
sed "s/@VERSION@/$PKG_VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist"
cp "$REPO_ROOT/packaging/icons/oadm.icns" "$APP/Contents/Resources/OADM.icns"
chmod 0755 "$APP/Contents/MacOS/Oadm.Client"
ensure_signed "$APP/Contents/MacOS/Oadm.Client"

# Not relocatable: always /Applications, even when another copy of the bundle id exists elsewhere.
pkgbuild --analyze --root "$WORK/client" "$WORK/client-components.plist"
plutil -replace 0.BundleIsRelocatable -bool NO "$WORK/client-components.plist"

# --- server
SERVER="$WORK/server"
mkdir -p "$SERVER"
cp -R "$SERVER_DIR"/. "$SERVER/"
rm -f "$SERVER/web.config"
cp "$HERE/uninstall-oadm.sh" "$SERVER/uninstall-oadm.sh"
chmod 0755 "$SERVER/Oadm.Server" "$SERVER/uninstall-oadm.sh"
ensure_signed "$SERVER/Oadm.Server"

SCRIPTS="$WORK/scripts"
mkdir -p "$SCRIPTS"
for s in preinstall postinstall; do
  tr -d '\r' < "$HERE/scripts/$s" > "$SCRIPTS/$s"
  chmod 0755 "$SCRIPTS/$s"
done

# --- LaunchDaemon
LAUNCHD="$WORK/launchd"
mkdir -p "$LAUNCHD"
cp "$HERE/com.oadm.server.plist" "$LAUNCHD/com.oadm.server.plist"
plutil -lint "$LAUNCHD/com.oadm.server.plist"
chmod 0644 "$LAUNCHD/com.oadm.server.plist"

# --- component packages
PKGS="$WORK/pkgs"
mkdir -p "$PKGS"
pkgbuild --root "$WORK/client" --component-plist "$WORK/client-components.plist" \
  --identifier com.oadm.pkg.client --version "$PKG_VERSION" --install-location /Applications \
  "$PKGS/oadm-client.pkg"
pkgbuild --root "$LAUNCHD" --identifier com.oadm.pkg.launchd --version "$PKG_VERSION" \
  --install-location /Library/LaunchDaemons "$PKGS/oadm-launchd.pkg"
pkgbuild --root "$SERVER" --scripts "$SCRIPTS" --identifier com.oadm.pkg.server --version "$PKG_VERSION" \
  --install-location "/Library/Application Support/OADM/server" "$PKGS/oadm-server.pkg"

# --- product archive
cat > "$WORK/distribution.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
    <title>OADM $VERSION</title>
    <options customize="never" require-scripts="true" rootVolumeOnly="true" hostArchitectures="$HOST_ARCHS"/>
    <domains enable_localSystem="true" enable_anywhere="false" enable_currentUserHome="false"/>
    <volume-check>
        <allowed-os-versions>
            <os-version min="13.0"/>
        </allowed-os-versions>
    </volume-check>
    <choices-outline>
        <line choice="default">
            <line choice="com.oadm.pkg.client"/>
            <line choice="com.oadm.pkg.launchd"/>
            <line choice="com.oadm.pkg.server"/>
        </line>
    </choices-outline>
    <choice id="default"/>
    <choice id="com.oadm.pkg.client" visible="false">
        <pkg-ref id="com.oadm.pkg.client"/>
    </choice>
    <choice id="com.oadm.pkg.launchd" visible="false">
        <pkg-ref id="com.oadm.pkg.launchd"/>
    </choice>
    <choice id="com.oadm.pkg.server" visible="false">
        <pkg-ref id="com.oadm.pkg.server"/>
    </choice>
    <pkg-ref id="com.oadm.pkg.client" version="$PKG_VERSION" onConclusion="none">oadm-client.pkg</pkg-ref>
    <pkg-ref id="com.oadm.pkg.launchd" version="$PKG_VERSION" onConclusion="none">oadm-launchd.pkg</pkg-ref>
    <pkg-ref id="com.oadm.pkg.server" version="$PKG_VERSION" onConclusion="none">oadm-server.pkg</pkg-ref>
</installer-gui-script>
EOF

mkdir -p "$OUT_DIR"
PKG="$OUT_DIR/OADM-$VERSION-$RID.pkg"
productbuild --distribution "$WORK/distribution.xml" --package-path "$PKGS" "$PKG"
echo "built $PKG"
