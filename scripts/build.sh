#!/usr/bin/env bash
# Build the whole solution (server, client, plugins, tests).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/_common.sh"

"$DOTNET" build "$SOLUTION" -c "$CONFIGURATION" "$@"
