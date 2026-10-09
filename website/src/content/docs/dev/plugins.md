---
title: Write a plugin
description: Add your own tasks, pages and toolbar entries to OADM.
---

Most OADM features are plugins, and your plugins use the same SDK as the built-in ones. The full guide with code is
[plugins/README.md](https://github.com/Cacsjep/oadm/blob/main/plugins/README.md).

## Plugin types

| Type | Where it shows up | Runs | Examples |
|---|---|---|---|
| Task plugin | Device context menu, optionally the toolbar | On the server, one task per device, with steps and a log | Restart, Upgrade firmware, Users |
| Task dialog | Opens before the task runs | In the client; reads the device through read-only queries | Users, Date and time |
| Core plugin | Own page in the navigation rail | A service on the server with a page in the client; can add tasks | NTP server, PKI, Snapshot report |
| Toolbar plugin | A control in the Devices toolbar | In the client | Add, Export |

## Start

1. Copy the closest built-in plugin: `Oadm.Plugins.Restart` (task without dialog), `Oadm.Plugins.Users` (task with
   dialog) or `Oadm.Plugins.NtpServer` (page).
2. Rename the projects, set the id in `plugin.json` and `<OadmPluginId>`, add them to `Oadm.sln`.
3. Build. The output goes to `artifacts/plugins/<id>/`, which `manage run dev` loads.
4. Add a test project with fake devices and headless screenshots.

An installed OADM loads plugins from `plugins/<id>/` next to the server and client, and from
`<data folder>/plugins/<id>/`. A broken plugin is logged and skipped.

## Rules

- **Device safety**: check the device's API versions first, validate all input before the first write, never lock
  OADM out, never store or log passwords.
- **Steps and names**: every device request is a named step; the task name says what the task does.
- **UI**: the shared controls and theme of the host, errors directly below the field, plain wording.
- **Scale**: lists stay fast with 5,000 devices.
