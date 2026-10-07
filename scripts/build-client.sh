#!/usr/bin/env bash
# Build the Avalonia client.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/_common.sh"

"$DOTNET" build "$CLIENT_PROJECT" -c "$CONFIGURATION" "$@"
