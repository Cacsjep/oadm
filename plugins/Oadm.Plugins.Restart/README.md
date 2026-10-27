# Restart (`oadm.restart`)

Task plugin, server only, always on. Toolbar and context menu, no dialog. Task name "Restart device". Spec:
[Bundled plugins](../../docs/spec/bundled-plugins.md).

## Task steps

`restart.cgi`, then `basicdeviceinfo.cgi` every 5 s (10 s per request) until the device goes down and answers again
(3 min for both waits together).

| Step | What happens |
|---|---|
| Check device | `basicdeviceinfo.cgi`; no answer fails "The device does not answer. Nothing was changed.". Detail: model and AXIS OS |
| Send restart | `restart.cgi` |
| Wait for the device to go offline | polls until `basicdeviceinfo.cgi` stops answering; progress = elapsed / timeout |
| Wait for the device to come back | polls until it answers again ("Back after 42 s") |
| Verify device | compares the serial number; a different one ends the step (and the task) with a warning |

## Packaging

The reference for the bundled plugins that reference the SDK by hand: `Oadm.Plugins.Restart.csproj` holds the
`OadmPluginId` / `OadmPluginOutputDir` properties and the `OadmDeployPlugin` target (copy to
`artifacts/plugins/oadm.restart/`). Layout, loading and deployment: [plugins/README.md](../README.md#project-layout).

## Device groups

The tasks run only on devices of the user's view and touch only their own device; no dialog, no query
([device groups](../../docs/spec/device-scope.md)). Tests: server `DeviceGroups/CanaryTests`, TaskService cases.
