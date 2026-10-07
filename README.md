# OADM - Open AXIS Device Management

Open source, cross-platform alternative to AXIS Device Manager. Runs on Windows, Linux and macOS.
The full specification lives in [CLAUDE.md](CLAUDE.md).

## Requirements

- .NET 10 SDK (see `global.json`)

## Build and run

```sh
dotnet build Oadm.sln
dotnet run --project src/Oadm.Server   # gRPC server on http://0.0.0.0:5080
dotnet run --project src/Oadm.Client   # Avalonia desktop client
dotnet test Oadm.sln                   # unit tests, no network needed
```

The server has no authentication yet. Run it on a trusted LAN only.

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
