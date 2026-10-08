# OADM - Open AXIS Device Management

Open source, cross-platform alternative to AXIS Device Manager. Runs on Windows, Linux and macOS.

**Its an independent, open-source project for managing AXIS network devices.
OADM is not affiliated with, sponsored by, or endorsed by Axis Communications.**

- [For users](#for-users)
- [For developers](#for-developers)
- [License](#license)

> **Security note:** clients log in over TLS (users with the roles Admin and Operator). Still run the server on a trusted
> network: the cameras themselves are reached over the LAN.

# For users

## Install

Installers are built for every release (GitHub Actions, workflow `release`; check downloads against `SHA256SUMS.txt`
of the release, the installers are not signed). Each installer offers **Server and client** (default) or **Client
only** (a client that connects to a server on another computer):

| | Windows | Linux (Debian, Ubuntu) | macOS |
|---|---|---|---|
| Package | `OADM-<version>-win-x64.msi` | `oadm_`, `oadm-server_`, `oadm-client_<version>_amd64.deb` | `OADM-<version>-osx-x64.pkg` |
| Install | Double-click the MSI, choose on "Choose what to install" | `sudo apt install ./oadm_*.deb ./oadm-server_*.deb ./oadm-client_*.deb`; client only: `sudo apt install ./oadm-client_*.deb` | Right click > Open (the package is not signed yet); client only: Customize, uncheck "OADM server" |
| Server | Windows service "OADM Server", starts automatically | systemd service `oadm-server` | LaunchDaemon `com.oadm.server` |
| Client | Start menu "OADM" | App menu "OADM" or `oadm-client` | `/Applications/OADM.app` |
| Data | `%ProgramData%\OADM` | `/var/lib/oadm` | `/Library/Application Support/OADM` |
| Uninstall | Apps and Features | `sudo apt remove oadm oadm-server oadm-client` | `sudo /Applications/OADM.app/Contents/Resources/uninstall-oadm.sh` |

Uninstalling keeps the data folder (devices, credentials, settings, the PKI). Details: [`packaging/README.md`](packaging/README.md).

## First start

1. Install OADM on the computer that should run the server; the service starts on its own.
2. Start the client. The login window connects to `localhost:5080` over TLS; for a server on another computer enter
   `<server>:5080` (the list keeps the recent servers, `--server <server>:5080` preselects one). On the first
   connection the client shows the server certificate's SHA-256 fingerprint: compare it with the line
   "Server TLS certificate fingerprint" in the server log and confirm it.
3. While the server has no users, the login window creates the **first administrator**. On the server computer this
   needs nothing more; from another computer enter the setup code the server logs at startup and keeps in
   `setup-code.txt` in its data folder. Administrators add more users (Administrator or Operator) on the Settings page.
4. Add your devices with **Scan**, **Scan IP range** or **Add manually** on the Devices page. Add the passwords you
   use to the credential list on the Settings page first: OADM then logs in to every device it finds.

## Ports and permissions

| Port | Used by |
|---|---|
| TCP 5080 | Client to server (gRPC over TLS, login required) |
| UDP 123 | NTP server (only when enabled on its page) |
| UDP 67 | DHCP server (only when enabled on its page) |

The Windows installer opens TCP 5080 in the Windows firewall for Domain and Private networks; the NTP and DHCP servers
open their UDP port (Domain and Private) while they are enabled and close it again when disabled. The server runs as
LocalSystem / root, because
the NTP and DHCP servers need privileged ports. Another program that uses the same port (for example the Windows
Time service on 123) is shown on the plugin's page with a hint how to stop it.

## Data and backup

The data folder holds the database, the master key (`master.key`) that encrypts all stored passwords, and the
plugin settings, including the PKI's certificate authority. Back up the whole folder; the passwords in the database
cannot be read without `master.key`. The PKI page can also export the CA as a password-protected backup.

## Logs and troubleshooting

When the server does not start or stops, look at its log first. Log files roll daily or at 20 MB (server) / 10 MB
(client); the last 14 days and at most 30 files are kept, so they never fill the disk.

| | Server log (`oadm-server-<date>.log`) | If the server fails before its log starts |
|---|---|---|
| Windows (service) | `%ProgramData%\OADM\logs\` | Event Viewer, Windows Logs > Application (source ".NET Runtime") and System (Service Control Manager) |
| Linux (service) | `/var/lib/oadm/logs/` | `journalctl -u oadm-server` |
| macOS (service) | `/Library/Application Support/OADM/logs/` | `/Library/Logs/OADM/oadm-server.err.log` |
| Started by hand or with `manage` | `<data folder>/logs/` (default `%LOCALAPPDATA%\Oadm\logs`, `~/.local/share/Oadm/logs`, `~/Library/Application Support/Oadm/logs`) | the console window |

The client writes `client-<date>.log` to the `logs` folder of its data folder (`%LOCALAPPDATA%\Oadm\logs`,
`~/.local/share/Oadm/logs`, `~/Library/Application Support/Oadm/logs`). The Logs page shows the client log live and,
for administrators, the server's audit log (who changed what). Error messages in the client name the log file.

# For developers

## Requirements

- .NET 10 SDK (see `global.json`)

If .NET 10 is installed per user, for example with `dotnet-install` into `%LOCALAPPDATA%\Microsoft\dotnet`, set
`DOTNET_ROOT` to that folder. Without it, `Oadm.Client.exe` and `Oadm.Server.exe` only look in
`C:\Program Files\dotnet` and show a "You must install .NET" dialog. `manage.sh` / `manage.ps1` set `DOTNET_ROOT`
automatically, and `manage publish` produces self-contained builds that need no runtime at all.

## Build and run

```sh
./manage.sh run dev          # build everything, start the server in the background, then the client
./manage.sh run client --fake   # the client on built-in sample data, no server needed
```

Plain dotnet works too:

```sh
dotnet build Oadm.sln
dotnet run --project src/Oadm.Server   # gRPC server on https://0.0.0.0:5080 (own certificate, login required)
dotnet run --project src/Oadm.Client   # Avalonia desktop client
dotnet test Oadm.sln --filter "Category!=Hardware&Category!=Perf&Category!=HardwareWrite"   # unit tests
```

## Developer commands (`manage`)

One entry point per shell at the repository root, with the same verbs, options, help texts and exit codes:

```sh
./manage.sh <verb> [target] [options] [-- extra args]     # Linux, macOS, Git Bash
```

```powershell
.\manage.ps1 <verb> [target] [options] [-- extra args]    # Windows PowerShell 5.1, PowerShell 7
```

Every level explains itself: `manage` or `manage help` lists the verbs, `manage <verb> help` (or `-h`, `--help`)
shows targets, options, defaults and examples. Both shells print the help texts from
[`scripts/manage-help.txt`](scripts/manage-help.txt).

| Command | What it does |
|---|---|
| `manage build [all\|server\|client\|plugins] [--release]` | Build the solution (default), the server plus bundled plugins, the client, or only the plugins |
| `manage run server [--port N] [--data DIR] [--release] [--no-plugin-build]` | Build and run the server in the foreground (default `https://0.0.0.0:5080`) |
| `manage run client [--fake] [--server URL] [--data DIR] [--release] [--no-plugin-build]` | Build the client and all plugins, then run the client; `--fake` runs on sample data without a server |
| `manage run dev [--port N] [--data DIR] [--release] [--no-plugin-build]` | Server in the background (log in `artifacts/logs/server-dev.log`), then the client; closing the client stops the server. First run: confirm the server fingerprint, then create the first administrator in the login window (no setup code on the same computer) |
| `manage test [unit\|perf\|hardware\|all] [--filter EXPR] [--release]` | Unit tests (default, fast, what CI runs), scale tests with 5,000 devices and 50,000 tasks (`Category=Perf`, a few minutes, see [`docs/scale-audit.md`](docs/scale-audit.md)), read-only hardware tests against `dev-cameras.yaml`, or all of them |
| `manage publish [all\|server\|client] [--rid RID] [--version V]` | Self-contained single-file apps in `artifacts/publish/<app>/<rid>`, plugins next to each exe |
| `manage package <windows\|linux\|macos> [--rid RID] [--version V]` | Installer (MSI, .deb, .pkg) in `artifacts/packages/`, see [`packaging/README.md`](packaging/README.md) |
| `manage clean` | Remove `bin`, `obj` and `artifacts` (data folders stay) |
| `manage info` | Show the .NET SDK, `DOTNET_ROOT`, configuration and RID in use |

`--no-plugin-build` skips the plugin build and uses the plugins already deployed in `artifacts/plugins`: faster
when no plugin changed.

The scripts find a .NET 10 SDK on `PATH`, in `DOTNET_ROOT`, in `~/.dotnet` or in `%LOCALAPPDATA%\Microsoft\dotnet`,
set `DOTNET_ROOT` for the launched apps and turn off telemetry. `CONFIGURATION=Release` and `RID=linux-x64` (or
`osx-arm64`, `win-x64`, ...) set the defaults that `--release` and `--rid` override. Arguments after `--` go
unchanged to dotnet (build, test, publish) or to the app (run). PowerShell swallows a bare `--`, so write `'--'`
(quoted) there:

```sh
./manage.sh run dev --port 5099 --data ./tmp-data
./manage.sh test unit --filter FullyQualifiedName~Polling -- --no-build
```

```powershell
.\manage.ps1 publish client --rid win-x64
.\manage.ps1 build all '--' -v detailed
```

Exit codes: `0` success, `1` environment error (no .NET 10 SDK, no `dev-cameras.yaml`, server did not start),
`2` usage error, anything else is the exit code of dotnet or the app. If the execution policy blocks scripts, run
`powershell -ExecutionPolicy Bypass -File .\manage.ps1 ...`.

## Tests

| Category | Command | Content |
|---|---|---|
| Unit (no category) | `manage test unit` | Fast, no network, no devices; what CI runs on every push |
| `Perf` | `manage test perf` | Scale tests (5,000 devices, 50,000 tasks) with time budgets for a developer machine |
| `Hardware` | `manage test hardware` | Read-only tests against your devices in `dev-cameras.yaml` |
| `HardwareWrite` | never from `manage` | Tests that change a device; only run on purpose against a test device |

UI is tested only with Avalonia headless rendering; set `OADM_SCREENSHOT_DIR` to write the rendered screenshots.

### Developer cameras (`dev-cameras.yaml`)

Hardware tests and local tooling read the Axis devices available to you from `dev-cameras.yaml` in the repository
root. The file is **git-ignored** because it contains passwords, so every developer keeps their own.

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

Hardware tests skip themselves when the file is missing.

## Extending OADM: plugins

Almost every feature of OADM is a plugin, and your own plugins use the same SDK as the bundled ones. The full guide
with code is [`plugins/README.md`](plugins/README.md); the contracts are in `src/Oadm.Sdk` (server) and
`src/Oadm.Sdk.Client` (client).

### Plugin types

| Type | Where it shows up | Runs | Examples |
|---|---|---|---|
| **Task plugin** (`ITaskPlugin`) | Device context menu (in a group submenu) and optionally the toolbar | On the server, one task per selected device, with named steps and a log | Restart, Upgrade firmware, Users, Network settings |
| **Task dialog** (`ITaskPluginDialog`, client part of a task plugin) | Opens before the task runs | In the client; collects input, reads the device through read-only queries (`ITaskPluginQuery`), returns the payload | Users, Date and time, Assign IP address |
| **Core plugin** (`ICorePlugin` + `ICorePluginPage`) | Own page in the navigation rail | A long-running service on the server with a page in the client; may contribute task plugins | NTP server, DHCP server, PKI, VAPIX Commander, Snapshot report |
| **Toolbar plugin** (`IToolbarPlugin`) | A control in the Devices page toolbar | In the client | Scan, Scan IP range, Add manually (built in) |

### Create a plugin

1. Copy the closest bundled plugin as a starting point: `plugins/Oadm.Plugins.Restart` (task plugin without a
   dialog), `plugins/Oadm.Plugins.Users(.Client)` (task plugin with a dialog), `plugins/Oadm.Plugins.NtpServer(.Client)`
   (core plugin with a page), or the sample in `tests/TestPlugins` (toolbar plugin).
2. Rename the projects (`Oadm.Plugins.<Name>`, assembly `Oadm.Plugins.<Name>.Server` / `.Client`), set the id in
   `plugin.json` and `<OadmPluginId>` in the project file, and add the projects to `Oadm.sln`.
3. Build: the output lands in `artifacts/plugins/<plugin id>/`, where server and client of a repository checkout
   load it. `manage run dev` shows it right away.
4. Add a test project `tests/Oadm.Plugins.<Name>.Tests` (fake devices, no network) and, for a UI, headless screenshot
   tests.

Installed OADM loads plugins from `plugins/<id>/` next to the server and client exe and from `<data folder>/plugins/<id>/`.
A plugin folder holds `plugin.json`, `<Name>.Server.dll`, the optional `<Name>.Client.dll` and private
dependencies, never the SDK or Avalonia assemblies (they come from the host). A broken plugin is logged and skipped;
it never stops server or client.

### Rules every plugin follows

- **Device safety** for anything that changes a device: check the device's API versions first, validate all input
  before the first write, never lock OADM out, never persist or log passwords and payloads (details in
  [`CLAUDE.md`](CLAUDE.md), "HARD RULE: device safety").
- **Steps and names:** every device request is a named step ("Read users", "Upload firmware"); the task list shows
  what the task does ("Add user joe"), never just the menu name.
- **UI:** shared controls from `Oadm.Sdk.Client.Controls` (`ui:FormField`, `ui:StatusChip`, `ui:PasswordBox`, ...),
  validation errors directly under the field, plain language, no own colors or font sizes.
- **Scale:** lists are virtualized and stay fast with 5,000 devices (a test with 5,000 fake devices).

### Working with an AI assistant

OADM is co-developed with Claude, and the repository is set up for it:

- [`CLAUDE.md`](CLAUDE.md) is the single source of truth for the spec and the coding rules. Claude Code reads it
  automatically; for other assistants, give it to them first. Keep it current: a changed decision goes into
  `CLAUDE.md` in the same change.
- Describe the feature like a user story and let the assistant ask questions before it writes code, for example:
  *"Read CLAUDE.md and plugins/README.md. I want a task plugin 'Set host name' in the Network group, with a dialog
  for one or many devices. Ask me what is unclear, write the spec, then build it with tests and
  headless screenshots."*
- The assistant checks its UI work only with headless screenshots (`OADM_SCREENSHOT_DIR`), never by clicking on
  your desktop.
- Real devices: an assistant may run the read-only hardware tests against `dev-cameras.yaml`. Tests that change a
  device (`HardwareWrite`) only run when you explicitly ask, and only against a test device.
- Ask for small, focused commits and for `manage test unit` before a commit.

## Layout

| Folder | Content |
|---|---|
| `src/Oadm.Contracts` | gRPC protobuf contracts |
| `src/Oadm.Sdk` | Plugin SDK (server side) |
| `src/Oadm.Sdk.Client` | Plugin SDK (Avalonia UI side) and the shared controls |
| `src/Oadm.Core` | Domain, VAPIX client, discovery, tasks, persistence |
| `src/Oadm.Server` | Server host |
| `src/Oadm.Client` | Avalonia client |
| `plugins/` | Bundled plugins (task plugins and core plugins), see [`plugins/README.md`](plugins/README.md) |
| `tests/` | xUnit tests |
| `packaging/` | Installers (WiX, .deb, .pkg) |
| `docs/` | Specs, research and reference screenshots |

# License

Apache-2.0, see [LICENSE](LICENSE). Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); every
installer ships the complete `THIRD-PARTY-NOTICES.txt` (all packages with their license texts, generated by
`manage publish`), also shown in the client under Settings > About and licenses.
