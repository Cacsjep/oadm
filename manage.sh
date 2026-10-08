#!/usr/bin/env bash
# OADM developer commands for bash (Linux, macOS, Git Bash). Same verbs, options, help and exit
# codes as manage.ps1. Help texts live in scripts/manage-help.txt. Run "./manage.sh help".
# Works with bash 3.2 (macOS): no associative arrays, empty arrays expanded with ${a[@]+...}.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HELP_FILE="$REPO_ROOT/scripts/manage-help.txt"
SOLUTION="$REPO_ROOT/Oadm.sln"
SERVER_PROJECT="$REPO_ROOT/src/Oadm.Server/Oadm.Server.csproj"
CLIENT_PROJECT="$REPO_ROOT/src/Oadm.Client/Oadm.Client.csproj"
CONFIGURATION="${CONFIGURATION:-Debug}"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# ---------------------------------------------------------------- environment

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

# Find a dotnet with a .NET 10 SDK: PATH, DOTNET_ROOT, ~/.dotnet, %LOCALAPPDATA%\Microsoft\dotnet.
find_dotnet() {
  local candidates=() c
  command -v dotnet >/dev/null 2>&1 && candidates+=("$(command -v dotnet)")
  if [ -n "${DOTNET_ROOT:-}" ]; then candidates+=("$DOTNET_ROOT/dotnet" "$DOTNET_ROOT/dotnet.exe"); fi
  candidates+=("$HOME/.dotnet/dotnet")
  if [ -n "${LOCALAPPDATA:-}" ]; then
    candidates+=("$(cygpath -u "$LOCALAPPDATA" 2>/dev/null || echo "$LOCALAPPDATA")/Microsoft/dotnet/dotnet.exe")
  fi
  for c in "${candidates[@]}"; do
    if [ -f "$c" ] && [ -x "$c" ] && "$c" --list-sdks 2>/dev/null | grep -q '^10\.'; then
      echo "$c"; return 0
    fi
  done
  return 1
}

# Sets DOTNET and exports DOTNET_ROOT so app launchers find the same runtime. Exit 1 without SDK.
require_dotnet() {
  if ! DOTNET="$(find_dotnet)"; then
    echo "error: no .NET 10 SDK found. Install it from https://dot.net or with dotnet-install.sh --channel 10.0" >&2
    exit 1
  fi
  DOTNET_ROOT="$(dirname "$DOTNET")"
  export DOTNET_ROOT
}

# ---------------------------------------------------------------- help

# Prints one section of scripts/manage-help.txt with the placeholders filled in.
show_help() {
  awk -v section="$1" -v rid="$RID" -v cfg="$CONFIGURATION" '
    { sub(/\r$/, "") }
    /^## / { on = ($2 == section); next }
    on { gsub(/\{RID\}/, rid); gsub(/\{CONFIGURATION\}/, cfg); print }
  ' "$HELP_FILE"
}

# Usage error: message and the relevant help on stderr, exit code 2.
usage_error() {
  echo "error: $2" >&2
  echo >&2
  show_help "$1" >&2
  exit 2
}

is_verb() {
  case "$1" in build|run|test|publish|package|clean|info|help) return 0 ;; *) return 1 ;; esac
}

# ---------------------------------------------------------------- argument parsing

TARGET=""
OPT_RELEASE=0
OPT_FAKE=0
OPT_NO_PLUGIN_BUILD=0
OPT_PORT=""
OPT_DATA=""
OPT_SERVER=""
OPT_FILTER=""
OPT_RID=""
OPT_VERSION=""
EXTRA=()

# Options that take a value.
option_has_value() {
  case "$1" in --port|--data|--server|--filter|--rid|--version) return 0 ;; *) return 1 ;; esac
}

# Whether option $3 is valid for verb $1 and target $2.
option_allowed() {
  case "$1:$2:$3" in
    build:*:--release) return 0 ;;
    run:server:--port|run:server:--data|run:server:--release) return 0 ;;
    run:client:--fake|run:client:--server|run:client:--data|run:client:--release) return 0 ;;
    run:dev:--port|run:dev:--data|run:dev:--release) return 0 ;;
    run:*:--no-plugin-build) return 0 ;;
    test:*:--filter|test:*:--release) return 0 ;;
    publish:*:--rid|publish:*:--version) return 0 ;;
    package:*:--rid|package:*:--version) return 0 ;;
    *) return 1 ;;
  esac
}

