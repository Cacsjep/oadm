# Contributing to OADM

Thanks for helping. Bug reports, fixes, plugins, docs and tests of any size are welcome.

## Before you start

- **The spec is [`CLAUDE.md`](CLAUDE.md) and [`docs/spec/`](docs/spec/).** `CLAUDE.md` has the rules for the code (UI,
  wording, scale, device safety, security) and one paragraph per feature; `docs/spec/` has one file per feature. Read the
  parts you touch. A change of behavior updates the spec in the same pull request.
- For a bigger change, open an issue first and describe it as a user story, so we agree on the behavior before you
  write code.
- Plugins (tasks, pages, toolbar buttons, event rule actions) are the easiest way to add features: see
  [`plugins/README.md`](plugins/README.md) and the [developer guide](https://oadm.app/dev/overview/).

## Build, run and test

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) (see `global.json`). Everything else comes from NuGet.

```sh
./manage.sh build                 # or .\manage.ps1 build on Windows
./manage.sh run dev               # server in the background, then the client
./manage.sh run client --fake     # client with built-in sample data, no server or cameras
OADM_FAKE_USER=tech2 ./manage.sh run client --fake   # the same as tech2, a user with the device group Building B
./manage.sh test unit             # what CI runs; must pass before a pull request
```

On the first `run dev`, trust the server, then create the first administrator (no setup code on the same computer).
`manage help` lists the verbs, `manage <verb> help` the targets, options and examples of one; both scripts read them
from [`scripts/manage-help.txt`](scripts/manage-help.txt). The commands and test categories:
[Build from source](https://oadm.app/dev/build/).

If .NET 10 is installed per user (for example into `%LOCALAPPDATA%\Microsoft\dotnet`), set `DOTNET_ROOT` to that folder
before you start `Oadm.Server` or `Oadm.Client` from a build; `manage` sets it for the apps it starts.

Hardware tests read your devices from `dev-cameras.yaml` (copy `dev-cameras.example.yaml`; git-ignored, it holds
passwords) and skip themselves without it: [Testing](https://oadm.app/dev/testing/#real-cameras).

## Repository layout

| Folder | Content |
|---|---|
| `src/Oadm.Contracts` | gRPC protobuf contracts |
| `src/Oadm.Sdk`, `src/Oadm.Sdk.Client` | Plugin SDK (server side, Avalonia side with the shared controls) |
| `src/Oadm.Sdk.Testing`, `src/Oadm.Plugin.Sdk` | Test helpers for plugin tests, the MSBuild project SDK of plugins (NuGet packages) |
| `src/Oadm.Core`, `src/Oadm.Server` | Domain, VAPIX client, discovery, tasks, persistence; the server host |
| `src/Oadm.Client`, `src/Oadm.Theme` | The Avalonia client; the theme shared with the OADM Site Node app |
| `src/Oadm.Sites`, `src/Oadm.SiteNode*` | The link between server and site node; the OADM Site Node service, command line and app |
| `plugins/` | Bundled plugins, see [`plugins/README.md`](plugins/README.md) |
| `samples/`, `templates/` | The plugins of the developer tutorials with their tests; the `dotnet new` templates made from them |
| `tests/` | xUnit tests |
| `tools/` | The `oadm` tool, `manage new`, the SDK reference and notices generators |
| `website/`, `packaging/` | Website and user docs (https://oadm.app/); installers |
| `docs/` | The feature specs (`docs/spec/`) and the RFCs the protocol code follows (`docs/rfc/`) |

## Rules for changes

- **Tests with every change.** Unit tests need no network and no devices. Anything with a device list also gets a
  scale test (`Category=Perf`, 30,000 devices, 50,000 tasks).
- **UI is checked only with Avalonia headless tests** (screenshots with `OADM_SCREENSHOT_DIR`), never by automating a
  real desktop.
- **The HARD RULES in `CLAUDE.md` apply to every change:**
  - shared controls instead of copies;
  - validation errors directly below the field;
  - no style differences between views;
  - short, plain wording;
  - lists that scale to thousands of devices;
  - device safety for task plugins (check compatibility first, validate before writing, never lock OADM out).
- **Real devices:** hardware tests are read-only (`Category=Hardware`, against your own `dev-cameras.yaml`). Tests that
  change a device (`Category=HardwareWrite`) run only on purpose, against a test device.
- **New plugins:** start with `./manage.sh new task|page|action --name "My plugin"` (a working copy of the tutorial
  plugin under your name in `plugins/` and `tests/`, added to `Oadm.sln`). A plugin that does not ship with OADM needs
  no checkout: `dotnet new oadm-task` and the `oadm` command ([Develop with the .NET SDK](https://oadm.app/dev/plugin-sdk/)).
- **SDK comments:** every public type and member of `Oadm.Sdk` and `Oadm.Sdk.Client` has a doc comment for plugin
  authors; the website's SDK reference is generated from them (`manage build docs`) and a test fails without one.
- **No own sockets to devices:** `src/` and `plugins/` reach devices only through the host (`IVapixClient`, event
  streams, `IDeviceNetwork`), so devices at a site work too. The banned APIs (`Socket`, `TcpClient`, `UdpClient`,
  `Ping`, `Dns`, own HTTP handlers, `src/Oadm.Plugin.Sdk/targets/BannedSymbols.txt`) are build errors there (RS0030); an
  exception carries a `SuppressMessage` with a justification and is listed in `RoutingRulesTests.AllowedBannedApiUsers`.
- **Cross-platform:** the code builds and runs on Windows, Linux and macOS (no Windows-only APIs).
- **Secrets:** never log passwords, tokens or digest headers; never commit `dev-cameras.yaml`.

## Working with an AI assistant

OADM is developed together with Claude, and the repository is set up for it:

- Claude Code reads `CLAUDE.md` on its own (it points to the spec file of each feature); give it to other assistants
  first.
- Describe the feature like a user story and let the assistant ask before it writes code, for example: *"Read
  CLAUDE.md and plugins/README.md. I want a task plugin 'Set host name' in the Network group, with a dialog for one or
  many devices. Ask me what is unclear, write the spec, then build it with tests and headless screenshots."*
- The assistant checks UI work only with headless screenshots, never by clicking on your desktop. It may run the
  read-only hardware tests; tests that change a device run only when you ask, and only against a test device.
- Ask for small, focused commits and `manage test unit` before each commit. Review its work like any other contribution.

## Commits and pull requests

- [Conventional commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `test:`, `chore:`), small,
  focused and in English.
- Add a line to **Unreleased** in [`CHANGELOG.md`](CHANGELOG.md) for anything a user notices.
- No build warnings (they are errors) and `manage test unit` passes. A pull request runs the unit tests on Linux.
- Maintainers release by pushing a tag `vX.Y.Z`. CI then runs the tests on all three systems, builds the installers and
  publishes the GitHub release with that version's changelog section.

## License

OADM is licensed under the [Apache License 2.0](LICENSE). A contribution you submit is licensed under the same license
(Apache-2.0, section 5, "inbound = outbound"); no separate agreement is needed. Third-party code you add needs a
compatible license (MIT, BSD, Apache-2.0, LGPL as a separate library); never GPL code or GPL or nonfree FFmpeg builds.

## Reporting bugs and security issues

- **Bugs:** open a GitHub issue with the OADM version (About page), the operating system, the device model and AXIS OS
  version if a device is involved, what you expected, what happened, and the relevant log lines (see
  [Logs and troubleshooting](https://oadm.app/admin/troubleshooting/); remove addresses or names you do not want to
  share).
- **Security issues:** do **not** open a public issue. See [SECURITY.md](SECURITY.md).
