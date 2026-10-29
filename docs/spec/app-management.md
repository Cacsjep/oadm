# Application management

Status: **built 2026-10-28**.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

One page with the applications (ACAP) of many devices: which apps run where, in which version, with which license, and
the actions of the Applications task plugin for the selected rows at once.

Core plugin `plugins/Oadm.Plugins.AppManagement` (+ `.Client`), id `oadm.app-management`, rail page **Applications**
(group Maintenance: the SDK has no Devices group; icon of the Applications context menu group), free, **off by default** (its own package, so the
always-on `oadm.acap` stays as it is). The context menu dialog **Applications (ACAP)** stays unchanged.

## Rules that never break

1. **The page changes nothing itself.** Every change (install, upgrade, start, stop, remove, license keys) is a task of
   the Applications plugin `oadm.acap` with its payload, started through `ITaskRunner`: its checks (API, architecture,
   AXIS OS range, root apps, downgrade, signing, license key matched by MAC address, verification after each change), its
   permissions (`task:oadm.acap:*`) and the device groups apply exactly as from the dialog. The page needs `oadm.acap`'s
   task to be available (it is always on); the actions the role lacks are hidden.
2. **The same checks before the task, from the same code.** The install preview uses the Applications plugin's own
   `.eap` reader and compatibility rules (`plugins/Oadm.Plugins.Acap/Shared`: `EapPackage`, `AcapCompatibility`,
   `LicenseKeys`), linked as source, never a copy or a second implementation: architecture (processor), AXIS OS range
   and the manifest schema's minimum, root apps (refused on AXIS OS 12+), signed-only devices (`AllowUnsigned` false,
   and AXIS OS 13+ where the setting is gone and only signed apps install), another app with the same name, vendor change,
   install kind and downgrade, license keys by MAC address, application id and expiry. A rule added there applies to the
   dialog, the page and the task at once.
3. **Choose devices first.** Nothing is read before the user chooses devices (`PageDeviceChoice` first in the toolbar,
   `plugins/README.md` "Choose devices first"); only devices of the user's device group.
4. **Read only on demand.** The installed applications are read when the user chooses devices or clicks **Refresh**, and
   after a task of the page ends on a device (that device only). No reads in polling; the results live in server memory
   per user session (gone after a server restart), never in the database.
5. **Scale.** 30,000 devices: reads as a plugin job (`IPluginJobs`), at most 16 devices at once, 15 s per device,
   progress row and Cancel; rows streamed to the page in batches; both grids virtualized; summaries, filters and search
   one pass over the rows.

## Reading

- Per device: the cached API list must offer `application` 1.x (else the row "Applications cannot be managed on this
  device", no request), then `applications/list.cgi`, then best effort (null when not offered) `config.cgi` AllowUnsigned
  and the embedded development version parameter for the install dialog's checks, these two at the same time (three
  read requests in one slot and one timeout per device). Parsing and error texts are the Applications
  plugin's (`ApplicationApiClient`, list parser), linked as source (`plugins/Shared`), not copied.
- Devices that are not Ok: not read, "Not checked: <status>" like Compliance.
- A failed read keeps the device in the list with the error in its status cell.
- Each application also carries its own page as list.cgi names it (`ConfigurationPage`, e.g.
  `local/ax_msf/redirect.html`; `InstalledApplication.ConfigurationPath` = "/" + it, null without one or when it would
  leave the device: the SDK's one check `DevicePaths.ToDevicePath` / `IsOnDevice`, as for Open app page and Web View's
  Custom path: no "//" anywhere (no scheme or host), no "\\", no ".." segment also percent-encoded, no spaces).

## Page

