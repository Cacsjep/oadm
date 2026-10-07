#!/usr/bin/env bash
# Publish a self-contained server to artifacts/publish/server/<rid>. Set RID=linux-x64 etc. to cross-publish.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/_common.sh"

OUT="$REPO_ROOT/artifacts/publish/server/$RID"
"$DOTNET" publish "$SERVER_PROJECT" -c Release -r "$RID" --self-contained -o "$OUT" "$@"
mkdir -p "$OUT/plugins"
for p in "$REPO_ROOT"/plugins/*/*.csproj; do
  name="$(basename "$(dirname "$p")")"
  "$DOTNET" publish "$p" -c Release -o "$OUT/plugins/$name"
done
echo "server published to $OUT"
