# OADM - Open AXIS Device Management

Open source, cross-platform alternative to AXIS Device Manager. Runs on Windows, Linux and macOS.
The full specification lives in [CLAUDE.md](CLAUDE.md).

## Requirements

- .NET 10 SDK (see `global.json`)

If .NET 10 is installed per user, for example with `dotnet-install` into
`%LOCALAPPDATA%\Microsoft\dotnet`, set `DOTNET_ROOT` to that folder. Without it,
`Oadm.Client.exe` and `Oadm.Server.exe` only look in `C:\Program Files\dotnet` and show a
"You must install .NET" dialog. The scripts set `DOTNET_ROOT` automatically, and the
`publish-*` scripts produce self-contained builds that need no runtime at all.

## Build and run

```sh
dotnet build Oadm.sln
dotnet run --project src/Oadm.Server   # gRPC server on http://0.0.0.0:5080
dotnet run --project src/Oadm.Client   # Avalonia desktop client
dotnet test Oadm.sln                   # unit tests, no network needed
```

The server has no authentication yet. Run it on a trusted LAN only.

## Scripts

Every script exists as `scripts/<name>.sh` (Linux, macOS, Git Bash) and `scripts/<name>.ps1`
(Windows PowerShell). They find a .NET 10 SDK on PATH, in `DOTNET_ROOT`, or in the per-user
install folder, and pass extra arguments through.

| Script | What it does |
|---|---|
| `build` | Build the whole solution |
| `build-server` | Build the server and bundled plugins |
| `build-client` | Build the client |
| `run-server` | Build and run the server on `http://0.0.0.0:5080` |
| `run-client` | Run the client. `run-client --fake` runs on sample data without a server |
| `run-dev` | Start the server in the background, then the client; closing the client stops the server |
| `test` | Unit tests, no camera needed (what CI runs) |
| `test-hardware` | Hardware tests against the cameras in `dev-cameras.yaml` |
| `publish-server` | Self-contained server plus plugins in `artifacts/publish/server/<rid>` |
| `publish-client` | Self-contained client in `artifacts/publish/client/<rid>` |
| `clean` | Remove `bin`, `obj` and `artifacts` |

Set `CONFIGURATION=Release` for release builds and `RID=linux-x64`, `osx-arm64`, `win-x64`
to publish for another platform.

```sh
./scripts/run-dev.sh                 # Linux / macOS / Git Bash
```

```powershell
.\scripts\run-dev.ps1                # Windows PowerShell
```

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
dotnet test Oadm.sln --filter Category=Hardware
dotnet test Oadm.sln --filter Category!=Hardware   # what CI runs
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
