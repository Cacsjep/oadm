#!/usr/bin/env bash
# Publish a self-contained client to artifacts/publish/client/<rid>. Set RID=osx-arm64 etc. to cross-publish.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/_common.sh"

OUT="$REPO_ROOT/artifacts/publish/client/$RID"
"$DOTNET" publish "$CLIENT_PROJECT" -c Release -r "$RID" --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:DebugType=embedded -o "$OUT" "$@"
echo "client published to $OUT"