parse_args() {
  local verb="$1"; shift
  local opts=() arg
  while [ $# -gt 0 ]; do
    arg="$1"; shift
    case "$arg" in
      help|-h|--help) show_help "$verb"; exit 0 ;;
      --) EXTRA=("$@"); break ;;
      -*)
        option_has_value "$arg" || [ "$arg" = --release ] || [ "$arg" = --fake ] || [ "$arg" = --no-plugin-build ] \
          || usage_error "$verb" "unknown option '$arg' (arguments for dotnet or the app go after --)"
        if option_has_value "$arg"; then
          [ $# -gt 0 ] && [ -n "$1" ] || usage_error "$verb" "option $arg needs a value"
          opts+=("$arg" "$1"); shift
        else
          opts+=("$arg")
        fi
        ;;
      *)
        [ -z "$TARGET" ] || usage_error "$verb" "unexpected argument '$arg'"
        TARGET="$arg"
        ;;
    esac
  done

  case "$verb" in
    build)   TARGET="${TARGET:-all}"; case "$TARGET" in all|server|client|plugins) ;; *) usage_error build "unknown target '$TARGET' for build" ;; esac ;;
    run)     [ -n "$TARGET" ] || usage_error run "missing target for run (server, client or dev)"
             case "$TARGET" in server|client|dev) ;; *) usage_error run "unknown target '$TARGET' for run" ;; esac ;;
    test)    TARGET="${TARGET:-unit}"; case "$TARGET" in unit|perf|hardware|all) ;; *) usage_error test "unknown target '$TARGET' for test" ;; esac ;;
    publish) TARGET="${TARGET:-all}"; case "$TARGET" in all|server|client) ;; *) usage_error publish "unknown target '$TARGET' for publish" ;; esac ;;
    package) [ -n "$TARGET" ] || usage_error package "missing target for package (windows, linux or macos)"
             case "$TARGET" in windows|linux|macos) ;; *) usage_error package "unknown target '$TARGET' for package" ;; esac ;;
    clean|info)
             [ -z "$TARGET" ] || usage_error "$verb" "$verb takes no target"
             [ ${#EXTRA[@]} -eq 0 ] || usage_error "$verb" "$verb takes no extra arguments" ;;
  esac

  local i=0 name value
  while [ $i -lt ${#opts[@]} ]; do
    name="${opts[$i]}"; value=""
    if option_has_value "$name"; then i=$((i + 1)); value="${opts[$i]}"; fi
    i=$((i + 1))
    if ! option_allowed "$verb" "$TARGET" "$name"; then
      if [ "$verb" = clean ] || [ "$verb" = info ]; then
        usage_error "$verb" "$verb takes no options"
      fi
      usage_error "$verb" "option $name is not valid for '$verb $TARGET'"
    fi
    case "$name" in
      --release) OPT_RELEASE=1 ;;
      --fake) OPT_FAKE=1 ;;
      --no-plugin-build) OPT_NO_PLUGIN_BUILD=1 ;;
      --port)
        case "$value" in ''|*[!0-9]*) usage_error "$verb" "--port needs a number from 1 to 65535" ;; esac
        [ "$value" -ge 1 ] && [ "$value" -le 65535 ] || usage_error "$verb" "--port needs a number from 1 to 65535"
        OPT_PORT="$value" ;;
      --data) OPT_DATA="$value" ;;
      --server) OPT_SERVER="$value" ;;
      --filter) OPT_FILTER="$value" ;;
      --rid) OPT_RID="$value" ;;
      --version)
        printf '%s\n' "$value" | grep -Eq '^v?[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.+-]+)?$' \
          || usage_error "$verb" "--version needs a version like 1.2.3 or 1.2.3-rc.1 (a leading v is removed)"
        OPT_VERSION="${value#v}" ;;
    esac
  done
  if [ $OPT_RELEASE -eq 1 ]; then CONFIGURATION=Release; fi
  return 0
}

# ---------------------------------------------------------------- helpers

