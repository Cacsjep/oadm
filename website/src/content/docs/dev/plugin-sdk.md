---
title: Develop with the .NET SDK
description: Create, run, debug, test and ship an OADM plugin with the NuGet packages, without a checkout of OADM.
---

Everything you need is on NuGet, published with every OADM release in the same version: the SDK, test helpers, a
project SDK, templates and the `oadm` command. You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and
any editor; Windows, Linux and macOS work the same way.

## Your first plugin

```sh
dotnet new install Oadm.Plugin.Templates
dotnet new oadm-task -n DoorBell        # or oadm-page, oadm-action
cd DoorBell
dotnet tool restore                     # the oadm command of the same version
dotnet build
dotnet test
dotnet oadm dev start                   # an OADM server for this plugin, here, until Ctrl+C
```

In a second terminal:

```sh
dotnet oadm dev client
```

The client opens against the development server. On its first start it asks you to create the administrator (the server
runs on your computer, so no setup code is needed). The plugin is already there: right-click a device and open
**Network > DoorBell**.

| Template | What you get | Tutorial |
|---|---|---|
| `oadm-task` | A context menu entry with a dialog, one task per device | [Task plugin](../tutorial-task/) |
| `oadm-page` | A core plugin with its own page in the navigation rail | [Plugin page](../tutorial-page/) |
| `oadm-action` | An action for the event rules | [Event rule action](../tutorial-action/) |

`-n` becomes the class names: letters and digits, starting with a letter. Options: `--id acme.door-bell` (default
`community.<name in kebab case>`) and `--displayName "Door bell"`.

## What the template creates

```text
DoorBell/
  DoorBell.slnx
  Directory.Build.props          every project: .NET 10, the plugin folder .oadm/plugins/
  .config/dotnet-tools.json      the oadm command of this SDK version
  README.md                      the commands
  AGENTS.md                      the rules for AI assistants, see Build a plugin with AI
  CLAUDE.md                      points Claude Code to AGENTS.md
  DoorBell/                      the server part: DoorBell.Server.dll
    DoorBell.csproj
  DoorBell.Client/               the client part: DoorBell.Client.dll (dialog or page)
    DoorBell.Client.csproj
  DoorBell.Tests/                the tests (xUnit, Oadm.Sdk.Testing)
  .oadm/                         after a build: the plugin folder, the development server's data
```

Every build copies the plugin folder (both parts and `plugin.json`) to `.oadm/plugins/<plugin id>/`. That folder is
what OADM loads and what you ship.

## The project files

Both parts use the project SDK `Oadm.Plugin.Sdk`, in the version of the other packages (`Sdk="Oadm.Plugin.Sdk/4.5.2"`).
It names the assemblies, references the OADM SDK without copying it and writes `plugin.json` from the server project's
properties. The properties, and what OADM provides at runtime: [Plugin basics](../plugins/#the-project-files).

## The development server

`dotnet oadm dev start` runs a portable OADM server of this version on your computer, in the terminal you start it in:

- It downloads the portable server and client of the release once (checked against the release's checksums) and keeps
  them for every plugin.
- Its data lives in `.oadm/server-data` of your plugin, so it never touches an installed OADM. It listens on
  `https://localhost:5090` (`--port` for another).
- It loads every plugin folder in `.oadm/plugins/`. When a build changes the server part, it restarts by itself. A
  client part is loaded when the client starts, so open the client again for a new dialog or page.
- `dotnet oadm dev status`, `dev logs` and `dev stop` (from another terminal) show and end it.

Add cameras in the client like in any OADM: the development server reaches the devices of your network like an
installed one. A page or a dialog also opens without devices.

## Debug

Attach the debugger of Visual Studio, Rider or VS Code to `Oadm.Server` (the server part) or `Oadm.Client` (a dialog or
page) of the development server. `dotnet oadm debug --installed` runs the OADM installed on your computer with your
build instead. Both in detail, the logs and a plugin that does not show up: [Debug a plugin](../debugging/).

## Try it in the installed OADM

```sh
dotnet oadm deploy --installed
```

The client part goes into your user's client plugin folder. Then, after one administrator prompt, the tool stops the
service, copies the plugin into the server's plugin folder, removes the "downloaded from the internet" mark and starts
the service again. Close and open the client to load the client part. `--plugin <id>` picks one when the folder has
several. `dotnet oadm folders` shows where the installed server and client keep their data and plugins; nothing is
guessed, the tool reads them from the service.

The installed OADM must be at least the release of the packages you build with. On Windows `deploy` and `debug` refuse
an older one; on Linux and macOS they only warn.
`dotnet oadm doctor` checks that, the .NET SDK and the ports.

## Ship it

```sh
dotnet oadm pack
```

This builds in Release and writes `<plugin id>-<version>.oadmplugin`: a ZIP of the plugin folder. To install it, an
administrator unpacks it into a folder `<plugin id>` in the server's plugin folder and in the plugin folder of each
client, then restarts both (`dotnet oadm folders` names the folders). Tell your users that plugins run with the server's
rights: they install only plugins they trust.

## Tests

The tests reference `Oadm.Sdk.Testing`: a fake camera and device, the server's contexts of a task, an action and a core
plugin, and `StepRun`, which ends a task exactly like the server. See [Testing](../testing/).

## All commands

| Command | What it does |
|---|---|
| `oadm dev start [--port 5090] [--from <folder or ZIP>] [--server-version <version>]` | The development server; `--from` takes a portable OADM you built or downloaded yourself, `--server-version` downloads the portable server of another release (default: the tool's version) |
| `oadm dev client [--port <port>] [--from <folder or ZIP>] [--client-version <version>]` | The client with this folder's plugins, against the development server; `--from` and `--client-version` as for the server |
| `oadm dev status`, `dev logs [--lines 50]`, `dev stop` | The development server's state, its log, stop it |
| `oadm deploy --installed [--plugin <id>] [--server-only] [--workspace <folder>]` | Into the OADM installed on this computer; `--server-only` skips the client, `--workspace` names the plugin's folder when you run it elsewhere |
| `oadm deploy --to <folder>` | Copies the plugin folders into any OADM plugin folder |
| `oadm debug --installed [--workspace <folder>]` | The installed server in your terminal with your build |
| `oadm folders` | The installed server's and client's folders |
| `oadm pack [--out <folder>] [--configuration Release]` | The `.oadmplugin` file, built in that configuration (default Release) |
| `oadm license keys`, `license sign` | Keys and license keys for a plugin you sell |
| `oadm doctor` | Checks .NET, ports, the installed OADM and this folder |
| `oadm version` | The version of the `oadm` command |

Install the command for every folder with `dotnet tool install -g Oadm.Tools`; it is then `oadm` instead of
`dotnet oadm`.

## With a checkout of OADM

Working on OADM itself, or on a plugin that ships with it, you build in the repository instead:
`./manage.sh new task --name "My plugin"` creates the same plugin in `plugins/` and `tests/`, and `./manage.sh run dev`
starts OADM with it. See [Build from source](../build/).
