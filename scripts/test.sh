#!/usr/bin/env bash
# Run unit tests (no camera needed). This is what CI runs.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/_common.sh"

"$DOTNET" test "$SOLUTION" -c "$CONFIGURATION" --filter "Category!=Hardware" "$@"
