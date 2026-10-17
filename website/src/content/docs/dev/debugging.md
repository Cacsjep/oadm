---
title: Debug a plugin
description: Breakpoints in the server and the client, the logs, and what to check when a plugin does not show up.
---

Your plugin runs inside the OADM server (its server part) and inside the client (its dialog or page). You debug it by
attaching the debugger to the process that runs the part you want to stop in.

## With the .NET SDK

1. `dotnet oadm dev start` runs the development server with your plugin from `.oadm/plugins/`, with its symbols.
2. `dotnet oadm dev client` opens the client with your client part.
3. Attach the debugger of Visual Studio, Rider or VS Code (C# Dev Kit) to **`Oadm.Server`** (tasks, queries, core
   plugins, actions) or **`Oadm.Client`** (dialogs, pages, toolbar entries), then trigger your code.

A build restarts the development server when the server part changed; attach again after it. To stop in your code with
the data and devices of the OADM installed on your computer, `dotnet oadm debug --installed` runs the installed server in
your terminal with your build (the service starts again afterwards). The development server's log: `dotnet oadm dev logs`,
or `.oadm/server-data/logs/`. More: [Develop with the .NET SDK](../plugin-sdk/#debug).

## In a checkout of OADM

Plugins built in your checkout are copied with their `.pdb` files to `artifacts/plugins/<id>/`, and OADM loads them with
their symbols, so breakpoints in your code work.

1. Build: `./manage.sh build plugins` (or your IDE's build).
2. Start OADM: `./manage.sh run dev` starts the server in the background and then the client.
3. Attach the debugger of Visual Studio, Rider or VS Code (C# Dev Kit):
   - to **`Oadm.Server`** for task plugins, queries, core plugins and actions;
   - to **`Oadm.Client`** for dialogs, pages and toolbar entries.
4. Trigger your code: run the task, open the page, fire the rule.

To start the server under the debugger from the beginning (to stop in `StartAsync`), run the project
`src/Oadm.Server` from your IDE instead of `run dev`, then start the client with `./manage.sh run client`.

OADM reads plugin files into memory when it starts, so a running OADM never locks them: rebuild whenever you like,
then restart the server (and the client for client parts) to load the new build.

## Logs

| What | Where |
|---|---|
| Server, started by `manage run dev` | `artifacts/logs/server-dev.log` |
| Server log file | `logs/oadm-server-<date>.log` in the data folder (from a checkout: `%LOCALAPPDATA%\Oadm`, `~/.local/share/Oadm` or `~/Library/Application Support/Oadm`), and on the **Logs** page, **Server** (permission "See the server log") |
| Client log file | `logs/client-<date>.log` in the client's data folder, and live on the **Logs** page |
| One task | **Details** in the tasks pane: the steps and what `ctx.Log` wrote |
| Event rules | The **Event log** on the Event rules page: every action run with its result and what `ctx.Log` wrote |

Write to the logs from your plugin:

- `ctx.Logger` (server parts) writes to the server log. Use it for what developers and support need.
- `ctx.Log(level, message)` in a task writes to that task's log, which users see. Use it for what users need.
- Never log a password, a token or a payload with secrets.

## A plugin does not show up

Look in the server log first. Every plugin folder writes one of these lines at start:

```text
Loaded plugin package acme.hello 1.0.0 from G:\odam\artifacts\plugins\acme.hello (1 plugin(s))
Plugin problem in G:\odam\artifacts\plugins\acme.hello: Plugin acme.hello requires SDK 0.2.0, host has 0.1.0.
```

| You see | Check |
|---|---|
| No line for your plugin | The plugin folder (`.oadm/plugins/<id>/`, in a checkout `artifacts/plugins/<id>/`) exists and holds `plugin.json` and a `*.Server.dll` (the build copies them; a build with `OadmSkipPluginDeploy=true` does not) |
| "requires SDK ..." | `minSdkVersion` in `plugin.json` is newer than the server's SDK: update OADM, or build with the packages of an older release |
| "is already loaded from another folder" | The same id in two plugin folders: remove one |
| Loaded, but no menu entry | The plugin is off on the **Plugins** page; your class needs to be public with a public parameterless constructor; `ShowInMenus` must be true; the user's role needs the task's permission |
| The menu entry is grey | `CanRun` returned false: the tooltip shows your `NotSupportedReason` |
| No rail entry for a page | The plugin is off, the role lacks "show", or `HasPage` is false |
| "The page of this plugin is not installed on this client." | The client found no `ICorePluginPage` with your plugin id: check the client part's `PluginId` and that its `*.Client.dll` is in the folder |
| "The task ... needs a client plugin ... that is not installed on this computer." | `RequiresDialog` is true but no `ITaskPluginDialog` with the same `PluginId` was loaded: look for "Could not load client plugin" in the client log |
| "The ... page failed: ..." | `CreateView` threw: the message and the stack trace are in the client log |

## Tests first

Most problems are faster to find in a test than in the running app: a fake camera and a fake context run your task or
your service in milliseconds, with breakpoints in your IDE's test runner. The tutorials show the fakes for each kind of
plugin.
