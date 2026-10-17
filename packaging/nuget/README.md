# OADM plugin SDK

Write plugins for [OADM](https://oadm.app/) (Open AXIS Device Management): entries of the device context menu, pages
of their own and event rule actions. Every package comes with each OADM release, in its version.

```sh
dotnet new install Oadm.Plugin.Templates
dotnet new oadm-task -n DoorBell        # or oadm-page, oadm-action
cd DoorBell
dotnet tool restore
dotnet build
dotnet test
dotnet oadm dev start                   # an OADM server for this plugin
```

| Package | What it is |
|---|---|
| `Oadm.Sdk` | The plugin SDK: tasks, core plugins, event rule actions, devices, VAPIX |
| `Oadm.Sdk.Client` | Dialogs, pages, toolbar entries and the shared controls of the client |
| `Oadm.Sdk.Testing` | A fake camera, device and the server's contexts for plugin tests |
| `Oadm.Plugin.Sdk` | The project SDK: `<Project Sdk="Oadm.Plugin.Sdk/x.y.z">` |
| `Oadm.Plugin.Templates` | `dotnet new oadm-task`, `oadm-page`, `oadm-action` |
| `Oadm.Tools` | The `oadm` command: development server, deploy, debug, `.oadmplugin` files, licenses |

Guide: https://oadm.app/dev/plugin-sdk/ · SDK reference: https://oadm.app/api/ · Source: https://github.com/Cacsjep/oadm
