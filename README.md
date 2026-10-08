# OADM - Open AXIS Device Management

Open source alternative to AXIS Device Manager for Windows, Linux and macOS.

**OADM is an independent open source project. It is not affiliated with, sponsored by or endorsed by Axis
Communications.**

- [For users](#for-users)
- [For developers](#for-developers)
- [License](#license)

# For users

## Install

Download the installer for your system from the [latest release](https://github.com/Cacsjep/oadm/releases/latest).
The installers are not signed yet; check a download against `SHA256SUMS.txt` of the release. Every installer offers
**Server and client** (default) or **Client only**, for a computer that connects to a server elsewhere. Uninstalling
keeps the data folder (devices, credentials, settings, PKI).

### Windows

1. Download `OADM-<version>-win-x64.msi` and double-click it.
2. Accept the terms and the license, then choose **Server and client** or **Client only**.
3. Finish. With the server, the Windows service **OADM Server** starts now and on every boot.

- Client: Start menu > **OADM**.
- Data folder: `%ProgramData%\OADM`.
- Uninstall: Settings > Apps > OADM.

### Linux (Debian, Ubuntu)

Download the `.deb` files you need and install them:

```sh
sudo apt install ./oadm-server_*.deb ./oadm-client_*.deb   # server and client
sudo apt install ./oadm-server_*.deb                       # server only
sudo apt install ./oadm-client_*.deb                       # client only
```

- Server: systemd service `oadm-server` (`sudo systemctl status oadm-server`).
- Client: app menu > **OADM**, or `oadm-client`.
- Data folder: `/var/lib/oadm`.
- Uninstall: `sudo apt remove oadm-server oadm-client`.

### macOS

1. Download `OADM-<version>-osx-x64.pkg`.
2. The package is not signed yet: right-click it, choose **Open** and confirm.
3. For the client only, click **Customize** and clear **OADM server**.

- Server: background service `com.oadm.server`.
- Client: **Applications > OADM**.
- Data folder: `/Library/Application Support/OADM`.
- Uninstall: `sudo /Applications/OADM.app/Contents/Resources/uninstall-oadm.sh`.

More per system: [`packaging/README.md`](packaging/README.md).

## First start

1. Install OADM on the computer that runs the server. The service starts on its own.
2. Start the client. It connects to `localhost:5080`; for another computer enter `<server>:5080`
   (`--server <server>:5080` preselects it). On the first connection, compare the fingerprint the client shows with
   the line "Server TLS certificate fingerprint" in the server log, then confirm.
3. While the server has no users, the login window creates the **first administrator**. From another computer you
   also need the setup code from `setup-code.txt` in the server's data folder (also in the server log). Add more users
   on the **Users** page.
4. Add the passwords you use on the **Credentials** page. Then add devices with **Add** on the Devices page
   (**Discovery**, **Network range**, **Add manually** or **Import from file**). OADM logs in to every device it finds.

## Ports and permissions

| Port | Used by |
|---|---|
| TCP 5080 | Client to server (encrypted, login required) |
| UDP 123 | NTP server (only when enabled) |
| UDP 67 | DHCP server (only when enabled) |

The Windows installer opens TCP 5080 for Domain and Private networks. The NTP and DHCP servers open their port while
enabled. The server runs as LocalSystem / root because these two servers need privileged ports. When another program
holds the port (for example Windows Time on 123), the plugin's page says so and how to stop it.

## Data and backup

The data folder holds the database, the plugin settings (including the PKI's certificate authority) and `master.key`,
which encrypts all stored passwords. Back up the whole folder: without `master.key` the passwords cannot be read. The
PKI page can also back up the CA to a password-protected file.

## Logs and troubleshooting

If the server does not start or stops, read its log first. Logs roll daily or at 20 MB (server) / 10 MB (client);
OADM keeps the last 14 days and at most 30 files.

| | Server log (`oadm-server-<date>.log`) | If the server fails before its log starts |
|---|---|---|
| Windows (service) | `%ProgramData%\OADM\logs\` | Event Viewer, Windows Logs > Application (source ".NET Runtime") and System (Service Control Manager) |
| Linux (service) | `/var/lib/oadm/logs/` | `journalctl -u oadm-server` |
| macOS (service) | `/Library/Application Support/OADM/logs/` | `/Library/Logs/OADM/oadm-server.err.log` |
| Started by hand or with `manage` | `<data folder>/logs/` (default `%LOCALAPPDATA%\Oadm\logs`, `~/.local/share/Oadm/logs`, `~/Library/Application Support/Oadm/logs`) | the console window |

The client writes `client-<date>.log` to the same default folders. The Logs page shows the client log live and, for
administrators, the audit log (who changed what). Client error messages name the log file.

# For developers

## Requirements

- .NET 10 SDK (see `global.json`)

If .NET 10 is installed per user (for example with `dotnet-install` into `%LOCALAPPDATA%\Microsoft\dotnet`), set
`DOTNET_ROOT` to that folder. Otherwise `Oadm.Client.exe` and `Oadm.Server.exe` look only in `C:\Program Files\dotnet`
and show "You must install .NET". `manage` sets `DOTNET_ROOT` for you, and `manage publish` builds self-contained apps
that need no runtime.

## Build and run

```sh
./manage.sh run dev             # build everything, start the server in the background, then the client
./manage.sh run client --fake   # client with built-in sample data, no server
```

Plain dotnet:

```sh
dotnet build Oadm.sln
dotnet run --project src/Oadm.Server   # gRPC server on https://0.0.0.0:5080 (own certificate, login required)
dotnet run --project src/Oadm.Client   # Avalonia client
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

`manage help` lists the verbs; `manage <verb> help` (or `-h`, `--help`) shows targets, options, defaults and examples.
Both scripts read their help from [`scripts/manage-help.txt`](scripts/manage-help.txt).

| Command | What it does |
|---|---|
| `manage build [all\|server\|client\|plugins] [--release]` | Build the solution (default), the server with the bundled plugins, the client, or only the plugins |
| `manage run server [--port N] [--data DIR] [--release] [--no-plugin-build]` | Build and run the server in the foreground (default `https://0.0.0.0:5080`) |
| `manage run client [--fake] [--server URL] [--data DIR] [--release] [--no-plugin-build]` | Build the client and all plugins, then run the client; `--fake` uses sample data without a server |
| `manage run dev [--port N] [--data DIR] [--release] [--no-plugin-build]` | Server in the background (log in `artifacts/logs/server-dev.log`), then the client; closing the client stops the server. First run: confirm the fingerprint, then create the first administrator (no setup code on the same computer) |
| `manage test [unit\|perf\|hardware\|all] [--filter EXPR] [--release]` | Unit tests (default, what CI runs), scale tests with 5,000 devices and 50,000 tasks (`Category=Perf`, a few minutes, see [`docs/scale-audit.md`](docs/scale-audit.md)), read-only hardware tests against `dev-cameras.yaml`, or all |
| `manage publish [all\|server\|client] [--rid RID] [--version V]` | Self-contained single-file apps in `artifacts/publish/<app>/<rid>`, plugins next to each exe |
| `manage package <windows\|linux\|macos> [--rid RID] [--version V]` | Installer (MSI, .deb, .pkg) in `artifacts/packages/`, see [`packaging/README.md`](packaging/README.md) |
| `manage clean` | Remove `bin`, `obj` and `artifacts` (data folders stay) |
| `manage info` | Show the .NET SDK, `DOTNET_ROOT`, configuration and RID in use |

`--no-plugin-build` uses the plugins already in `artifacts/plugins`; faster when no plugin changed.

The scripts find a .NET 10 SDK on `PATH`, in `DOTNET_ROOT`, in `~/.dotnet` or in `%LOCALAPPDATA%\Microsoft\dotnet`,
set `DOTNET_ROOT` for the apps they start and turn off telemetry. `CONFIGURATION=Release` and `RID=linux-x64` (or
`osx-arm64`, `win-x64`, ...) set defaults that `--release` and `--rid` override. Arguments after `--` go unchanged to
dotnet (build, test, publish) or to the app (run). PowerShell drops a bare `--`, so quote it there (`'--'`):

```sh
./manage.sh run dev --port 5099 --data ./tmp-data
./manage.sh test unit --filter FullyQualifiedName~Polling -- --no-build
```

```powershell
.\manage.ps1 publish client --rid win-x64
.\manage.ps1 build all '--' -v detailed
```

Exit codes: `0` success, `1` environment error (no .NET 10 SDK, no `dev-cameras.yaml`, server did not start), `2` usage
error, anything else comes from dotnet or the app. If the execution policy blocks scripts, run
`powershell -ExecutionPolicy Bypass -File .\manage.ps1 ...`.

## Tests

| Category | Command | Content |
|---|---|---|
| Unit (no category) | `manage test unit` | Fast, no network, no devices; what CI runs |
| `Perf` | `manage test perf` | Scale tests (5,000 devices, 50,000 tasks) with time budgets for a developer machine |
| `Hardware` | `manage test hardware` | Read-only tests against your devices in `dev-cameras.yaml` |
| `HardwareWrite` | never from `manage` | Tests that change a device; run only on purpose against a test device |

The UI is tested only with Avalonia headless rendering; `OADM_SCREENSHOT_DIR` saves the screenshots.

### Developer cameras (`dev-cameras.yaml`)

Hardware tests and local tools read your Axis devices from `dev-cameras.yaml` in the repository root. The file holds
passwords, so it is **git-ignored** and every developer keeps their own.

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

Without the file, hardware tests skip themselves.

## Extending OADM: plugins

Most features are plugins, and your plugins use the same SDK as the bundled ones. Full guide with code:
[`plugins/README.md`](plugins/README.md). Contracts: `src/Oadm.Sdk` (server) and `src/Oadm.Sdk.Client` (client).

### Plugin types

| Type | Where it shows up | Runs | Examples |
|---|---|---|---|
| **Task plugin** (`ITaskPlugin`) | Device context menu (in a group submenu), optionally the toolbar | On the server, one task per selected device, with named steps and a log | Restart, Upgrade firmware, Users, Network settings |
| **Task dialog** (`ITaskPluginDialog`, client part of a task plugin) | Opens before the task runs | In the client: collects input, reads the device through read-only queries (`ITaskPluginQuery`), returns the payload | Users, Date and time, Assign IP address |
| **Core plugin** (`ICorePlugin` + `ICorePluginPage`) | Own page in the navigation rail | A long-running service on the server with a page in the client; may contribute task plugins | NTP server, DHCP server, PKI, VAPIX Commander, Snapshot report |
| **Toolbar plugin** (`IToolbarPlugin`) | A control in the Devices page toolbar | In the client | Add, Remove, Export (built in) |

### Create a plugin

1. Copy the closest bundled plugin: `plugins/Oadm.Plugins.Restart` (task plugin without a dialog),
   `plugins/Oadm.Plugins.Users(.Client)` (task plugin with a dialog), `plugins/Oadm.Plugins.NtpServer(.Client)` (core
   plugin with a page), or the sample in `tests/TestPlugins` (toolbar plugin).
2. Rename the projects (`Oadm.Plugins.<Name>`, assembly `Oadm.Plugins.<Name>.Server` / `.Client`), set the id in
   `plugin.json` and `<OadmPluginId>` in the project file, and add the projects to `Oadm.sln`.
3. Build. The output goes to `artifacts/plugins/<plugin id>/`, which server and client load in a repository checkout;
   `manage run dev` shows it at once.
4. Add a test project `tests/Oadm.Plugins.<Name>.Tests` (fake devices, no network) and, for a UI, headless screenshot
   tests.

An installed OADM loads plugins from `plugins/<id>/` next to the server and client exe and from
`<data folder>/plugins/<id>/`. A plugin folder holds `plugin.json`, `<Name>.Server.dll`, the optional
`<Name>.Client.dll` and private dependencies, never the SDK or Avalonia assemblies (the host provides them). A broken
plugin is logged and skipped; server and client still start.

### Rules for every plugin

- **Device safety** when a plugin changes a device: check the device's API versions first, validate all input before
  the first write, never lock OADM out, never store or log passwords and payloads (see [`CLAUDE.md`](CLAUDE.md),
  "HARD RULE: device safety").
- **Steps and names:** every device request is a named step ("Read users", "Upload firmware"); the task list says what
  the task does ("Add user joe"), not the menu name.
- **UI:** shared controls from `Oadm.Sdk.Client.Controls` (`ui:FormField`, `ui:StatusChip`, `ui:PasswordBox`, ...),
  validation errors directly below the field, plain language (see "Wording" in `CLAUDE.md`), no own colors or font
  sizes.
- **Scale:** lists are virtualized and stay fast with 5,000 devices (tested with 5,000 fake devices).

### Working with an AI assistant

OADM is developed together with Claude, and the repository is set up for it:

- [`CLAUDE.md`](CLAUDE.md) is the single source of truth for the spec and the coding rules. Claude Code reads it on its
  own; give it to other assistants first. A changed decision goes into `CLAUDE.md` in the same change.
- Describe the feature like a user story and let the assistant ask before it writes code, for example: *"Read
  CLAUDE.md and plugins/README.md. I want a task plugin 'Set host name' in the Network group, with a dialog for one or
  many devices. Ask me what is unclear, write the spec, then build it with tests and headless screenshots."*
- The assistant checks UI work only with headless screenshots (`OADM_SCREENSHOT_DIR`), never by clicking on your
  desktop.
- Real devices: an assistant may run the read-only hardware tests against `dev-cameras.yaml`. Tests that change a
  device (`HardwareWrite`) run only when you ask, and only against a test device.
- Ask for small, focused commits and `manage test unit` before each commit.

## Layout

| Folder | Content |
|---|---|
| `src/Oadm.Contracts` | gRPC protobuf contracts |
| `src/Oadm.Sdk` | Plugin SDK (server side) |
| `src/Oadm.Sdk.Client` | Plugin SDK (Avalonia side) and the shared controls |
| `src/Oadm.Core` | Domain, VAPIX client, discovery, tasks, persistence |
| `src/Oadm.Server` | Server host |
| `src/Oadm.Client` | Avalonia client |
| `plugins/` | Bundled plugins (task and core plugins), see [`plugins/README.md`](plugins/README.md) |
| `tests/` | xUnit tests |
| `packaging/` | Installers (WiX, .deb, .pkg) |
| `docs/` | Specs, research and reference screenshots |

# License

Apache-2.0, see [LICENSE](LICENSE). Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Every
installer ships the full `THIRD-PARTY-NOTICES.txt` (every package with its license text, generated by
`manage publish`); the client shows it on the **About** page.