- Split view side by side (chosen 2026-10-28 over tabs By application / By device, a grouped grid, a matrix and a split
  above and below): two cards, **Devices** left and **Apps** right (the card title like the Permissions page's, "Apps"
  so the page title is not repeated and everything fits at full HD), a vertical splitter between them (11:9; each card
  keeps at least 20 % of the width). No toolbar above them: each card has one header row with its title first, level
  with the other (decision 2026-10-28, "all in one row"):
  - left: device choice, **Refresh**, **Install**; right of them **Hide AXIS apps** (on by default, one box: hides
    `Bundled=Yes`, the apps that come with AXIS OS, and every app with the word "AXIS" in its name, any case; it changes
    the device counts too), the status filter (All, Not running, License missing, License expires soon (5 days), Read
    failed) and **Search devices** (app, vendor, address, model, MAC address, AXIS OS, SoC, architecture, version); the
    read's progress and error below it only while they apply;
  - right: **Install licenses**, **Start**, **Stop**, **Remove** (what does not fit goes into the More menu) and
    **Search applications**.
  - **Devices** (left): one row per device with shown rows after the filters, in address order: address, model, MAC
    address, AXIS OS, SoC (the SoC column's text, `DeviceTexts.Soc`), architecture (`IDeviceInfo.Soc` / `Architecture`,
    SDK 0.7.0), Applications (the shown count, right-aligned), Status: a running task ("Stopping"), the read error (Not
    checked as a warning), "No applications", "All running" (ok) or the problems ("1 task problem, 1 license problem,
    1 license ends soon, 2 not running"; error for a failed task or a missing or invalid license, warning for licenses
    that end soon and tasks with warnings). Problems sort first.
  - **Applications** (right): the applications of the selected devices, one row per device and application: address,
    application, version, status (Running, Stopped, Idle), license (the end date of a valid license, "Valid", "Not
    required", "Missing", "Invalid"), vendor, ⋯. Application, Status and Vendor share the free width. Its header row:
    **Install licenses**, **Start**, **Stop**, **Remove** (they act on the selected application rows; what does not fit
    goes into the More menu), right its own search (address, application, vendor, version; it keeps the selection of
    the rows it still shows). Empty: "Select devices to see their applications.", "No applications" or "No application
    matches the filters.".
    With several devices selected the rows are grouped by device (a collapsible header with the address, one group per
    device, the Address column hidden).
  - The selected devices and application rows stay selected when rows change (a task's end reads the device again,
    pushed rows, a filter); rows no longer shown leave the selection. The grid reset of a rebuild is not a user choice.
  - The device columns share the width (star sizes, Status the widest), so a wide window has no empty space at the right.
- Selection: Install acts on the devices selected in Devices (application rows in Apps never narrow it); Install
  licenses, Start, Stop and Remove on the selected application rows.
- Actions (command buttons, each with its texts and confirmations):
  - **Install** opens the page's own dialog **Install applications** (chosen 2026-10-28 over the Applications plugin's
    dialog through `RunTaskAsync`: "the same view again"): fixed size, no card. **Choose files** (`ui:FileRow`): one
    `.eap`, or one per architecture of the same application and version (`AcapCompatibility.CheckPackageSet`, refused at
    the file row before any upload). A virtualized grid of the target devices: address, model, AXIS OS, SoC,
    architecture, **Result**. Once files are chosen every device is checked off the UI thread with the Applications
    plugin's own preview (`AcapCompatibility.Preview`, linked source, also the Applications dialog's): the package of its
    architecture ("No package for mips."), architecture, AXIS OS range and manifest schema, root apps, signed-only on
    AXIS OS 13+, another app with the same name, vendor change, install kind and downgrade. The facts are what the page
    read (architecture, AXIS OS, installed applications, AllowUnsigned, the embedded development version); the task
    checks them again with fresh values before it uploads. Devices that were not read show their row's error.
    Devices that do not fit are skipped; the footer says "4 of 5 devices can install." **Start after install** (on),
    **Allow downgrade** (always shown to roles with `task:oadm.acap:downgrade`, never to others).
    **Install** (primary, its tooltip says why it is off) uploads the files (`ICorePluginClientContext.UploadAsync`, SDK
    0.7.0, one progress row, Cancel stops it) and asks the server part to start one `oadm.acap` Install run for the
    devices that fit (`RunRequest.Install`: the uploaded files with their architectures, version, options).
  - **Install licenses** opens the page's own dialog **Install licenses** for the selected application rows: fixed size,
    no card. **Choose files**: key files or ZIPs of them (`LicenseKeySet.ReadFiles`). A virtualized grid: address, model,
    application, license, **Result**: the key of that device and application with the Applications plugin's own rules
    (`LicenseKeyMatcher`, linked source, also the Applications dialog's: MAC address, application id, expiry; "No license
    key for this device.", "Key expired", "Other application", "Other version", "Several keys"; of several keys of a device
    for the application the best counts: not expired, fitting the installed version, the latest end date first, so an
    expired key never hides a valid one; the payload carries exactly the usable keys of that verdict); applications that need no license are
    skipped ("This application needs no license."). Files that are not keys show as one warning below the file row.
    **Install licenses** asks the server part to start one `oadm.acap` license run per application with the keys of its
    devices (`RunItem.LicenseKeys`, `LicenseApplicationId`).
  - The Applications plugin's dialog keeps its start view "license" / "license:<package name>" (SDK 0.7.0,
    `ITaskDialogContext.StartView`) and its package preview's **Manifest** value; the page no longer opens it.
  - **Start**, **Stop**, **Remove** (confirmed with the device and application count, red; preinstalled apps are
    refused by the task as today): the page's server part starts `oadm.acap` tasks through `ITaskRunner` with the
    dialog's payload (`AcapModels`, linked source) for the selected rows' devices, grouped per application, as the
    calling user (`ICallContext.Tasks`, permissions checked like `TaskService.Run`). The same server method (`run`)
    starts Install (`task:oadm.acap:install`, with Allow downgrade also `downgrade`; one application per run, the whole
    device follows the task) and Install licenses (`task:oadm.acap:license`); the Applications plugin's `AcapPayload.Parse`
    checks every payload before the first task. The older `follow` method (tasks the Applications dialog started) stays,
    the page no longer calls it.
  - The page shows the started tasks' progress (Installing, Starting, ...) and reads the device again when its task ends.
    The activity names its applications (`DeviceApps.ActivityApplications`): only their rows show it; empty (Install, a
    task without an application) means every row of the device. The device row shows it too.
  - **The end of a task in its row**: when all tasks of a device ended, the server part asks the task runner how each
    ended (`ITaskRunner.GetOutcomeAsync`, SDK 0.7.0, as the calling user: device groups apply) and keeps the message of
    each Failed and Done with warnings task for its device and application (null application: an Install task, it concerns
    the whole device). The read after the task carries them (`DeviceApps.TaskResults`); the next
    read the user starts (Refresh, a new choice) drops them. The row of that application (all rows of the
    device for an unknown application; the "No applications" row) shows the fresh state followed by the task's message
    in its Status cell, one text: "Stopped. The application did not start. Nothing was changed." (an error icon for
    Failed, a warning icon for Done with warnings; the grid is `wrapRows`, so two lines). Such rows sort with the problems.
    No tooltip, no extra column. Done and Cancelled tasks leave nothing.

