# Changelog

Notable changes to OADM, newest first. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions:
[Semantic Versioning](https://semver.org/). New changes go under **Unreleased**; a release renames that section to its
version and date, and the release workflow copies it into the GitHub release.

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

[1.4.0]: https://github.com/Cacsjep/oadm/releases/tag/v1.4.0
[1.3.0]: https://github.com/Cacsjep/oadm/releases/tag/v1.3.0
[1.2.0]: https://github.com/Cacsjep/oadm/releases/tag/v1.2.0
[1.1.0]: https://github.com/Cacsjep/oadm/releases/tag/v1.1.0
