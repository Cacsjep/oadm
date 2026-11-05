#!/usr/bin/env bash
# Checks that a published server holds the SHA-256 of an add-on ZIP (docs/spec/add-ons.md rule 2):
#   check-server-hash.sh <publish folder> <zip> <plugin id>
# The hash is assembly metadata of Oadm.Server (AssemblyAddOnHashes): a custom attribute with key and value in plain text,
# so it is found in the single-file exe without running it (macOS and arm64 runners build what they cannot always run).
# In the attribute a one-byte length (64 = "@") sits between key and value. Needs grep, unzip, sha256sum or shasum.
set -euo pipefail

folder="$1"
zip="$2"
id="$3"
fail() { echo "error: server in $folder: $1" >&2; exit 1; }

exe="$folder/Oadm.Server"
[ -f "$exe" ] || exe="$folder/Oadm.Server.exe"
[ -f "$exe" ] || fail "no Oadm.Server"
[ -f "$zip" ] || fail "no $zip"
if command -v sha256sum >/dev/null 2>&1; then want="$(sha256sum "$zip" | cut -d' ' -f1)"; else want="$(shasum -a 256 "$zip" | cut -d' ' -f1)"; fi

key="OadmAddOnSha256:$id"
found="$(LC_ALL=C grep -aoE "$(printf '%s' "$key" | sed 's/\./\\./g')@[0-9a-f]{64}" "$exe" | sed 's/.*@//' | sort -u || true)"
[ -n "$found" ] || fail "no hash of $id built in"
[ "$found" = "$want" ] || fail "the hash of $id is $found, $(basename "$zip") has $want"

# The fingerprint: without it every older add-on would show Update available for ever. "none" (built outside git) is not built in.
content="$(unzip -p "$zip" oadm-content.txt | tr -d '\r\n')"
if [ "$content" != none ]; then
  key="OadmAddOnContent:$id"
  found="$(LC_ALL=C grep -aoE "$(printf '%s' "$key" | sed 's/\./\\./g')@[0-9a-f]{64}" "$exe" | sed 's/.*@//' | sort -u || true)"
  [ "$found" = "$content" ] || fail "the fingerprint of $id is '$found', $(basename "$zip") has '$content'"
fi
echo "$(basename "$exe"): hash of $id matches $(basename "$zip")"
