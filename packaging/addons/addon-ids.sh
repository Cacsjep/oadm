#!/usr/bin/env bash
# The plugin ids of the add-on plugin projects (<OadmAddOn>true</OadmAddOn>, docs/spec/add-ons.md), one per line, sorted:
# <OadmPluginId>, else the project folder name (like manage.sh). release.yml uses it to check that no installer and no
# portable ZIP holds an add-on folder and that every add-on has its ZIP.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
for p in "$root"/plugins/*/*.csproj; do
  [ -f "$p" ] || continue
  grep -Eq '<OadmAddOn>[[:space:]]*true[[:space:]]*</OadmAddOn>' "$p" || continue
  id="$(sed -n 's:.*<OadmPluginId>\([^<]*\)</OadmPluginId>.*:\1:p' "$p" | head -n 1 | tr -d '[:space:]')"
  echo "${id:-$(basename "$(dirname "$p")")}"
done | sort -u
