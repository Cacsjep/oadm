#!/bin/sh
# Removes OADM installed by the .pkg (macOS has no uninstaller for packages). Run with sudo:
#   sudo "/Library/Application Support/OADM/server/uninstall-oadm.sh"
# The data folder (database, master key, logs in /Library/Application Support/OADM) is kept.
set -e

if [ "$(id -u)" -ne 0 ]; then
    echo "run with sudo" >&2
    exit 1
fi

launchctl bootout system/com.oadm.server >/dev/null 2>&1 || true
rm -f /Library/LaunchDaemons/com.oadm.server.plist
rm -rf /Applications/OADM.app
rm -rf "/Library/Application Support/OADM/server" "/Library/Application Support/OADM/runtime"
for id in com.oadm.pkg.client com.oadm.pkg.launchd com.oadm.pkg.server; do
    pkgutil --forget "$id" >/dev/null 2>&1 || true
done
echo "OADM removed. Data kept in /Library/Application Support/OADM."
