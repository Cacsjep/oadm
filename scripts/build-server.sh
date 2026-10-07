#!/usr/bin/env bash
# Build the server and the bundled plugins.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/_common.sh"

"$DOTNET" build "$SERVER_PROJECT" -c "$CONFIGURATION" "$@"
for p in "$REPO_ROOT"/plugins/*/*.csproj; do "$DOTNET" build "$p" -c "$CONFIGURATION"; done
