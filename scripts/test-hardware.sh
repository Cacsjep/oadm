#!/usr/bin/env bash
# Run hardware tests against the cameras in dev-cameras.yaml.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/_common.sh"

if [ ! -f "$REPO_ROOT/dev-cameras.yaml" ] && [ -z "${OADM_DEV_CAMERAS:-}" ]; then
  echo "dev-cameras.yaml not found, copy dev-cameras.example.yaml first" >&2; exit 1
fi
"$DOTNET" test "$SOLUTION" -c "$CONFIGURATION" --filter "Category=Hardware" "$@"