## Log and settings (stage 2)

The device's own Apps page offers both in an application's menu (⋯); the page offers them per row. A row's ⋯ menu shows
**Settings** when the application registered settings on the row's device, else **Open app page** when it has its own
page (list.cgi `ConfigurationPage`; greyed out while Web View is off, tooltip "Turn on Web View on the Plugins page"),
and always **Show log**. Whether it has settings is asked when the
menu first opens (the settings read for that one device, at most 5 s, the ⋯ button disabled meanwhile) and kept until
the device is read again; when it cannot be asked, Settings stays (the window then says why).

- **Log** (a row's ⋯ **Show log**): `GET /axis-cgi/admin/systemlog.cgi?appname=<Name>` (the device's log lines of
  that application only; read only, through the page's server part on the device's route). Shown in a window with the
  shared log view (newest last, Refresh, Copy, search, at most 2 MB / 10,000 lines kept, each line cut to 8 KB while it is read, the read stopped after 64 M characters); a device without lines: "No log
  lines from this application." The Level column is narrow and shows only the status icon (`ui:StatusChip` without text:
  error for ERR/ERROR/CRIT/ALERT/EMERG, warning for WARNING/WARN, info for INFO/NOTICE; DEBUG and others no icon) with
  the level as its tooltip; the search still finds the level text. Seen on AXIS P3265-V, AXIS OS 12.11 (2026-10-28).
