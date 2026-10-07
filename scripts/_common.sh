#!/usr/bin/env bash
# Shared helpers for OADM scripts. Sourced, not executed.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SERVER_PROJECT="$REPO_ROOT/src/Oadm.Server/Oadm.Server.csproj"
CLIENT_PROJECT="$REPO_ROOT/src/Oadm.Client/Oadm.Client.csproj"
SOLUTION="$REPO_ROOT/Oadm.sln"
CONFIGURATION="${CONFIGURATION:-Debug}"

# Find a dotnet that has a .NET 10 SDK: PATH first, then the usual per-user install folders.
find_dotnet() {
  local candidates=()
  command -v dotnet >/dev/null 2>&1 && candidates+=("$(command -v dotnet)")
  [ -n "${DOTNET_ROOT:-}" ] && candidates+=("$DOTNET_ROOT/dotnet")
  candidates+=("$HOME/.dotnet/dotnet")
  [ -n "${LOCALAPPDATA:-}" ] && candidates+=("$(cygpath -u "$LOCALAPPDATA" 2>/dev/null || echo "$LOCALAPPDATA")/Microsoft/dotnet/dotnet.exe")
  for c in "${candidates[@]}"; do
    if [ -x "$c" ] && "$c" --list-sdks 2>/dev/null | grep -q '^10\.'; then
      echo "$c"; return 0
    fi
  done
  echo "error: no .NET 10 SDK found. Install it from https://dot.net or with dotnet-install.sh --channel 10.0" >&2
  return 1
}

DOTNET="$(find_dotnet)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Runtime identifier for publish, overridable with RID=linux-arm64 etc.
default_rid() {
  local os arch
  case "$(uname -s)" in
    Linux*) os=linux ;;
    Darwin*) os=osx ;;
    MINGW*|MSYS*|CYGWIN*) os=win ;;
    *) os=linux ;;
  esac
  case "$(uname -m)" in
    arm64|aarch64) arch=arm64 ;;
    *) arch=x64 ;;
  esac
  echo "$os-$arch"
}
RID="${RID:-$(default_rid)}"
