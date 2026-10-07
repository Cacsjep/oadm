#!/usr/bin/env bash
# Start the server in the background, then the client. Stopping the client stops the server.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/_common.sh"

"$SCRIPT_DIR/build.sh"
mkdir -p "$REPO_ROOT/artifacts/logs"
"$DOTNET" run --no-build --project "$SERVER_PROJECT" -c "$CONFIGURATION" > "$REPO_ROOT/artifacts/logs/server-dev.log" 2>&1 &
SERVER_PID=$!
trap 'kill $SERVER_PID 2>/dev/null || true' EXIT
echo "server started (pid $SERVER_PID), log: artifacts/logs/server-dev.log"
sleep 3
"$DOTNET" run --no-build --project "$CLIENT_PROJECT" -c "$CONFIGURATION" -- "$@"
