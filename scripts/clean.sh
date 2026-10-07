#!/usr/bin/env bash
# Remove build output (bin, obj, artifacts). Keeps the server data folder.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/_common.sh"

"$DOTNET" clean "$SOLUTION" >/dev/null || true
find "$REPO_ROOT/src" "$REPO_ROOT/plugins" "$REPO_ROOT/tests" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
rm -rf "$REPO_ROOT/artifacts"
echo "clean"
