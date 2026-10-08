# Oadm.Plugins.Restart

First task plugin (`oadm.restart`). Toolbar + context menu, no dialog. Calls `restart.cgi`, then
polls `basicdeviceinfo.cgi` every 5 s until the device goes down and answers again
(3 min for both waits together).

Steps (one per request and per wait):

| Step | What happens |
|---|---|
| Check device | `basicdeviceinfo.cgi`; fails "Nothing was changed" when the device does not answer. Detail: model and AXIS OS |
| Send restart | `restart.cgi` |
| Wait for the device to go offline | polls until `basicdeviceinfo.cgi` stops answering; progress = elapsed / timeout |
| Wait for the device to come back | polls until it answers again |
| Verify device | compares the serial number; a different one ends the step (and the task) with a warning |

## Packaging

- Output assembly: `Oadm.Plugins.Restart.Server.dll` (the loader picks up `*.Server.dll`).
- `plugin.json` (id, version, minSdkVersion) is copied next to it.
- `Oadm.Sdk` is referenced with `Private=false` / `ExcludeAssets=runtime`: the SDK and its
  dependencies are shared from the host and never land in the plugin folder.

## Development deployment

Every build (also `dotnet build Oadm.sln`) copies the output to

```
<repo>/artifacts/plugins/oadm.restart/
```

The server finds that folder with `Oadm.Core.Plugins.PluginPaths.Development()` (walks up from the
server's base directory to `Oadm.sln`). Installed plugins live in `<datafolder>/plugins/<id>/`
(`PluginPaths.Installed(dataFolder)`) with the same layout. The loader reads plugin assemblies
into memory, so a rebuild works while the server runs. Restart the server to load the new build.

To do the same in another plugin project, copy the `OadmPluginId` / `OadmPluginOutputDir`
properties and the `OadmDeployPlugin` target from the `.csproj`.
