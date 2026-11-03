#!/usr/bin/env bash
# Checks one add-on ZIP (docs/spec/add-ons.md "Release files"): check-addon-zip.sh <zip> <plugin id> <rid>
#   - the plugin folder's content at the top: plugin.json with the id, the server part (*.Server.dll) and, when the
#     add-on has a client project, the client part (*.Client.dll);
#   - native libraries of <rid>'s platform only: no runtimes/ folder of another platform, no .so on Windows and macOS,
#     no .dylib on Windows and Linux;
#   - the limits of the server: the ZIP at most 400 MB (its download limit), the client part it hands to clients
#     (plugin.json, the top-level *.Client.dll/.pdb and *.Server.dll/.pdb, everything under client/) at most 24 MB
#     unpacked and without native libraries;
#   - THIRD-PARTY-NOTICES.txt at the top (tools/Oadm.Notices --addon).
# Needs unzip (Linux, macOS, Git Bash on Windows).
set -euo pipefail

zip="$1"
id="$2"
rid="$3"
fail() { echo "error: $(basename "$zip"): $1" >&2; exit 1; }

[ -f "$zip" ] || fail "missing"
names="$(unzip -Z1 "$zip")"
size=$(wc -c < "$zip")
# unzip -l: length, date, time, name; the client part as the server picks it.
client=$(unzip -l "$zip" | awk 'NF >= 4 && $1 ~ /^[0-9]+$/ {
  name = $0; sub(/^ *[0-9]+ +[^ ]+ +[^ ]+ +/, "", name)
  if (name == "plugin.json" || name ~ /^[^\/]+\.(Client|Server)\.(dll|pdb)$/ || name ~ /^client\//) sum += $1
} END { print sum + 0 }')
echo "$(basename "$zip"): $(printf '%s\n' "$names" | grep -c .) files, $((size / 1024)) KB, client part $((client / 1024)) KB unpacked"
[ "$size" -le $((400 * 1024 * 1024)) ] || fail "larger than 400 MB, the server's download limit"
[ "$client" -le $((24 * 1024 * 1024)) ] || fail "client part larger than 24 MB"
bad_client="$(printf '%s\n' "$names" | grep -E '^client/.*(\.so$|\.so\.|\.dylib$)' || true)"
[ -z "$bad_client" ] || fail "native libraries in client/: $(echo $bad_client)"

printf '%s\n' "$names" | grep -qx 'plugin.json' || fail "no plugin.json at the top"
printf '%s\n' "$names" | grep -qx 'THIRD-PARTY-NOTICES.txt' || fail "no THIRD-PARTY-NOTICES.txt at the top"
unzip -p "$zip" plugin.json | grep -Eq "\"id\"[[:space:]]*:[[:space:]]*\"$id\"" || fail "plugin.json has another id than $id"
printf '%s\n' "$names" | grep -Eq '^[^/]+\.Server\.dll$' || fail "no server part (*.Server.dll)"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
for p in "$root"/plugins/*.Client/*.csproj; do
  [ -f "$p" ] || continue
  grep -Eq '<OadmAddOn>[[:space:]]*true[[:space:]]*</OadmAddOn>' "$p" || continue
  grep -q "<OadmPluginId>$id</OadmPluginId>" "$p" || continue
  printf '%s\n' "$names" | grep -Eq '^[^/]+\.Client\.dll$' || fail "no client part (*.Client.dll)"
done

os="${rid%%-*}"
keep=(-e "$rid" -e "$os")
if [ "$os" != win ]; then keep+=(-e unix); fi
other="$(printf '%s\n' "$names" | grep -E '^runtimes/[^/]+/' | cut -d/ -f2 | sort -u | grep -vx "${keep[@]}" || true)"
[ -z "$other" ] || fail "runtimes of another platform: $(echo $other)"
case "$os" in
  win) foreign='\.(so|dylib)$|\.so\.' ;;
  linux) foreign='\.dylib$' ;;
  *) foreign='\.so$|\.so\.' ;;
esac
bad="$(printf '%s\n' "$names" | grep -E "$foreign" || true)"
[ -z "$bad" ] || fail "native libraries of another platform: $(echo $bad)"
echo "$(basename "$zip"): ok"
