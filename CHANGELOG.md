# Changelog

Notable changes to OADM, newest first. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions:
[Semantic Versioning](https://semver.org/). New changes go under **Unreleased**; a release renames that section to its
version and date, and the release workflow copies it into the GitHub release.

## [Unreleased]

### Added

- Device list: an **Architecture** column ("aarch64", "armv7hf"), off by default; turn it on in the column chooser.
  Also in the search and the export.
- Device groups (in work): a user can be limited to the devices of a group (sites, tags, single devices). After the
  upgrade every user keeps all devices. Such a user sees and runs tasks only on the devices of the group; a queued task
  whose device left the group fails before it starts.
- Device groups: renaming a group keeps its users' pages open; the DHCP server page shows a group's users the leases of
  their devices by MAC address only.
- Device groups: a user with a group finds and adds devices only at the sites of the group, and only with the passwords
  they enter (never the credential list). The waiting list shows each user the devices of their sites; devices waiting
  without a site only to the user who kept them.
- Device groups: device triggers, Live View views and dashboards belong to the group of the user who made them; a user
  with a group sees and changes only these, with the devices of the group. Image Health Dashboard, Edge storage and the
  Action Rule Editor show each group its own table.
- Device groups: a device that leaves a group disappears at once from the group's tables, dashboard values and scan
  results, and an export of its recordings ends. An administrator can save a group's view or dashboard after one of its
  devices left the group: the save leaves that device out and says so. The same for a group's device trigger.
- Hardening scan: every user with the same devices may stop a running scan, not only the user who started it. The scan
  keeps running when the user who started it logs out.
- Device groups: the dashboards of a user with a group offer only the group's own device triggers, and their live values
  stop at once when the user's devices change.
- Device groups: a file you upload for a task (firmware, an application, a backup) can be used only by you, in the same
  login. Reports and other results of plugins are gone once your devices change.
- Users page: a **Device groups** card with New device group, Edit and Delete; the group editor picks sites, tags and
  single devices and shows the count. Add user and Edit user have a **Devices** field; **Your role** shows your devices.
- Device groups: the device list, live video, the web interface and the Sites page show only the devices and sites of
  the user's group; live video stops when its device leaves the group.
- Device groups: users of a group tag only their devices and never add or remove a tag a group uses; compliance checks
  belong to a group and run only on its devices.
- Device groups: event rules and the event log are only for users with all devices. Rules run on all devices as before.
- Device groups: administrators choose the device group of a compliance check, and its card names it. Deleting a tag or
  removing a site a group uses names the group first. Users with a group do not see Event rules, and Add is off when
  their group has no site.
- Device groups: certificates from the PKI and EST of a device in a group name only its stored address and its Axis
  name, never a host name or address set on the camera.
- Plugin SDK 0.4.0: plugin methods get the user's call (`ICallContext`) with the devices that user may reach; plugins
  built with SDK 0.3.0 keep working for users with all devices.
- Device groups: users with a group see only plugin pages built for device groups, and plugin pages show them only
  their own devices, reports and live updates.
- Plugin SDK 0.4.0: `Oadm.Sdk.Testing.PluginCanary` checks that every method of a plugin refuses devices outside the
  user's group.
- Device groups: the task plugins and VAPIX Commander work for users with a group, on the devices of the
  group. Assign IP address and Network settings say only "In use" for an address of a device outside the group, and
  never set an address another device managed by OADM uses.
- Device groups: VAPIX Commander saved commands are changed only by users with all devices; others run a saved command
  only with the passwords they enter.
- Device groups: users with a group see the PKI and EST server pages read only, with the certificates and enrollments of
  their own devices. Certificates of a device such a user added name only its IP addresses, never its host name.
- Device groups: users with a group see the NTP server and DHCP server pages read only, with the requests and leases of
  their own devices. Snapshot report, Hardening scan, Edge Recording Export, I/O Monitor, Metadata Monitor and Web View
  work only on their devices; a monitor stops when its device leaves the group. System report is for users with all
  devices.
- Device groups: a task waiting in the queue fails when the user who started it was disabled or deleted, or no longer
  has the device. A device on the waiting list whose user was deleted waits for Try now.
- Device groups: plugins made for an older OADM work only for users with all devices; for other users their tasks are
  not in the menu.
- Device groups: the users of the same devices share one hardening scan, and any of them can stop it. Reports, exports
  and other results of plugins belong to the login that started them. A user runs at most 50 of them per plugin at a
  time.

### Changed

- The **Users** page is now **Permissions**: users and device groups side by side (on small screens one below the
  other), the roles below. The user list no longer shows the last login.
- Remove, Refresh, Check now, tagging, the RTSP port, the first password, the queries of task dialogs and the other
  device actions do nothing when a chosen device was removed meanwhile and say "1 device is no longer available."
  (before, Remove and Refresh skipped it).

## [4.1.0] - 2026-10-23

### Added

- **OADM PRO**: Settings has a **License** card with the server ID, the license state, **Import license** and **Remove
  license**, and links to start a 30-day trial or activate a license on oadm.app. OADM itself works the same without a
  license.
- Plugins: OADM PRO plugins are listed with an **OADM PRO** chip; without an active license they stay off and
  **Activate** opens Settings > License. They stop when the license ends and run again with a new one.
- **EST server** (OADM PRO, off by default): cameras with AXIS OS 12.10 or later get their certificates for HTTPS,
  IEEE 802.1X, MQTT and RTSPS from the OADM CA and renew them by themselves. Only cameras OADM manages get certificates;
  they sign in with their Axis device ID, and a renewal only with a certificate the OADM CA issued for that camera.
- EST server at sites: cameras at a site get and renew their certificates through their site node. The node app asks to
  allow TCP 9454 when the first camera there is set up; the EST server page shows the sites whose node has not allowed it
  yet.
- EST server page: **Listen on** chooses the network interface the cameras reach the server on, settings grouped under
  **General** and **Profiles**; **Enrollments** with a search, **Delete all** and **View certificate** (also a double
  click on a row: validity, issued to and by, key, serial number, fingerprint, uses and names); **Not renewed** lists the
  certificates a camera did not renew in time (cameras renew halfway through the validity).
- **Certificate enrollment** (Security): lists the enrollment profiles of the chosen camera (also those of another EST
  server) with their status; **Set up** sets them up, **Remove** removes the selected profile from every selected camera.
  The dialog stays open while it runs and shows the step it is on, then the result with the camera's message. Cameras
  with IEEE 802.1X on keep their network access certificate unless you choose to replace it.
- Events **Certificate enrolled**, **Enrollment refused** and **Certificate not renewed** for the event rules.
- Settings: **Create support report** (card **Support**, administrators) saves one ZIP with the server's and this
  computer's log files and a summary of the server, for a support request. No passwords are included.
- Plugin SDK 0.3.0 (OADM 4.1): OADM PRO, plugin services, `ICorePluginContext.Records` (`IPluginRecords`, also
  `GetAsync`) for data that grows with the devices, `IPluginNetwork.ListenTcpAsync` with the addresses of one interface
  (`InterfaceOptions.All`, `AllId`, `AllLabel`, `IsAll`), `ITaskDialogContext.CanRunTasks` and `RunTaskAsync` (a task
  dialog starts its task and follows it while it stays open). Plugins built with the 4.1 packages need OADM 4.1 or later.

### Changed

- A device's new HTTPS certificate from the OADM CA (renewed by the camera or by PKI) no longer shows **Certificate
  changed**: OADM accepts a certificate the OADM CA issued directly for the same device. Any other new certificate still
  does.
- Security: OADM pins a renewed HTTPS certificate only when it names exactly one camera, this one; PKI takes only IP
  addresses from what a device reports and never puts another camera's `axis-<serial>` name into a certificate.
- The server ID shows only in Settings > **License**, no longer in the Server card or on the Plugins page.
- Turning off **Allow Basic login over HTTP** now also ends event streams and live videos that logged in with Basic at
  once; turning it on or off no longer stops running tasks.
- Updated libraries: MailKit 4.18.1, BouncyCastle 2.7.0.

### Fixed

- The OADM Site Node tries to reach the server every 10 s at most (was 60 s) and logs a repeated failure only once.
- Configure devices: a host name setting of spaces only or with a tab is refused.

## [4.0.2] - 2026-10-22

### Added

- Settings: **Allow Basic login over HTTP** (card **Security**, off by default) for devices without HTTPS that accept only
  Basic login. OADM then sends their password unencrypted, also for live view and event streams.

### Fixed

- A device without HTTPS that accepts only Basic login says "The device accepts only Basic login over HTTP. Turn on HTTPS
  on the device, or allow Basic login over HTTP in Settings." in Log in, on the add devices page, in live view and in the
  device list (Status "Basic login only") instead of "The user name or password is wrong."
- Configure devices: a host name setting with several names separated by spaces (NTP servers such as
  `10.0.0.17 ts1.univie.ac.at`) no longer fails the check.

## [4.0.0] - 2026-10-22

### Added

- **Sites**: OADM manages devices in networks the server cannot reach, through an **OADM Site Node** at each site. The
  node is a small service with an app for Windows and Linux (x64 and ARM64): `OADM-SiteNode-<version>-win-x64.msi`
  (`SERVER=<address>` for silent installs) and the packages `oadm-site-node` and `oadm-site-node-app`. It always connects
  out to the server (TCP 5081) and never listens, so no port has to be opened at the site.
- Sites: turn on **Accept site nodes** on the new **Sites** page, enter the server's address in the node app and bind the
  node with **Bind** and the pairing code the app shows (a new code every 10 minutes). The Sites page lists every site
  with its state, devices, response time and version; rename, replace and remove sites there.
- Sites: Discovery, Network range, Add manually, Import from file (column **Site**), Import from ADM and the waiting list
  find devices through every site. The device list has a **Site** column; devices that move between sites follow; devices
  at a site open in Web View.
- Sites: devices of an offline site show **Site offline**. OADM does not poll them, and tasks, plugin pages, Log in, Set
  password, the RTSP port, live view and Web View refuse them; compliance says Not checked. New events "Site offline"
  per site and "Any site offline", and the placeholder {site}.
- Sites: the node app shows the link to the server at a glance (purple: ready to bind, green: connected), the pairing
  code with a copy button, the network cards the node reaches and its ports in one table.
- NTP server: serves the devices at all sites or at the sites you choose (**Sites**); a site you no longer choose gets
  its port removed in the node app again.
- Plugins page: which plugins work at sites and the ports they need. Assign IP address refuses addresses outside a site's
  networks.
- Roles: a **start page** per role, the page its users see first after a login (Devices, Compliance, Sites, Event rules,
  a plugin page or Logs); the Administrator role has one too.

### Changed

- Plugin SDK 0.2.0: network checks routed like the device, relays at sites, site support and test fakes. Plugins built
  with the 4.0 packages need OADM 4.0 or later; plugins built for 0.1.0 still load.
- Plugins: the plugin SDK warns when a plugin opens its own network connections to devices; use the connections OADM
  offers instead.
- The server opens TCP 5081 for site nodes in the Windows firewall only while **Accept site nodes** is on.
- Applications (ACAP): the window is wider; while you install an application or a license, the installed applications
  stay on the left and the install card sits on the right.

### Fixed

- Applications (ACAP): installing an application failed with "The package is too large or the device's storage is full.
  (error 3)" on AXIS OS 12 devices.

## [3.6.2] - 2026-10-21

### Added

- VAPIX Commander: a **Siren and light** category for AXIS strobes and sirens: get capabilities, status, profiles and the
  last health check; start a siren, a light or a profile, stop them, add, remove and upload profiles, run a health check
  and turn maintenance mode on or off.
- VAPIX Commander: commands can send a file (up to 1 MB). **Choose file** below the command; a saved command keeps the
  file. In your own commands use the body type **multipart** and a field of the type **file**.

### Fixed

- Resize Issue: When OADM was minimized, window restore was not correct, therefore it was afterwards not return fullscreen.

## [3.6.0] - 2026-10-19

### Added

- **Compliance** page below Devices: which devices need attention at first glance. It is off until you click **Enable
  Compliance Checks** on the page. A count card per check, the tabs
  Problems, Warnings and All, and one column per check. Out of the box it shows the devices OADM cannot reach or log in
  to. Add your own checks: Certificate (expired or fewer than N days left), Firmware (at least a version), Application
  running, Matches configuration file (a file from Create configuration file or AXIS Device Manager) and SD card (with
  Edge storage on). Each check runs on the devices or tags you choose, counts as Problem or Warning and is an event for
  the event rules, so a failing check can send an email.
- Devices list: a **Compliance** column (turn it on with Choose columns), and **Show each compliance check** in the toolbar
  for one column per check.
- Roles: **See compliance** and **Change compliance checks**.
- Plugin SDK: compliance check types (`IComplianceCheckType`), so plugins can add their own kinds of checks.

### Changed

- Count cards (Edge storage, Image Health Dashboard and others) have the card background like every other card.
- Add devices: devices OADM logged in to are checked by themselves, so **Add** works right away; a device you uncheck
  stays unchecked. The check box in the list header checks or unchecks all of them (instead of **Select all
  authenticated**). The scan status and the counts share one line. An import offers **Add 4 now, 2 later** next to
  **Add** for devices that do not answer yet (instead of the "Keep devices that do not answer" check box).

## [3.5.1] - 2026-10-18

### Changed

- Select fields and number fields show the accent border while they have the focus, like text fields.

### Fixed

- Dialogs open on the monitor of the window they belong to. Before, they always opened on the main monitor when the main
  window was on another one. A plugin page opened with "Open in new window" opens on the monitor of the main window.
- The mouse wheel over a select field or a number field scrolls the page. Before, scrolling a long form (event rule
  actions, New rule of the Action Rule Editor) changed the chosen action or number as soon as the field passed under the
  pointer.

## [3.5.0] - 2026-10-17

### Added

- **Send TCP message**: a new event rule action that sends a message to a host and port, for building, alarm and video
  systems that listen on a TCP port. Text, JSON, XML or hex bytes with the event's values, an optional line end and TLS,
  and optionally waits for an answer that must contain a given text.
- **Licenses for commercial plugins**: plugin makers can sell plugins without a license system of their own. The Plugins
  page shows this server's ID, the license state of every plugin that needs one and lets administrators enter or remove
  a license key. The Settings page shows the server ID too. `oadm license keys` and `oadm license sign` make the
  vendor's keys and the license keys; the plugin reads the result (valid, trial, features) and decides what runs.
- **Plugin SDK on NuGet**: plugins are written with the .NET SDK alone, no checkout of OADM needed.
  `dotnet new oadm-task`, `oadm-page` or `oadm-action` creates a working plugin with its tests (the tutorials' plugins);
  the `oadm` command (`dotnet tool install -g Oadm.Tools`) runs a development server with the plugin and restarts it after
  every build, opens the client against it, puts the plugin into the installed OADM or runs the installed server with it
  for debugging (one administrator prompt), writes the `.oadmplugin` file and makes license keys. Packages: `Oadm.Sdk`,
  `Oadm.Sdk.Client`, `Oadm.Sdk.Testing`, `Oadm.Plugin.Sdk`, `Oadm.Plugin.Templates`, `Oadm.Tools`, in the version of each
  release. Guide on the website: Developers > Develop with the .NET SDK.
- **Portable server and client**: every release also has `OADM-Server-<version>-<platform>.zip` and
  `OADM-Client-<version>-<platform>.zip` (run without installing; the development server uses them).
  `--print-folders` prints where a server or client keeps its data and plugins.
- **Server log on the Logs page**: the switch Server, Client, Audit. The server's log shows 200 entries a page with level
  filter and search; the client log pages the same way. Other roles than administrators need the new permission "See the
  server log". Failed event rule actions are written to the server log with their reason.

### Changed

- Plugins load whatever SDK version they were built against as long as their `minSdkVersion` fits: the SDK, Avalonia and
  the other shared assemblies always come from OADM. The client no longer locks a plugin's file, so a plugin can be
  rebuilt while the client runs.
- Settings: "Full refresh interval (min)" is now called **Read device details every (min)**.
- Action Rule Editor: grouping by the rules' event is called **Event**, like the column and the dialogs.
- `manage license keys|sign` runs the license commands of the `oadm` tool (`tools/Oadm.Tools`). The
  tutorials' plugins write their `plugin.json` from their project file.

### Fixed

- Add devices: a **Retry** of a login at the moment a search ended lost the results of the logins it starts on the other
  devices of the page.

## [3.4.0] - 2026-10-17

### Added

- **Devices that are off are added later**: an import (from a file or from ADM) keeps the devices that do not answer
  and OADM adds each one once it comes online, with the login and tags of its line. The Devices page shows how many are
  waiting; the list shows the last try and its result and lets you try them now (rows say "Checking"), export them or
  remove them. A device that rejects every password waits for "Try now" instead of getting the same passwords again.
  Importing again marks lines that are waiting already. Settings: "Try waiting devices every (min)", 15 by default.
- **Gauge widget on dashboards**: a number on an arc from Min to Max, with the Number widget's decimals, unit, color and
  thresholds and a line width; the thresholds color the arc, the ticks follow Min and Max by themselves, and the marker
  glides to each new value.

### Changed

- **Import from file and Import from ADM** take up to 30,000 devices per file, as many as one server manages. The file
  size no longer gets in the way: long host names, passwords and tags fit.
- **Imports are faster**: 64 addresses are checked at a time, logins run 32 at a time, and an address that does not
  answer is given up after the scan timeout (1.5 s). 30,000 devices with 10 % off take about 12 to 15 minutes on a LAN.
- **Tags** can have up to 64 characters. Longer tags in an imported file are shortened to 64 instead of stopping the
  device from being added. Long tags in the device list end with "…".
- **Dashboards**: a new threshold takes a color no other threshold of the widget uses yet.
- **Settings**: "IP range scan timeout" is now "Scan timeout"; it also applies to the first contact of imported devices.

## [3.3.1] - 2026-10-16

### Fixed

- **Small and scaled screens** (e.g. Full HD at 125 %): windows and dialogs fit on the screen, toolbar buttons that do
  not fit go into a More menu (Edge storage, Snapshot report), the Edge Recording Export filters wrap to a second line,
  device and task columns stay readable and scroll sideways, and Settings shows one column on a narrow window. The main
  window can now be made as small as 1200 x 640.

## [3.3.0] - 2026-10-16

### Added

- **Dashboards** (Monitoring, off by default): live values of your device triggers and live video on saved dashboards.
  Drag an event onto the grid and choose the device and the value: On / Off widgets with an icon per state, Text, and
  Number with a unit and thresholds that color it. Values that may be old are dimmed with the reason. Shared by every
  user, like Live View. Copy a widget for other devices in one step, select several widgets to move them and change
  what they share, and add up to 16 cameras for live video.
- **Any device events**: Any device: unreachable, credentials required, certificate changed, certificate
  expires soon and certificate expired are on while at least one device has the status, with how many.
  For event rules and dashboards.

### Changed

- **Event rules**: the values of a device trigger are listed as placeholders of the rule's actions.
- **Live View**: in edit mode every camera has a thin grey frame and its remove button is red.

## [3.2.0] - 2026-10-16

### Added

- **Up to 30,000 devices per server.** One OADM server now manages up to 30,000 devices. An add that would go over the
  limit is refused whole with a message, and the add page stays open.
- **Choose the devices** on the pages that read many cameras: Action Rule Editor, Edge storage, Image Health Dashboard,
  Hardening scan and Snapshot report. Choose all devices, tags or single devices; the page starts with the devices
  selected on the Devices page, else with your last choice, and reads only those.
- Metadata Monitor and I/O Monitor choose their device in the device picker with search.
- The Devices page says **Loading devices** until the device list arrived, and the context menu says **Loading tasks**
  until the tasks arrived. Remove, starting tasks, export, Delete all and adding many devices show a progress row.

### Changed

- **Device status**: a device that does not answer within 3 s is asked once more before it shows Unreachable. 200 devices
  are checked at once, so 30,000 devices are checked every minute. If most devices stop answering (for example the
  network of the server is down), they show Unreachable after one round, about 8 minutes for 30,000.
- **Full refresh**: devices that do not answer are left out, a new firmware is read at once, and 64 devices are read at
  once.
- **Server memory**: 30,000 devices need about 300 MB instead of several GB while clients load the device list.
- **System report**: at most 50 devices at once.
- **Device triggers**: all triggers together watch at most 10,000 devices; devices that do not answer are not contacted
  until they answer again.
- **Hardening scan**: one **Scan** button scans the chosen devices (instead of Scan all and Scan selected).
- **View certificates** and **Delete certificates**: at most 5,000 devices at once.

### Fixed

- Tagging thousands of devices, sorting the device list and changing tags while it is grouped by tag no longer freeze
  the window when many rows are selected.

## [3.1.0] - 2026-10-15

### Added

- **Action Rule Editor** (rail page, Automation; off by default, turn it on under Plugins): the cameras' own action rules,
  recipients and schedules of every device in one page, grouped by what they do, and changed for many at once. Example:
  500 cameras that "Send notification through TCP" changed to "... while the rule is active" in one step, each camera
  keeping its own message.
  - **Edit**: only the checked parts change (name, on or off, event, schedule, once or while active, action, recipient,
    each setting); everything else stays as each camera has it.
  - **New rule**: one rule for many cameras, from the events they offer, with conditions, a schedule and the action's
    settings with their defaults.
  - **Copy to devices**, **Turn on**, **Turn off**, **Delete**, and **Undo** of every change for 30 days.
  - **Recipients** and **Schedules** tabs: new, edit, copy and delete; changing a recipient can take the rules that send
    to it along.
  - Works on AXIS OS 10, 11 and 12; passwords stay on the server.
  - Try a change on two or three cameras first, check it there, then apply it to the rest. See the
    [Action Rule Editor](https://oadm.app/tools/action-rule-editor/) page.

### Changed

- **VAPIX Commander**: read commands show only the values a camera has (no "(not found)" for WDR, audio output, zoom or
  the secure RTSP settings of cameras without them), and the RTSP settings read also works on AXIS OS 11.9.

## [3.0.8] - 2026-10-14

### Added

- **Import from ADM** also reads the newer export format of AXIS Device Manager (format version 2).

## [3.0.7] - 2026-10-14

### Added

- **Linux ARM64**: `oadm-server` and `oadm-client` packages for ARM64 (Raspberry Pi 4 and 5, ARM servers), with their own
  download button on the website.

### Fixed

- The release build stopped on a security advisory of a .NET library used for email and PDF reports; OADM now uses the
  fixed version.

## [3.0.6] - 2026-10-14

### Added

- **Upgrade firmware** tells you when an upgrade is not on the upgrade path Axis recommends ("Recommended path: the
  latest AXIS OS 11.11, then 12.6.85."). The upgrade still starts; when the device refuses it, the task says what to
  install first.
- **Import from ADM** (Add menu): the device export of AXIS Device Manager adds its devices with their logins and tags. A
  device is only added when it answers with the MAC address of the export; another device at that address gets no
  password. An export of several ADM servers asks first.

## [3.0.5] - 2026-10-14

### Added

- **Import from file** first shows the columns and an example file; **Choose file** opens the file dialog.

### Changed

- The changelog page on the website has smaller text, so more releases fit on the screen.

## [3.0.4] - 2026-10-14

### Added

- **Live View**: a click on a camera under **Cameras** (outside editing) opens it alone in its own tab.
- **Live View**: the camera under the mouse gets a thin purple frame.

### Fixed

- **Live video**: the client could crash while showing live video. Converting a picture wrote past its memory for widths
  that are not a multiple of 16 pixels.

## [3.0.3] - 2026-10-14

### Changed

- Your user name and **Log out** moved from the bottom of the navigation rail to the top right of the window, next to the
  window buttons. A click on your name still shows what your role allows.

## [3.0.2] - 2026-10-14

### Added

- **Web View**: the page select also opens the camera's **Overlays** and **Privacy masks** pages.

### Fixed

- **Credentials**: logins saved with "Save to credential list" while adding devices show up when the page opens; before,
  they showed only after the client restarted.
- **Web View** on Windows: the page failed with "Access is denied" in the installed client. The browser data is now kept
  in the user's own folder instead of next to the program.

## [3.0.0] - 2026-10-13

### Added

- **Edge storage** page: the SD card of every camera in one table (state, size, used, wear, encryption, retention) and
  the camera's tools for the selected cameras: Check, Set retention, Repair, Format, Encrypt and Turn off encryption. Every
  tool asks first and says which recordings are lost; the card is always mounted again afterwards. Off by default
  (Plugins page); "Format and encrypt storage" in the roles decides who may format (Operator does not have it).
- **Light appearance**: Settings > This computer > Appearance, Dark or Light. Takes effect after the client restarts.
- Add devices: **Use DNS names instead of IP addresses** (Discovery and Network range) lists the devices by their DNS
  name and adds them by it. Devices without a DNS name are added by IP address. Remembered on each computer.
- **Configure devices** (context menu, Maintenance), like ADM: set chosen parameters, action rules and stream profiles on
  many devices at once. The settings come from a device or an AXIS Device Manager configuration file; each device skips
  what it does not have. Dangerous settings (addresses, HTTPS, ports, login method, IEEE 802.1X) are marked, confirmed and
  need their own permission.
- **Create configuration file** (context menu, Maintenance): save chosen settings of one device as an AXIS Device Manager
  configuration file.
- The device picker can choose exactly one device.
- **Applications (ACAP)** installs license key files: with the package or on their own (**Install license**). Choose many
  key files or a ZIP; each device gets the key with its MAC address.
- **Backup and restore** (Settings, administrators): back up the whole server into one password-protected file, now or
  every day automatically, and restore it, also on another computer. A restore saves the current state first; a backup of
  an older OADM is upgraded.
- Before every database upgrade the server copies its database to `backups/before-upgrade`.

### Fixed

- **Configure devices**: the web server ports, HTTP and HTTPS policy, HTTP/2 and the web interface switch count as
  dangerous settings; IP addresses are never offered or written there (use Network settings or Assign IP address).
- **Applications (ACAP)**: a device with license keys for several versions of an application gets the one that fits
  the installed version.
- Backup and restore: a restore cut off by a crash is put back at the next start.
- **Applications (ACAP)** is offered on cameras that run applications but do not name them in their API list (seen on AXIS
  OS 11.9). OADM now reads the camera's parameters, as Axis documents it.
- Applications: removing an application waits up to 2 minutes (it can take about a minute); every error the camera
  reports for installing, starting, stopping, removing and license keys is shown in plain words with what to do; a failed
  upgrade that removed the application says so; long details in the device table wrap.
- **VAPIX Commander**: the PTZ, privacy mask and application commands were refused on current cameras (they asked for an
  API version or name the cameras do not list).

### Changed

- The server setting "Use host name when available, otherwise IP address" is removed: the add page decides per add (see
  above). Devices keep the address they were added with.
- Settings: the server settings are in four cards (Polling, Discovery, Tasks, Server), saved together with Save below
  them.
- The client asks for the terms of use on its first start on each computer (and again when they change). The Windows
  and macOS installers no longer show a license page, so every install method and platform is the same.

## [2.6.0] - 2026-10-13

### Added

- **Edge Recording Export** page: download the recordings on the SD cards and network shares of your cameras to this
  computer. Choose cameras (also by tag), all recordings or a time range, and Search shows per camera how many
  recordings there are and about how large they are. Download saves them one after the other into a folder per camera,
  as MP4 where the camera offers it; a stopped download continues where it ended. Off by default (Plugins page);
  "Export recordings" in the roles decides who may download (Operator has it).
- Plugins can send files to their page through the server (`ICorePluginDownloads`, gRPC `PluginService.Download`).

### Changed

- VAPIX Commander: **Choose devices** picks the target devices in the same window as the event rules: all devices, tags
  or single devices. Below it, only the devices that cannot be reached or do not support every command are listed.

## [2.5.0] - 2026-10-12

### Added

- **Live View** page: saved views of several cameras at once, opened in tabs. Create a view, drag cameras from the list
  into it, move and resize them on a grid, and save it for every user. Up to 16 cameras per view; "Change views" in the
  roles decides who may create and change views.

- Live view: **zoom and focus** for cameras with a remote zoom or focus lens, in a bar over the picture: Zoom and Focus
  sliders, **Autofocus**, **Autofocus area** (drag a frame over the picture) and **Reset focus**. Needs the new
  permission **Optics Control** (Operator has it).
- Live view: **PTZ cameras** move by dragging from the circle in the middle of the picture, zoom with the mouse wheel,
  center on a click, and go to **Home** or a preset from the bar.
- **RTSP port per camera**: right-click a camera, **OADM** > **RTSP port**, for cameras behind a port forward such as the
  AXIS Virtual Loan Tool. The live view and the Metadata Monitor use it; nothing changes on the camera.

- Live view: **decoding on the graphics card** (Settings > This computer > "Decode video on the graphics card", on by
  default). The client tests the graphics card at every start in a separate process; when it does not work the processor
  decodes and the live view shows what to do (Linux: install the video driver of the graphics card).

- Live view: **fullscreen** (button top left or double-click; Esc closes) with a stream of its own at the size of the
  screen, and **Adaptive** or **Full** quality (button top left).
- Settings > This computer: **Show video details over the picture** (codec, resolution, frame rate, bitrate, graphics
  card or processor) and **Frame rate (adaptive quality)**, 20 by default.

### Fixed

- A logout, a new password or a disabled user now ends that user's open live views, Web View pages and event streams at
  once; a role change can no longer be missed while the user is busy.
- Users page: unsaved changes of the permission matrix survive a reconnect and adding a user; a save made on an older
  matrix is refused instead of undoing another administrator's change; rows no longer show the wrong permission after
  collapsing a group or searching.
- Web View: devices that need a login or a password get no requests from open tabs; turning the plugin off stops open
  tabs; tabs close when the role loses "Open devices"; Reload works every time; tab titles follow a moved device; large
  uploads through the web interface keep the server's disk from filling up.
- PTZ: a drag can no longer leave the camera turning (a second button, Alt+Tab, closed controls), the stop is never
  overtaken by a late move, and choosing the same preset again goes there again.
- Zoom and focus: an answer for the previous source no longer lands on the new one.
- RTSP port: setting it for many cameras is one write, and the live view really uses the new port at once.
- Add devices: "Save to credential list" is for administrators only.

### Changed

- Live view: the resolution follows the size of the picture and changes without a gap: the old picture stays until the
  new one is there. The video stops 2 seconds after it is hidden (another page, a minimized window) and starts again
  when shown.
- Live view: the picture area shows only the picture, a progress ring or an error. Fullscreen, quality and the source
  buttons show when you point at the picture; zoom, focus and PTZ open with a button at the bottom right. The header
  shows the model and the source name.
- Metadata Monitor: the RTSP port field moved to the device list (**OADM** > **RTSP port**).
- Live view: the video uses 16:9 whenever the camera offers it, also on cameras with a 4:3 sensor.
- Live view: the video details are a small card at the top right with one line each (codec, resolution, frame rate,
  bit rate, decoding), so the zoom, focus and PTZ controls keep the bottom.
- Live view: zoom, focus and PTZ are shown when the live view opens, in one row with the **Controls** button.
- RTSP port dialog: a shorter text above the port.

## [2.4.0] - 2026-10-12

### Added

- **Roles and permissions**: every user has one role. **Administrator** may do everything, **Operator** works with
  devices and tasks, **Viewer** sees the devices, live view and the monitoring pages. Create roles of your own in the
  permission matrix on the **Users** page: one row per permission, grouped by Devices, Tasks, Event rules and plugin,
  one column per role. A warning icon marks permissions with full control of the devices.
- Finer rights inside tasks: **Upgrade firmware** (upgrade, with factory default, downgrade), **Users** on devices
  (add, change, remove), **Applications (ACAP)** (install, downgrade, start and stop, remove) and **Import
  configuration** (settings, connection settings). The dialogs show only what the role may do.
- Plugin pages follow the role: a role sees only the pages it may show, and Save of the NTP and DHCP server, the
  certificate authority, triggers, scans and VAPIX Commander's Send and Run need their own permissions.
- **Your role**: click your name at the bottom of the navigation rail to see what your role allows.
- A new role applies to the user's next action, without logging in again.

### Changed

- **After the update the Operator role has less**: Web View, removing users on devices, firmware downgrades and
  factory defaults, downgrading and removing applications, and importing connection settings are off. Administrators see
  a notice with **Check roles** once; tick what your operators need.
- **Users** page: **+ User** adds a user; Edit user, Change password and New role open dialogs. The users on top, the
  roles below, split in half.

### Fixed

- A session ended by Log out, a new password or a disabled user could stay valid on the server until it expired.

## [2.3.0] - 2026-10-12

### Added

- **Web View**: the web interface of the devices in tabs inside OADM, already logged in. Devices grouped by tag, one
  tab per device, Back and Reload, and a page select for the web interface, Image Health Analytics, Object Analytics,
  License Plate Verifier or any other path on the device. The login stays on the server.

### Changed

- Logs and the task details: information entries show the blue info icon.
- Event rules: the **Placeholders** list shows only next to actions that write texts (Send email, Send HTTP request).
- Navigation: **Plugins** moved to the top of the bottom entries, with a divider; the selected entry has a short
  accent bar at its left edge, like Windows apps; quieter hover and selection colors, purple group names, the pages
  scroll when they do not fit, and the icon wobbles on hover. **DHCP server** and **NTP server** have their own icons.
- The **Plugins** page groups the plugins like the navigation rail: Servers, Automation, Security, Monitoring, Reporting.
- Rounded corners as in Windows 11: cards, menus and flyouts 8 px, buttons, inputs and rows 4 px.
- Avalonia 12.1.4.

## [2.1.1] - 2026-10-11

### Fixed

- Image Health Dashboard: a detection turned off in the app no longer keeps a camera out of **Good**.

## [2.1.0] - 2026-10-11

### Added

- Event rules: new actions **Send email** and **Send HTTP request** (webhooks, Teams, Slack, building systems), with
  login, headers, a JSON, XML, HTML or text body and **Allow untrusted certificates**. **Send test** tries them right away.
- Event rules: a **Placeholders** list next to the actions, with a copy button for each. New placeholders for the
  device: `{hostName}`, `{mac}`, `{firmware}`, `{category}`, `{tags}`, `{status}`, `{webUrl}`, and `{date}`, `{timeUtc}`.
- VAPIX Commander: **+** in the Library writes a new command in its own window, from scratch or from an existing
  command, with Send to try it. The pen icon of a saved command opens it there to change it.
- VAPIX Commander: a field can be removed again; its value goes back where the placeholder was.

### Changed

- Event rules: the limit **Flood limit** is now called **Max per minute**.
- VAPIX Commander: the page has no Raw request tab any more; the middle card is the rollout set.
- VAPIX Commander: Make field and Remove are icons in each query and header row.

### Fixed

- VAPIX Commander: renaming a placeholder while typing no longer resets the field's label, type and value.
- VAPIX Commander: a click on a library command sometimes needed a second click.

## [2.0.0] - 2026-10-11

### Added

- **Event rules** (new page under Automation): IF events THEN actions. Events: device unreachable, credentials required,
  certificate changed, certificate expiring or expired, and any event of your devices through Device triggers. Conditions: one event, then
  further events and schedules that must all hold. Actions (up to five per rule, in order): **Set output** (pulse,
  active, inactive, toggle or follow the rule, for example a relay) and **Run VAPIX command** (a saved VAPIX Commander
  command, with an optional second one when the rule turns off), on the device of the event, chosen devices, devices
  with tags or all devices. Limits against event storms, copy rules, and an event log of everything that ran.
- **I/O Monitor** (plugin, off by default): pick a device and see its inputs and outputs live.
- **Device triggers** (plugin, off by default): use any event your devices offer in event rules, for example a digital
  input, a virtual input, an Object Analytics scenario or a storage alert, on one or many devices, optionally only for
  one port or scenario. OADM reads the events from the devices and keeps a live connection to each. Every trigger also
  has a "connection lost" event, so a rule can react when a device stops sending. The event's values (port, scenario,
  counts) can be used in actions, for example {port}. The detections of AXIS Image Health Analytics (blur, block,
  redirect, ...) come this way too.
- Plugin pages open in a window of their own: right-click the page in the navigation rail, **Open in new window**.
- **Export configuration** and **Import configuration** in the context menu under Maintenance (AXIS OS 12.5 or later):
  save the configuration of devices as files, then import one file into every selected device of the same model, or the
  ZIP of an export so each device gets its own file back. Host name, IP address and device names stay as they are on
  each device. Uses the same export and import as the camera's web interface.
- Image Health Dashboard: count cards above the table for Good, Detected, Pending, Detection off and Errors (app not
  running or camera not reachable).
- Event rules: the dialog is a wizard (Rule, When, Then, Overview). Devices are chosen in one device picker: the device
  of the event, all devices, or any mix of tags and single devices (a tag also covers devices tagged later).
- Device triggers can use all devices and tags too; new and newly tagged devices join by themselves.
- The event log is on the Event rules page below the rules, with **Delete all** for administrators.
- Run VAPIX command fills the event's values into the command: type `{carID}` or `{port}` as a field's value.
- VAPIX Commander: the request body is colored while you type (JSON, XML) and **Reset** starts a new raw request.
- Import configuration: a separate area **Connection settings** (HTTPS, HTTP port, login method, IEEE 802.1X, IPv6, web
  interface), off by default and with a warning.
- Log in: a refresh icon next to the server tries again, e.g. when the server was not running yet.

### Changed

- The Metadata Monitor is now off by default. To use it, an administrator turns it on on the Plugins page.
- Cameras with older AXIS OS (8.x, before 8.50) can be added and managed: OADM recognizes them by their login prompt and
  reads what they support from their parameters, so Upgrade firmware, Users, Network settings, Date and time and
  Applications are available for them.
- Every dialog has a header with its title and a close button, no window title bar; dialogs no longer move or resize.
- Event rule limits are called **Dead time** and **Flood limit**; their meaning is in an info icon next to the field.
- Form fields show their error as soon as you leave them, also when they were left empty.
- Device triggers has a new icon.

### Fixed

- Event rules: one slow device no longer delays the actions of other devices; duplicating a rule with two actions of
  the same type works; actions wait until the plugins have started; rule states survive a restart.
- Device triggers: fewer device requests on large sites, no connections left open after turning the plugin off, a
  refused filter affects only its own trigger.
- Import configuration no longer copies the multicast addresses of one camera to the others, and one failed settings
  area no longer stops the rest.
- I/O Monitor: the live view keeps running after a login or certificate change and after a short connection drop.
- "Open in new window" opens one window, also after logging out and in again.
- The device picker keeps a chosen tag that no device has yet.
- The event log stays live after the connection to the server drops.

## [1.5.0] - 2026-10-10

### Added

- **SoC** column on the Devices page: the chip of each device, for example ARTPEC-8. Also in the search and the export.
- Download section of the website: a "What's new" link to the release notes of the latest version.

### Changed

- **Plugins** has its own page in the navigation rail, next to Users and Credentials (administrators only), with a
  short description of each plugin instead of what it adds and its version.
- Restart, Upgrade firmware, Date and time, Applications (ACAP), Users, Network settings, PKI and System report are
  always on, can no longer be turned off and are not listed on the Plugins page. Plugin SDK: `alwaysOn` and
  `description` in `plugin.json`.

## [1.4.0] - 2026-10-09

### Added

- **Image Health Dashboard**: the status of AXIS Image Health Analytics on every camera in one table: blur, block,
  redirect, under-exposure and unsuitability as OK, Pending, Detected or Off, plus "Not running" for cameras where the
  app is stopped. The page checks the cameras when it opens and on **Refresh**; **Auto refresh** checks every 10
  seconds while the page is open. Nothing runs in the background. Off by default.
- **Plugins** on the Settings page: every plugin with what it adds and its version. Administrators turn plugins on or
  off; the change applies at once and is in the audit log. A plugin that is off shows no page, menu entry or toolbar
  button. Plugin SDK: `enabledByDefault` in `plugin.json`.
- Metadata Monitor: **RTSP port** for cameras behind a router that forwards RTSP to another port. OADM remembers it
  per camera.

### Changed

- The **Hardening scan** is off by default. Turn it on under Settings > Plugins.
- Snapshot report: a snapshot that does not arrive within 3 seconds (was 10) counts as failed, so offline cameras no
  longer slow down the page.

## [1.3.0] - 2026-10-09

### Fixed

- Snapshot report: **Export PDF** failed in the installed version ("The report could not be created"): the plugin did
  not find its PDF library. Plugins now load every library from their own folder.
- Report errors always say what went wrong; they were empty before.
- The client no longer logs a hidden error from the start splash animation.

### Changed

- Updated Avalonia to 12.1.3 and the test tools.

### Security

- `SECURITY.md`: report vulnerabilities privately through GitHub. Dependabot alerts and updates and CodeQL code scanning
  are on.

## [1.2.0] - 2026-10-09

### Added

- PKI: **Install CA certificates** in the Security menu installs CA certificates on the selected devices, so they trust
  what those CAs issued (for example a RADIUS server or a video management system). Choose several files at once (PEM,
  also bundles, or DER). Each certificate is checked (a CA, not expired) and shown with name, issuer, validity and
  fingerprint; duplicates are merged. Certificates a device already has are skipped.
- **Unsupported tasks stay visible**: the device context menu lists every task of a group; one the selection cannot run
  is greyed out with the reason as tooltip ("Needs AXIS OS 11.11 or later (this device has 11.9.65)", or "...: 3 of 5
  selected devices"). Toolbar task buttons too. Plugin SDK: `ITaskPlugin.NotSupportedReason` and `TaskSupportReasons`;
  the bundled plugins name the missing firmware, API or device status.
- **Device tags** with colors: a Tags column with colored chips (sortable, searchable, in the column chooser) and
  **Tags** in the device context menu for one or many devices: check tags for all, some or none of the selection,
  create tags in one of eight colors; administrators rename, recolor and delete them.
- **Group by tag** on the Devices toolbar: one collapsible group per tag, a device with several tags under each, "No
  tag" last; remembered per client.
- The device export has a **Tags** column; the import reads it and tags the devices it adds.
- Plugins read a device's tags through `IDeviceInfo.Tags`.
- DHCP server: **Automatically add Axis devices that get an address**. OADM checks that the device is an Axis device
  and logs in with the credential list. Cameras without a password are added as "Password not set", cameras no
  credential fits as "Credentials required".
- DHCP server: managed devices that get a new address are followed right away, after OADM checked the device at that
  address.
- **Set password** in the device context menu for devices in factory default: sets the first password like the add
  page, checked against the device's password rules.

### Changed

- Shorter, plainer texts across the app.
- Snapshot report: opening the page takes no snapshots; press **Create snapshots** first.
- Date and time: compact dialog like ADM's.
- Login window: no logo above the form.
- Live view: no "Live" label while the picture plays; codec, resolution and frame rate stay.
- Clearer layout: Address first in the device grid, colour only for warnings and errors, red buttons for delete and
  remove, longer task names, readable failure reasons, and a More menu when the toolbar does not fit.

### Fixed

- Hardening scan: the result list no longer covers the toolbar when the window is smaller than when the detail pane
  was last resized.

## [1.1.0] - 2026-10-08

### Added

- **Log in** for devices whose password was changed elsewhere: shown for devices in "Credentials required". The server
  checks the new user name and password on each device before it stores them; administrators can also save them to the
  credential list.
- **Hardening scan** (rail page, group Security): a read-only check of every managed device against the AXIS OS
  hardening guide, Basic and Extended, one column per check, CSV export.
- **System report**: toolbar button that downloads the server reports of the selected devices as one ZIP for Axis
  support.
- **Export** of the device list as CSV, and **Import from file** (a CSV or one address per line, optional user name and
  password per device) on the add page.
- **Refresh** in the toolbar and the context menu: reads the selected devices again.
- **Add** menu on the Devices toolbar: Discovery, Network range, Add manually and Import from file in one button.
- **AXIS OS - Release Notes** button on the Devices toolbar (opens help.axis.com).
- Animated **start splash** while the client connects, resumes a remembered login and loads the devices.
- Core plugin pages are **grouped** in the navigation rail: Servers, Automation, Security, Monitoring, Reporting
  (plus Maintenance, Integrations, Utilities and Extensions for future plugins).
- Certificate columns also for devices reached over HTTP only (for example through a port forward).
- `NOTICE` file with the copyright line, shipped with every installer and shown on the About page.
- `CONTRIBUTING.md` and this changelog.

### Changed

- Linux: two packages, `oadm-server` and `oadm-client`; the metapackage `oadm` is gone (apt removes it on upgrade).
- Minimum window width 1800 px; System report, Export and AXIS OS - Release Notes are buttons with text.
- Certificates without an end date (31.12.9999, for example the Axis factory certificate) show "Never".
- VAPIX Commander: no "Started N tasks" message after a rollout; the tasks appear in the tasks pane below.
- Hardening scan: the detail area is only the result table; its Result column sorts failed checks first.
- Plugin SDK: DHCP, HTTPS and IEEE 802.1X of each device, streamed device answers, a data folder per plugin, core
  plugins without a page, and calls from toolbar plugins to their server part.

### Fixed

- The splash logo's lens is centered in its ring.
- The server publish no longer contains an IIS `web.config` and other unused web files.

[3.0.8]: https://github.com/Cacsjep/oadm/releases/tag/v3.0.8
[3.0.7]: https://github.com/Cacsjep/oadm/releases/tag/v3.0.7
[3.0.6]: https://github.com/Cacsjep/oadm/releases/tag/v3.0.6
[3.0.5]: https://github.com/Cacsjep/oadm/releases/tag/v3.0.5
[3.0.4]: https://github.com/Cacsjep/oadm/releases/tag/v3.0.4
[3.0.3]: https://github.com/Cacsjep/oadm/releases/tag/v3.0.3
[3.0.2]: https://github.com/Cacsjep/oadm/releases/tag/v3.0.2
[2.3.0]: https://github.com/Cacsjep/oadm/releases/tag/v2.3.0
[2.1.1]: https://github.com/Cacsjep/oadm/releases/tag/v2.1.1
[2.1.0]: https://github.com/Cacsjep/oadm/releases/tag/v2.1.0
[2.0.0]: https://github.com/Cacsjep/oadm/releases/tag/v2.0.0
[1.5.0]: https://github.com/Cacsjep/oadm/releases/tag/v1.5.0
[1.4.0]: https://github.com/Cacsjep/oadm/releases/tag/v1.4.0
[1.3.0]: https://github.com/Cacsjep/oadm/releases/tag/v1.3.0
[1.2.0]: https://github.com/Cacsjep/oadm/releases/tag/v1.2.0
[1.1.0]: https://github.com/Cacsjep/oadm/releases/tag/v1.1.0
