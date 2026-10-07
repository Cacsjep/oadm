#!/usr/bin/env bash
# Build and run the client. Pass --fake to run without a server on sample data.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/_common.sh"

exec "$DOTNET" run --project "$CLIENT_PROJECT" -c "$CONFIGURATION" -- "$@"