- **Settings** (a row's ⋯ **Settings**: the row's device; when the row is selected, every selected device with that
  application): the
  parameters the application registered (`manifest.json` `acapPackageConf.configuration.paramConfig`, read in the app
  with `ax_parameter_*`; developer.axis.com/acap/get-started/create-your-first-acap-application), `param.cgi?action=listdefinitions&listformat=xmlschema&group=root.<Name>`
  (group = the application's `Name`; most applications register none: "This application has no settings here. Open its
  own page on the device."). Then, when the application lists a `ConfigurationPage` and Web View (`oadm.web-view`) is
  on: one device: the link **Open app page** opens that device's Web View tab on the page
  (`ICorePluginClientContext.OpenDevicePage(deviceId, path)`, SDK 0.7.0; the window closes; when the host opens nothing
  the window says "Web View cannot open this device."); several devices: no link, "Open it on one device."; Web View off: the link greyed out and "Turn on Web View on the
  Plugins page." below it. Without a
  `ConfigurationPage`: the text only. Each parameter: nice name, value,
  type from the definition (text, number with range, list of values, yes/no); parameters the definition marks `hidden`
  are not shown (the device page hides them too); password types are never read back or shown (empty field, "Unchanged").
  Several devices: one row per parameter with the value of most devices and "differs on 3".
- **Change settings** = a task of `oadm.acap`, new action **Change settings** (`{action: "settings", application,
  values: {name: value}}`, payload in memory only, values never logged; the server part refuses a run with more than
  one application, "Change the settings of one application at a time. Nothing was changed."): Check compatibility, Read installed
  applications (the application must be installed), Read settings (the definitions, every name must exist and every value
  fit its type and range, else Failed "Nothing was changed"), Change settings (`param.cgi?action=update`, all values in
  one request), Verify settings (read back, passwords skipped). New permission `task:oadm.acap:settings` ("Change
  application settings", Administrator and Operator by default). Task name "Change settings of <app>". Only the changed
  values are sent.

## Several packages per install (stage 2)

The Applications (ACAP) dialog and task take several `.eap` files of the same application at once, one per architecture
(e.g. the `aarch64` and the `armv7hf` build), like Upgrade firmware takes one file per model:

- All files must name the same application (`appName`) and version; two files for the same architecture, or a file of
  another application, are refused in the dialog before any upload. A package without an architecture (`all`,
  `noarch`, none) fits every device and cannot be combined with others.
- Each device gets the file of its architecture (basicdeviceinfo `Architecture`); the preview shows per device which file
  ("aarch64 build") and every other check as today; a device without a fitting file is skipped with the reason "No
  package for armv7hf." (Done with warnings, nothing changed).
- The payload carries the files (`files: [{fileId, sha256, architecture}]`, the old single `fileId` still read); the task
  picks the file again from a fresh basicdeviceinfo before it uploads.

## Settings values are treated as secret

Shared settings are often server addresses, thresholds or keys (pre-shared keys are plain text parameters). So the task
log, the audit log and task names never contain setting values, only names; the page shows values only to users with
**Change application settings**; values travel only in the payload (memory only).

## Permissions

The page itself: permission `oadm.app-management:view` ("View applications"; Administrator, Operator by default).
Actions: the Applications plugin's task permissions; a role without them sees the page read only.

## Tests

- The page's preview and the task give the same verdict for the same package and device (shared rules, one test per
  rule through the page).

- No read before a device choice; only devices of the caller's group; not Ok devices not read.
- Every action starts `oadm.acap` tasks with the dialog's payload (FakeTaskRunner), never a device write of its own.
- The device rows and their summaries, the right grid of the selected devices and its search, the selection kept over a change; Hide
  AXIS apps (preinstalled and named AXIS); status filters; search.
- A failed or warning task's message in its rows until the next read the user starts; a group user learns only the ends
  of tasks on the group's devices; Open app page for one device only.
- Install applications and Install licenses: the verdict per row from the shared rules (several files per architecture,
  downgrade, signed-only, not read devices; key found, expired, other application, no license needed), the run request
  sent (files, version, options; keys per application), the permissions (install, downgrade, license) checked by the
  server part before any task; 30,000 devices checked and matched within a Perf budget; headless screenshots of both.
- 30,000 devices with 10 applications each (300,000 rows): filter, search, summary and select all within a Perf budget.
- Headless screenshots of the page with two selected devices, dark and light.
