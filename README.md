# OADM - Open AXIS Device Management

Open source, cross-platform alternative to AXIS Device Manager. Runs on Windows, Linux and macOS.
The full specification lives in [CLAUDE.md](CLAUDE.md).

## Requirements

- .NET 10 SDK (see `global.json`)

If .NET 10 is installed per user, for example with `dotnet-install` into
`%LOCALAPPDATA%\Microsoft\dotnet`, set `DOTNET_ROOT` to that folder. Without it,
`Oadm.Client.exe` and `Oadm.Server.exe` only look in `C:\Program Files\dotnet` and show a
"You must install .NET" dialog. `manage.sh` / `manage.ps1` set `DOTNET_ROOT` automatically,
and `manage publish` produces self-contained builds that need no runtime at all.

## Build and run

```sh
dotnet build Oadm.sln
dotnet run --project src/Oadm.Server   # gRPC server on http://0.0.0.0:5080
dotnet run --project src/Oadm.Client   # Avalonia desktop client
dotnet test Oadm.sln                   # unit tests, no network needed
```

The server has no authentication yet. Run it on a trusted LAN only.

## Developer commands (`manage`)

One entry point per shell at the repository root, with the same verbs, options, help texts
and exit codes:

```sh
./manage.sh <verb> [target] [options] [-- extra args]     # Linux, macOS, Git Bash
```

```powershell
.\manage.ps1 <verb> [target] [options] [-- extra args]    # Windows PowerShell 5.1, PowerShell 7
```

Every level explains itself: `manage` or `manage help` lists the verbs, `manage <verb> help`
(or `-h`, `--help`) shows targets, options, defaults and examples. Both shells print the help
texts from [`scripts/manage-help.txt`](scripts/manage-help.txt).

| Command | What it does |
|---|---|
| `manage build [all\|server\|client\|plugins] [--release]` | Build the solution (default), the server plus bundled plugins, the client, or only the plugins |
| `manage run server [--port N] [--data DIR] [--release]` | Build and run the server in the foreground (default `http://0.0.0.0:5080`) |
| `manage run client [--fake] [--server URL] [--data DIR] [--release]` | Build the client and all plugins, then run the client; `--fake` runs on sample data without a server |
| `manage run dev [--port N] [--data DIR] [--release]` | Server in the background (log in `artifacts/logs/server-dev.log`), then the client; closing the client stops the server |
| `manage test [unit\|perf\|hardware\|all] [--filter EXPR] [--release]` | Unit tests (default, fast, what CI runs), scale tests with 5,000 devices and 50,000 tasks (`Category=Perf`, a few minutes, see [`docs/scale-audit.md`](docs/scale-audit.md)), read-only hardware tests against `dev-cameras.yaml`, or all of them |
| `manage publish [all\|server\|client] [--rid RID]` | Self-contained single-file apps in `artifacts/publish/<app>/<rid>`, plugins next to the server exe |
| `manage clean` | Remove `bin`, `obj` and `artifacts` (data folders stay) |
| `manage info` | Show the .NET SDK, `DOTNET_ROOT`, configuration and RID in use |

The scripts find a .NET 10 SDK on `PATH`, in `DOTNET_ROOT`, in `~/.dotnet` or in
`%LOCALAPPDATA%\Microsoft\dotnet`, set `DOTNET_ROOT` for the launched apps and turn off
telemetry. `CONFIGURATION=Release` and `RID=linux-x64` (or `osx-arm64`, `win-x64`, ...) set the
defaults that `--release` and `--rid` override. Arguments after `--` go unchanged to dotnet
(build, test, publish) or to the app (run). PowerShell swallows a bare `--`, so write `'--'`
(quoted) there:

```sh
./manage.sh run dev --port 5099 --data ./tmp-data
./manage.sh test unit --filter FullyQualifiedName~Polling -- --no-build
```

```powershell
.\manage.ps1 publish client --rid win-x64
.\manage.ps1 build all '--' -v detailed
```

Exit codes: `0` success, `1` environment error (no .NET 10 SDK, no `dev-cameras.yaml`, server
did not start), `2` usage error, anything else is the exit code of dotnet or the app. If the
execution policy blocks scripts, run `powershell -ExecutionPolicy Bypass -File .\manage.ps1 ...`.

## Developer cameras (`dev-cameras.yaml`)

Integration tests and local tooling read the Axis devices available to you from
`dev-cameras.yaml` in the repository root. The file is **git-ignored** because it contains
passwords, so every developer keeps their own.

```sh
cp dev-cameras.example.yaml dev-cameras.yaml
```

```yaml
cameras:
  - address: 10.0.0.48      # IP or host name
    user: root
    password: secret
    scheme: https           # optional: https (default) or http
    note: "AXIS P3265-V"    # optional
```

Hardware tests are tagged `Category=Hardware` and skip themselves when the file is missing:

```sh
./manage.sh test hardware     # dotnet test Oadm.sln --filter Category=Hardware
./manage.sh test unit         # dotnet test Oadm.sln --filter "Category!=Hardware&Category!=Perf" (what CI runs)
./manage.sh test perf         # dotnet test Oadm.sln --filter Category=Perf (scale tests, docs/scale-audit.md)
```

## Layout

| Folder | Content |
|---|---|
| `src/Oadm.Contracts` | gRPC protobuf contracts |
| `src/Oadm.Sdk` | Plugin SDK (server side) |
| `src/Oadm.Sdk.Client` | Plugin SDK (Avalonia UI side) |
| `src/Oadm.Core` | Domain, VAPIX client, discovery, tasks, persistence |
| `src/Oadm.Server` | Server host |
| `src/Oadm.Client` | Avalonia client |
| `plugins/` | Bundled plugins, starting with Restart |
| `tests/` | xUnit tests |

## License

Apache-2.0, see [LICENSE](LICENSE).
