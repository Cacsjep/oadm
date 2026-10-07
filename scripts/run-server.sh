#!/usr/bin/env bash
# Build and run the server (gRPC on http://0.0.0.0:5080). Extra arguments go to the server.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/_common.sh"

"$SCRIPT_DIR/build-server.sh"
exec "$DOTNET" run --no-build --project "$SERVER_PROJECT" -c "$CONFIGURATION" -- "$@"
