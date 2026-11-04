#!/usr/bin/env bash
# Checks one add-on ZIP (docs/spec/add-ons.md "Release files"): check-addon-zip.sh <zip> <plugin id> <rid>
#   - the plugin folder's content at the top: plugin.json with the id, the server part (*.Server.dll) and, when the
#     add-on has a client project, the client part (*.Client.dll);
#   - native libraries of <rid>'s platform only: no runtimes/ folder of another platform, no .so on Windows and macOS,
#     no .dylib on Windows and Linux;
#   - the limits of the server: the ZIP at most 1024 MB (the most it takes from a client), the client part it hands to clients
#     (plugin.json, the top-level *.Client.dll/.pdb and *.Server.dll/.pdb, everything under client/) at most 24 MB
#     unpacked and without native libraries;
#   - THIRD-PARTY-NOTICES.txt at the top (tools/Oadm.Notices --addon); a win-* ZIP with DirectML.dll only when the notices
#     hold its license (MICROSOFT SOFTWARE LICENSE TERMS ... DIRECTML, from the add-on's OadmNoticePackage items);
#   - oadm-version.txt at the top with the version of the ZIP's name (the server records the add-on with it).
# The limits are kept equal to the server's constants by a test (AddOnPackagingTests): AddOnFolders.MaxZipBytes
# and AddOnFolders.MaxClientPartBytes.
# Needs unzip (Linux, macOS, Git Bash on Windows).
# grep -q reads here-strings: with pipefail "printf | grep -q" fails on a long text (grep stops reading, printf gets SIGPIPE).
set -euo pipefail

zip="$1"
id="$2"
rid="$3"
fail() { echo "error: $(basename "$zip"): $1" >&2; exit 1; }
max_zip_mb=1024
max_client_mb=24

[ -f "$zip" ] || fail "missing"
names="$(unzip -Z1 "$zip")"
size=$(wc -c < "$zip")
# unzip -l: length, date, time, name; the client part as the server picks it.
client=$(unzip -l "$zip" | awk 'NF >= 4 && $1 ~ /^[0-9]+$/ {
  name = $0; sub(/^ *[0-9]+ +[^ ]+ +[^ ]+ +/, "", name)
  if (name == "plugin.json" || name ~ /^[^\/]+\.(Client|Server)\.(dll|pdb)$/ || name ~ /^client\//) sum += $1
} END { print sum + 0 }')
echo "$(basename "$zip"): $(printf '%s\n' "$names" | grep -c .) files, $((size / 1024)) KB, client part $((client / 1024)) KB unpacked"
[ "$size" -le $((max_zip_mb * 1024 * 1024)) ] || fail "larger than $max_zip_mb MB, the most the server takes"
[ "$client" -le $((max_client_mb * 1024 * 1024)) ] || fail "client part larger than $max_client_mb MB"
bad_client="$(printf '%s\n' "$names" | grep -E '^client/.*(\.so$|\.so\.|\.dylib$)' || true)"
[ -z "$bad_client" ] || fail "native libraries in client/: $(echo $bad_client)"

grep -qx 'plugin.json' <<< "$names" || fail "no plugin.json at the top"
grep -qx 'THIRD-PARTY-NOTICES.txt' <<< "$names" || fail "no THIRD-PARTY-NOTICES.txt at the top"
unzip -p "$zip" plugin.json | grep -Eq "\"id\"[[:space:]]*:[[:space:]]*\"$id\"" || fail "plugin.json has another id than $id"
grep -Eq '^[^/]+\.Server\.dll$' <<< "$names" || fail "no server part (*.Server.dll)"
grep -qx 'oadm-version.txt' <<< "$names" || fail "no oadm-version.txt at the top"
expected="$(basename "$zip")"
expected="${expected#oadm-addon-$id-}"
expected="${expected%-$rid.zip}"
stamped="$(unzip -p "$zip" oadm-version.txt | tr -d '\r\n')"
[ "$stamped" = "$expected" ] || fail "oadm-version.txt says '$stamped', the ZIP's name '$expected'"
! grep -qx 'notices-extra.json' <<< "$names" || fail "notices-extra.json must not ship"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
for p in "$root"/plugins/*.Client/*.csproj; do
  [ -f "$p" ] || continue
  grep -Eq '<OadmAddOn>[[:space:]]*true[[:space:]]*</OadmAddOn>' "$p" || continue
  grep -q "<OadmPluginId>$id</OadmPluginId>" "$p" || continue
  grep -Eq '^[^/]+\.Client\.dll$' <<< "$names" || fail "no client part (*.Client.dll)"
done

os="${rid%%-*}"
if [ "$os" = win ] && grep -Eqi '(^|/)DirectML\.dll$' <<< "$names"; then
  notices="$(unzip -p "$zip" THIRD-PARTY-NOTICES.txt)"
  grep -q 'MICROSOFT SOFTWARE LICENSE TERMS' <<< "$notices" && grep -q 'DIRECTML' <<< "$notices" \
    || fail "DirectML.dll without its license in THIRD-PARTY-NOTICES.txt (OadmNoticePackage Microsoft.AI.DirectML)"
fi
leftover="$(printf '%s\n' "$names" | grep -Ei '(^|/)(DirectML\.Debug\.dll|DirectML\.Debug\.pdb|DirectML\.pdb|onnxruntime\.lib)$' || true)"
[ -z "$leftover" ] || fail "build leftovers: $(echo $leftover)"
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
