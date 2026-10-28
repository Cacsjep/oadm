# Application management (`oadm.app-management`)

Core plugin with the rail page **Applications** (group Maintenance, icon `app`): the applications (ACAP) of the chosen
devices (devices above, the selected devices' applications below), the Applications plugin's actions for many rows at once, and per application its
log and settings. Free, off by default. Spec: `docs/spec/app-management.md`.

## Parts

- `AppManagementPlugin` (server): permission `view` ("View applications", Administrator and Operator); every method
  needs it. Methods (`AppManagementMethods`): `read`, `getState`, `getRows` (pages of 1,000 devices), `run`, `follow`,
  `getLog`, `getSettings`. Events to the caller's session only: `rows` (whole devices, removed ids), `state` (progress).
- `Reading/AppReadService`: per login session (`ICallContext.SessionId`, at most 32 sessions, least recently used
  dropped) the rows of the chosen devices, in memory only. A read is a job of the caller (`IPluginJobs.Start`) through the
  caller's view, 16 devices at once over every session, 15 s per device. A device that is not Ok is not contacted
  ("Not checked: <status>"); one without `application` 1.x in its cached API list neither ("Applications cannot be
  managed on this device"); a failed read keeps the device with the error.
- `Shared/AppManagementModels.cs`: requests, replies, texts, `AppLicenses` (missing, invalid, ends within 5 days).
- Client (`Oadm.Plugins.AppManagement.Client`): `ApplicationsViewModel` (device choice, filters, the device selection, actions),
  `AppIndex` (the rows in one pass), `AppRows` (`AppRow`, `AppDeviceRow`), `AppRows`, `AppLogViewModel` / `AppLogWindow`,
  `AppSettingsViewModel` / `AppSettingsWindow`, `InstallAppsViewModel` / `InstallAppsWindow` (Install applications),
  `InstallLicensesViewModel` / `InstallLicensesWindow` (Install licenses).

## Reused code (linked source, never copied)

`plugins/Oadm.Plugins.Acap/ApplicationApiClient.cs` and `Shared/*.cs`: list.cgi and its parser, `EapReader` and
`AcapCompatibility.Preview` (the install check per device), `LicenseKeySet` and `LicenseKeyMatcher` (key files, the key
per device and application), `AcapPayload` (the
dialog's payload, parsed by its own `Parse` before a run), `AcapPermissions`, `AppLogReader`, `AppSettings`
(`TryNormalize` checks every settings field on the page). `plugins/Shared/CachedApiCheck.cs`, `ContextCall.cs`
(`CallDevices`), and on the client `SweepRows.cs` (`AddressSortKeys`).

## Device safety

The page never writes to a device. Start, Stop, Remove and Change settings are `oadm.acap` tasks started through
`ICallContext.Tasks` (the caller's device group), one run per application, after the permission check the task service
does (`task:oadm.acap:startStop`, `remove`, `settings`, `install` (+ `downgrade`), `license`), with all devices checked
before the first task. Install and Install licenses are the page's own dialogs: the files are read and every row checked
with the Applications plugin's rules on the client, the packages uploaded (`ICorePluginClientContext.UploadAsync`, SDK
0.7.0), then the server part starts the runs (Install: one run for the devices that fit, the task checks each again
before it uploads; Install licenses: one run per application with the keys of its devices). After the
page's tasks end (`ITaskRunner.IsActive`, every 2 s) the server part asks `ITaskRunner.GetOutcomeAsync` (SDK 0.7.0) how
each ended, keeps the messages of Failed and Done with warnings tasks per device and application
(`DeviceApps.TaskResults`, dropped by the next read the user starts) and reads the devices again; `AppRow.StatusText`
shows "<state>. <message>". The settings window offers **Open app page** for one device when list.cgi names a
`ConfigurationPage` and Web View is on (`ICorePluginClientContext.OpenDevicePage`, SDK 0.7.0). Setting values go only to
callers with `task:oadm.acap:settings`, travel only in the task payload and are never logged.

## Tests

`tests/Oadm.Plugins.AppManagement.Tests`: server reads, group scope, refusals, the actions' payloads (FakeCoreContext's
task runner), follow and read again, log and settings, the page's view model, a Perf test with 30,000 devices x 10
applications, headless screenshots (OADM_SCREENSHOT_DIR, OADM_TEST_APPEARANCE=Light for the light ones).
