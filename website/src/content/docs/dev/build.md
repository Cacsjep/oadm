---
title: Build from source
description: Build, run and test OADM from the repository.
---

You need the .NET 10 SDK (see `global.json`). The repository has one entry point per shell:
`./manage.sh` (bash: Linux, macOS, Git Bash) and `.\manage.ps1` (PowerShell 5.1 and 7), with the same verbs, options,
help texts and exit codes.

```sh
git clone https://github.com/Cacsjep/oadm.git
cd oadm
./manage.sh run dev             # build, start the server in the background, then the client
./manage.sh run client --fake   # client with sample data, no server
```

On the first `run dev`, trust the server, then create the first administrator (no setup code on the same computer).

## Commands

| Command | What it does |
|---|---|
| `manage build [all\|server\|client\|plugins\|docs] [--release]` | Build the solution, a part of it, or with `docs` the SDK reference of this website |
| `manage run server [--port N] [--data DIR]` | Run the server in the foreground on `https://0.0.0.0:5080` |
| `manage run client [--fake] [--server URL] [--data DIR]` | Run the client; `--fake` uses sample data without a server |
| `manage run dev [--port N] [--data DIR]` | Server in the background (log in `artifacts/logs/server-dev.log`), then the client; closing the client stops the server |
| `manage run node\|node-app` | The OADM Site Node on the console and its app, with their own data folder (`artifacts/dev-node`) |
| `manage test [unit\|perf\|hardware\|all] [--filter EXPR]` | Run tests, see [Tests](#tests) |
| `manage publish [all\|server\|client] [--rid RID] [--version V]` | Self-contained single-file apps in `artifacts/publish/<app>/<rid>`, plugins next to each exe |
| `manage package <windows\|linux\|macos> [--rid RID] [--version V]` | Installer (MSI, .deb, .pkg) in `artifacts/packages/` |
| `manage package site-node [--rid RID]` | The OADM Site Node installers (Windows MSI, Linux .deb) |
| `manage package nuget [--version V]` | The NuGet packages of the plugin SDK in `artifacts/nuget` (SDK, test helpers, project SDK, templates, the `oadm` command) |
| `manage new <task\|page\|action> --name NAME [--id ID]` | A new plugin from a tutorial, under your name, in `plugins/` and `tests/` and added to `Oadm.sln`; `--id` defaults to `community.<name>` |
| `manage license keys\|sign` | Key pair and license keys for a plugin you sell, see [Plugin licenses](../licenses/) |
| `manage clean` | Remove `bin`, `obj` and `artifacts` (data folders stay) |
| `manage info` | Show the .NET SDK, configuration and RID in use |

`manage help` lists everything; `manage <verb> help` shows the targets, options, defaults and examples of a verb.

- `--no-plugin-build` (run) uses the plugins already in `artifacts/plugins`; faster when no plugin changed.
- `CONFIGURATION=Release` and `RID=linux-x64` (or `osx-arm64`, `win-x64`, ...) set defaults that `--release` and
  `--rid` override.
- Arguments after `--` go unchanged to dotnet (build, test, publish) or to the app (run). PowerShell drops a bare `--`:
  write `'--'` there.
- Exit codes: `0` success, `1` environment error (no .NET 10 SDK, no `dev-cameras.yaml`, server did not start), `2`
  usage error, anything else comes from dotnet or the app.

```sh
./manage.sh run dev --port 5099 --data ./tmp-data
./manage.sh test unit --filter FullyQualifiedName~Polling -- --no-build
```

## Tests

| Category | Command | Content |
|---|---|---|
| Unit | `manage test unit` | Fast, no network, no devices |
| `Perf` | `manage test perf` | 30,000 devices, 50,000 tasks |
| `Hardware` | `manage test hardware` | Read-only, against your cameras in `dev-cameras.yaml` |
| `HardwareWrite` | never from `manage` | Change a device; only on purpose, only on a test device |

The UI is tested with Avalonia headless rendering. Set `OADM_SCREENSHOT_DIR` to keep the screenshots. More:
[Testing](../testing/).

## Contributing

Read [CONTRIBUTING.md](https://github.com/Cacsjep/oadm/blob/main/CONTRIBUTING.md) and
[CLAUDE.md](https://github.com/Cacsjep/oadm/blob/main/CLAUDE.md), the spec and coding rules. Changes go through a pull
request; the Linux tests must pass.
