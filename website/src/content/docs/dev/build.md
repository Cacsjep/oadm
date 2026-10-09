---
title: Build from source
description: Build, run and test OADM from the repository.
---

You need the .NET 10 SDK (see `global.json`). The repository has one entry point per shell:
`./manage.sh` (bash) and `.\manage.ps1` (PowerShell 5.1 and 7), with the same verbs and options.

```sh
git clone https://github.com/Cacsjep/oadm.git
cd oadm
./manage.sh run dev             # build, start the server in the background, then the client
./manage.sh run client --fake   # client with sample data, no server
```

On the first `run dev`, confirm the fingerprint, then create the first administrator.

## Commands

| Command | What it does |
|---|---|
| `manage build [all\|server\|client\|plugins]` | Build the solution or a part of it |
| `manage run server` | Run the server in the foreground on `https://0.0.0.0:5080` |
| `manage run client [--fake] [--server URL]` | Run the client |
| `manage run dev` | Server in the background, then the client; closing the client stops the server |
| `manage test [unit\|timing\|perf\|hardware\|all]` | Run tests |
| `manage publish [all\|server\|client] [--rid RID]` | Self-contained apps in `artifacts/publish/` |
| `manage package <windows\|linux\|macos>` | Installer in `artifacts/packages/` |
| `manage clean` | Remove `bin`, `obj` and `artifacts` |

`manage help` lists everything; `manage <verb> help` shows the options of a verb.

## Tests

| Category | Command | Content |
|---|---|---|
| Unit | `manage test unit` | Fast, no network, no devices |
| `Timing` | `manage test timing` | Depend on real time; local only, not in CI |
| `Perf` | `manage test perf` | 5,000 devices, 50,000 tasks |
| `Hardware` | `manage test hardware` | Read-only, against your cameras in `dev-cameras.yaml` |
| `HardwareWrite` | never from `manage` | Change a device; only on purpose, only on a test device |

The UI is tested with Avalonia headless rendering. Set `OADM_SCREENSHOT_DIR` to keep the screenshots.

## Contributing

Read [CONTRIBUTING.md](https://github.com/Cacsjep/oadm/blob/main/CONTRIBUTING.md) and
[CLAUDE.md](https://github.com/Cacsjep/oadm/blob/main/CLAUDE.md), the spec and coding rules. Changes go through a pull
request; the Linux tests must pass.
