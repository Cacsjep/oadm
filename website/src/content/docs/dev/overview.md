---
title: Developer overview
description: How OADM is built and what you can add to it.
---

OADM is written in C# on .NET 10. Most of what you see in the app is a plugin, built on the same SDK that is open to
you. This section explains how OADM is put together, what a plugin can do and how to write one.

## Two processes

```text
 Client (Avalonia)                 Server (.NET service)                 Axis devices
 ┌────────────────────┐   gRPC    ┌───────────────────────────┐  VAPIX  ┌──────────┐
 │ Devices page       │◄────────► │ Device table, polling     │◄──────► │ cameras  │
 │ Plugin pages       │   TLS     │ Task engine               │  HTTP(S)│ speakers │
 │ Task dialogs       │           │ Event rules               │  RTSP   │ I/O, ... │
 │ Toolbar entries    │           │ Plugins (server parts)    │  WS     │          │
 └────────────────────┘           └───────────────────────────┘         └──────────┘
```

- **The server owns everything**: the device table, the stored passwords, the tasks, discovery, polling and every
  request to a device. It runs as a service on Windows, Linux and macOS.
- **The client is a thin UI.** It never talks to a device. It sends requests to the server over gRPC and keeps a live
  copy of the device and task lists.
- **Passwords never leave the server.** A plugin gets a VAPIX client that is already logged in, never the password.

A plugin follows the same split: a **server part** (`*.Server.dll`) does the device work, an optional **client part**
(`*.Client.dll`) shows a dialog, a page or a toolbar button.

## What a plugin can do

| You want to | Build | Example in the repository |
|---|---|---|
| Change or read something on many devices from the context menu | [Task plugin](../tasks/) | Restart, Users, Upgrade firmware |
| Ask the user first (a form, a file) | Task plugin with a [dialog](../tasks/#the-dialog) | Date and time, Applications |
| Run a service or show a page in the navigation rail | [Core plugin](../core-plugins/) | NTP server, Edge storage, PKI |
| Add a button to the Devices toolbar | [Toolbar plugin](../core-plugins/#toolbar-buttons) | System report |
| Do something when an event rule fires | [Action](../event-rules/#write-an-action) | Send email, Set output |
| Let event rules react to something your plugin sees | [Event source](../event-rules/#publish-events) | Device triggers |

Inside these, the SDK gives you:

- every managed device with its model, firmware, status, tags and API list ([Devices and VAPIX](../devices/));
- a logged-in VAPIX client per device, with WebSockets, RTSP event streams and video source lists;
- tasks with named steps, progress, a log and warnings, one task per device;
- settings per plugin, encrypted secrets, a private data folder;
- live events from the server part to its page, file downloads to the client;
- the shared controls and theme of the app, so a plugin looks like the rest of OADM ([UI kit](../ui/));
- permissions that administrators give per role on the **Users** page.

## Where the code is

| Folder | Content |
|---|---|
| `src/Oadm.Sdk` | The server SDK: plugin interfaces, devices, VAPIX, tasks, events, actions. No UI |
| `src/Oadm.Sdk.Client` | The client SDK: dialog, page and toolbar interfaces, shared controls, validation |
| `src/Oadm.Core` | Device model, VAPIX client, discovery, task engine, database |
| `src/Oadm.Server` | The server host: gRPC services, plugin loader, polling |
| `src/Oadm.Client` | The Avalonia app |
| `src/Oadm.Contracts` | The gRPC contracts (`.proto`) |
| `plugins/` | Every built-in plugin, each with its own `README.md` |
| `tests/` | Unit, headless UI, performance and hardware tests |

The SDK sources are the reference: every member has a comment that says what it does and what the host guarantees.
Start with `src/Oadm.Sdk/Plugins/Plugins.cs` and `src/Oadm.Sdk.Client/ClientPlugins.cs`.

## Where to go next

1. [Build from source](../build/): get OADM running from a checkout.
2. [Plugin basics](../plugins/): project layout, `plugin.json`, how plugins load.
3. A tutorial, each on one page with every file: [task plugin](../tutorial-task/), [plugin page](../tutorial-page/),
   [event rule action](../tutorial-action/). `./manage.sh new task|page|action --name "My plugin"` creates the
   tutorial's plugin under your name, ready to build and run.
4. The topic pages: [Devices and VAPIX](../devices/), [Task plugins](../tasks/), [Core plugins and pages](../core-plugins/),
   [Event rules](../event-rules/), [UI kit](../ui/), [Testing](../testing/).

Every public type and member, with its signature and comment, is in the [SDK reference](../../api/). The
rules and edge cases are in
[plugins/README.md](https://github.com/Cacsjep/oadm/blob/main/plugins/README.md) and the spec,
[CLAUDE.md](https://github.com/Cacsjep/oadm/blob/main/CLAUDE.md).