plugin_projects() {
  local p
  for p in "$REPO_ROOT"/plugins/*/*.csproj; do [ -f "$p" ] && echo "$p"; done
  return 0
}

build_project() {
  "$DOTNET" build "$1" -c "$CONFIGURATION" ${EXTRA[@]+"${EXTRA[@]}"}
}

build_plugins() {
  local p
  while IFS= read -r p; do build_project "$p"; done < <(plugin_projects)
}

# Absolute path of a folder (created if missing).
abs_dir() {
  mkdir -p "$1"
  (cd "$1" && pwd)
}

# Built app dll: bin/<configuration>/<tfm>/<name>.dll.
app_dll() {
  local dll
  for dll in "$REPO_ROOT/src/$1/bin/$CONFIGURATION"/*/"$1.dll"; do
    [ -f "$dll" ] && { echo "$dll"; return 0; }
  done
  echo "error: $1.dll not found under src/$1/bin/$CONFIGURATION" >&2
  exit 1
}

# Arguments for the server from --port and --data.
SERVER_ARGS=()
set_server_args() {
  SERVER_ARGS=()
  if [ -n "$OPT_PORT" ]; then SERVER_ARGS+=("--Oadm:ListenUrl=https://0.0.0.0:$OPT_PORT"); fi
  if [ -n "$OPT_DATA" ]; then SERVER_ARGS+=("--Oadm:DataDir=$(abs_dir "$OPT_DATA")"); fi
  return 0
}

port_open() {
  (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null
}

# Same environment as the launch profile used by "dotnet run".
export_dev_environment() {
  export DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Development}"
  export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
}

# Version of published apps and packages: --version, else the development fallback.
DEFAULT_VERSION="0.1.0-dev"
app_version() {
  echo "${OPT_VERSION:-$DEFAULT_VERSION}"
}

