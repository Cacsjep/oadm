# Contributing to OADM

Thanks for helping. OADM is an open source alternative to AXIS Device Manager; contributions of any size are
welcome: bug reports, fixes, plugins, docs and tests.

## Before you start

- **The spec is [`CLAUDE.md`](CLAUDE.md).** It describes every feature and the rules the code follows (UI, scale,
  device safety, security). Read the parts you touch; a change of behavior updates `CLAUDE.md` in the same pull request.
- For a bigger change, open an issue first and describe it like a user story, so we can agree on the behavior before
  you write code.
- Plugins (tasks, pages, toolbar buttons) are the easiest way to add features: see [`plugins/README.md`](plugins/README.md).

## Build, run and test

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Everything else comes from NuGet.

```sh
./manage.sh build                 # or .\manage.ps1 build on Windows
./manage.sh run dev               # server in the background, then the client
./manage.sh run client --fake     # the client on built-in sample data, no server or cameras needed
./manage.sh test unit             # what CI runs; must pass before a pull request
```

`manage <verb> help` explains every command. More in the README: [Build and run](README.md#build-and-run),
[Developer commands](README.md#developer-commands-manage) and [Tests](README.md#tests).

## Rules for changes

- **Tests with every change.** Unit tests need no network and no devices. Scale tests (`Category=Perf`) cover 5,000
  devices and 50,000 tasks for anything with a device list.
- **UI is checked only with Avalonia headless tests** (screenshots with `OADM_SCREENSHOT_DIR`), never by automating
  a real desktop.
- **The HARD RULES in `CLAUDE.md` apply to every change:**
  - shared controls instead of copies;
  - validation errors directly below the field;
  - no style differences between views;
  - lists that scale to thousands of devices;
  - device safety for task plugins (check compatibility first, validate before writing, never lock OADM out).
- **Real devices:** hardware tests are read-only (`Category=Hardware`, against your own `dev-cameras.yaml`). Tests that
  change a device (`Category=HardwareWrite`) run only on purpose, against a test device.
- **Cross-platform:** the code must build and run on Windows, Linux and macOS (no Windows-only APIs).
- **Secrets:** never log passwords, tokens or digest headers; never commit `dev-cameras.yaml`.

## Commits and pull requests

- [Conventional commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`, `test:`, `chore:`), small and
  focused, in English.
- Add a line to the **Unreleased** section of [`CHANGELOG.md`](CHANGELOG.md) for anything a user notices.
- The build must have no warnings (warnings are errors) and `manage test unit` must pass.
- Releases are made by the maintainers by pushing a tag `vX.Y.Z`; CI then runs the tests on all three systems, builds
  the installers and publishes the GitHub release with the changelog section of that version.

## Working with an AI assistant

Many changes to OADM are written with AI coding assistants. The README section
[Working with an AI assistant](README.md#working-with-an-ai-assistant) explains how: give it `CLAUDE.md` first, let it ask questions before it
writes code, and review its work like any other contribution.

## License

OADM is licensed under the [Apache License 2.0](LICENSE). By submitting a contribution you agree that it is licensed
under the same license (Apache-2.0, section 5, "inbound = outbound"); no separate agreement is needed. Third-party
code you add must have a compatible license (MIT, BSD, Apache-2.0, LGPL as a separate library); never GPL code or GPL
or nonfree FFmpeg builds.

## Reporting bugs and security issues

- **Bugs:** open a GitHub issue with the OADM version (About page), the operating system, the device model and AXIS OS
  version if a device is involved, what you expected and what happened, and the relevant lines of the logs (see
  [Logs and troubleshooting](README.md#logs-and-troubleshooting); remove addresses or names you do not want to share).
- **Security issues:** please do **not** open a public issue. Report them privately through GitHub's
  **Security > Report a vulnerability** on the repository, so they can be fixed before they are made public.