publish_app() {
  rm -rf "$2"
  "$DOTNET" publish "$1" -c Release -r "$RID" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishReadyToRun=true -p:DebugType=embedded "-p:Version=$(app_version)" -o "$2" ${EXTRA[@]+"${EXTRA[@]}"}
  # Native symbol files of NuGet packages (SkiaSharp, HarfBuzzSharp); managed symbols are embedded.
  rm -f "$2"/*.pdb
}

# Plugin folder id of a plugin project (<OadmPluginId>, the folder name under artifacts/plugins), else the project name.
plugin_id() {
  local id
  id="$(sed -n 's:.*<OadmPluginId>\([^<]*\)</OadmPluginId>.*:\1:p' "$1" | head -n 1 | tr -d '[:space:]')"
  echo "${id:-$(basename "$(dirname "$1")")}"
}

# Publishes every plugin project (server and client part) into artifacts/publish/plugins/<id>/. Plugins are RID
# independent; both apps get the whole folder, each loader picks its own *.Server.dll or *.Client.dll.
PLUGIN_STAGE="$REPO_ROOT/artifacts/publish/plugins"
publish_plugins() {
  local p
  rm -rf "$PLUGIN_STAGE"
  mkdir -p "$PLUGIN_STAGE"
  while IFS= read -r p; do
    "$DOTNET" publish "$p" -c Release -p:OadmSkipPluginDeploy=true "-p:Version=$(app_version)" -o "$PLUGIN_STAGE/$(plugin_id "$p")"
  done < <(plugin_projects)
}

copy_plugins() {
  mkdir -p "$1/plugins"
  cp -R "$PLUGIN_STAGE"/. "$1/plugins/"
}

# ---------------------------------------------------------------- verbs

cmd_build() {
  require_dotnet
  case "$TARGET" in
    all) build_project "$SOLUTION" ;;
    server) build_project "$SERVER_PROJECT"; build_plugins ;;
    client) build_project "$CLIENT_PROJECT"; build_plugins ;;
    plugins) build_plugins ;;
  esac
}

# Builds the plugins, or with --no-plugin-build uses those already deployed in artifacts/plugins (warns when none are).
build_plugins_unless_skipped() {
  if [ "$OPT_NO_PLUGIN_BUILD" -eq 0 ]; then build_plugins; return; fi
  if [ -n "$(ls -A "$REPO_ROOT/artifacts/plugins" 2>/dev/null)" ]; then
    echo "skipping the plugin build (--no-plugin-build): using artifacts/plugins"
  else
    echo "warning: no plugins in artifacts/plugins yet: run once without --no-plugin-build" >&2
  fi
}

cmd_run() {
  require_dotnet
  export_dev_environment
  local app_args=(${EXTRA[@]+"${EXTRA[@]}"})
  EXTRA=()
  case "$TARGET" in
    server)
      build_project "$SERVER_PROJECT"; build_plugins_unless_skipped
      set_server_args
      exec "$DOTNET" exec "$(app_dll Oadm.Server)" ${SERVER_ARGS[@]+"${SERVER_ARGS[@]}"} ${app_args[@]+"${app_args[@]}"}
      ;;
    client)
      build_project "$CLIENT_PROJECT"; build_plugins_unless_skipped
      local client_args=()
      if [ $OPT_FAKE -eq 1 ]; then client_args+=(--fake); fi
      if [ -n "$OPT_SERVER" ]; then client_args+=(--server "$OPT_SERVER"); fi
      if [ -n "$OPT_DATA" ]; then client_args+=(--data "$(abs_dir "$OPT_DATA")"); fi
      exec "$DOTNET" exec "$(app_dll Oadm.Client)" ${client_args[@]+"${client_args[@]}"} ${app_args[@]+"${app_args[@]}"}
      ;;
    dev)
      if [ "$OPT_NO_PLUGIN_BUILD" -eq 1 ]; then
        build_project "$SERVER_PROJECT"; build_project "$CLIENT_PROJECT"; build_plugins_unless_skipped
      else
        build_project "$SOLUTION"
      fi
      set_server_args
      local log_dir="$REPO_ROOT/artifacts/logs" port="${OPT_PORT:-5080}" server_dll client_dll
      server_dll="$(app_dll Oadm.Server)"
      client_dll="$(app_dll Oadm.Client)"
      mkdir -p "$log_dir"
      "$DOTNET" exec "$server_dll" ${SERVER_ARGS[@]+"${SERVER_ARGS[@]}"} > "$log_dir/server-dev.log" 2>&1 &
      SERVER_PID=$!
      trap 'kill $SERVER_PID 2>/dev/null || true' EXIT
      echo "server started (pid $SERVER_PID), log: artifacts/logs/server-dev.log"
      local waited=0
      until port_open "$port"; do
        if ! kill -0 $SERVER_PID 2>/dev/null; then
          echo "error: server exited during startup, see artifacts/logs/server-dev.log" >&2
          tail -n 20 "$log_dir/server-dev.log" >&2 || true
          exit 1
        fi
        if [ $waited -ge 60 ]; then echo "warning: port $port not open after 30 s, starting the client anyway" >&2; break; fi
        sleep 0.5; waited=$((waited + 1))
      done
      local client_args=()
      if [ -n "$OPT_PORT" ]; then client_args+=(--server "localhost:$OPT_PORT"); fi
      local code=0
      "$DOTNET" exec "$client_dll" ${client_args[@]+"${client_args[@]}"} ${app_args[@]+"${app_args[@]}"} || code=$?
      exit $code
      ;;
  esac
}

cmd_test() {
  require_dotnet
  local filter
  case "$TARGET" in
    unit) filter="Category!=Hardware&Category!=Perf" ;;
    perf) filter="Category=Perf" ;;
    hardware)
      if [ ! -f "$REPO_ROOT/dev-cameras.yaml" ] && [ -z "${OADM_DEV_CAMERAS:-}" ]; then
        echo "error: dev-cameras.yaml not found and OADM_DEV_CAMERAS not set, copy dev-cameras.example.yaml first" >&2
        exit 1
      fi
      filter="Category=Hardware" ;;
    all) filter="Category!=HardwareWrite" ;;
  esac
  if [ -n "$OPT_FILTER" ]; then filter="($filter)&($OPT_FILTER)"; fi
  "$DOTNET" test "$SOLUTION" -c "$CONFIGURATION" --filter "$filter" ${EXTRA[@]+"${EXTRA[@]}"}
}

cmd_publish() {
  require_dotnet
  if [ -n "$OPT_RID" ]; then RID="$OPT_RID"; fi
  local out
  publish_plugins
  if [ "$TARGET" = all ] || [ "$TARGET" = server ]; then
    out="$REPO_ROOT/artifacts/publish/server/$RID"
    publish_app "$SERVER_PROJECT" "$out"
    copy_plugins "$out"
    echo "server published to $out"
  fi
  if [ "$TARGET" = all ] || [ "$TARGET" = client ]; then
    out="$REPO_ROOT/artifacts/publish/client/$RID"
    publish_app "$CLIENT_PROJECT" "$out"
    copy_plugins "$out"
    echo "client published to $out"
  fi
}

# Installer for one platform in artifacts/packages; publishes server and client for the RID first.
cmd_package() {
  local prefix arch out server client
  case "$TARGET" in windows) prefix=win ;; linux) prefix=linux ;; macos) prefix=osx ;; esac
  case "$(uname -m)" in arm64|aarch64) arch=arm64 ;; *) arch=x64 ;; esac
  RID="${OPT_RID:-$prefix-$arch}"
  case "$RID" in "$prefix-x64"|"$prefix-arm64") ;; *) usage_error package "--rid for package $TARGET is $prefix-x64 or $prefix-arm64" ;; esac
  case "$TARGET" in
    windows)
      case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) ;; *) echo "error: package windows needs Windows (WiX Toolset builds the MSI)" >&2; exit 1 ;; esac ;;
    linux)
      command -v dpkg-deb >/dev/null 2>&1 \
        || { echo "error: package linux needs dpkg-deb (Debian, Ubuntu or a container, see packaging/README.md)" >&2; exit 1; } ;;
    macos)
      command -v pkgbuild >/dev/null 2>&1 && command -v productbuild >/dev/null 2>&1 \
        || { echo "error: package macos needs macOS (pkgbuild, productbuild)" >&2; exit 1; } ;;
  esac

  OPT_RID="$RID"
  TARGET=all
  cmd_publish
  server="$REPO_ROOT/artifacts/publish/server/$RID"
  client="$REPO_ROOT/artifacts/publish/client/$RID"
  out="$(abs_dir "$REPO_ROOT/artifacts/packages")"
  case "$prefix" in
    win)
      # MSBuild gets Windows paths (Git Bash).
      "$DOTNET" build "$(cygpath -w "$REPO_ROOT/packaging/windows/Oadm.Installer.wixproj")" -c Release --no-incremental \
        "-p:OadmVersion=$(app_version)" "-p:OadmRid=$RID" "-p:OadmServerDir=$(cygpath -w "$server")" \
        "-p:OadmClientDir=$(cygpath -w "$client")" "-p:OadmPackageDir=$(cygpath -w "$out")" ;;
    linux) bash "$REPO_ROOT/packaging/linux/build-deb.sh" "$RID" "$(app_version)" "$server" "$client" "$out" ;;
    osx) bash "$REPO_ROOT/packaging/macos/build-pkg.sh" "$RID" "$(app_version)" "$server" "$client" "$out" ;;
  esac
  echo "package written to $out"
}

cmd_clean() {
  local d
  for d in src plugins tests tools; do
    [ -d "$REPO_ROOT/$d" ] || continue
    find "$REPO_ROOT/$d" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
  done
  rm -rf "$REPO_ROOT/artifacts"
  echo "clean"
}

cmd_info() {
  require_dotnet
  echo "repository     $REPO_ROOT"
  echo "dotnet         $DOTNET"
  echo "DOTNET_ROOT    $DOTNET_ROOT"
  echo "configuration  $CONFIGURATION"
  echo "rid            $RID"
  echo ".NET 10 SDKs"
  "$DOTNET" --list-sdks | grep '^10\.' | sed 's/^/  /'
}

# ---------------------------------------------------------------- main

if [ $# -eq 0 ]; then show_help overview; exit 0; fi
VERB="$1"; shift
case "$VERB" in
  help|-h|--help)
    if [ $# -eq 0 ]; then show_help overview; exit 0; fi
    case "$1" in -h|--help) show_help help; exit 0 ;; esac
    is_verb "$1" || usage_error overview "unknown verb '$1'"
    show_help "$1"
    exit 0 ;;
esac
is_verb "$VERB" || usage_error overview "unknown verb '$VERB'"
parse_args "$VERB" "$@"
"cmd_$VERB"
