# Changelog

All notable changes to OADM are listed here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).
New changes go under **Unreleased**; a release renames that section to its version and date, and the release
workflow copies the section into the GitHub release.

## [Unreleased]

### Added

- PKI: **Install CA certificates** in the Security menu installs one or more CA certificates on all selected devices,
  so they trust certificates those CAs issued (e.g. of a RADIUS server or a video management system). Choose several
  files at once (PEM, also bundles, or DER), add more or remove rows; each certificate is checked (a CA, not expired)
  and shown with name, issuer, validity and fingerprint, duplicates are merged. Certificates a device already has are
  skipped.
- **Unsupported tasks stay visible**: the device context menu always lists every task in its group; a task the
  selection cannot run is greyed out with the reason as tooltip ("Needs AXIS OS 11.11 or later (this device has
  11.9.65)", or "...: 3 of 5 selected devices" for several), also on toolbar task buttons. Plugin SDK:
  `ITaskPlugin.NotSupportedReason` and `TaskSupportReasons`; the bundled plugins name the missing firmware, API or
  device status.
- **Device tags** with colors: a Tags column with colored chips (sortable, searchable, in the column chooser), a
  **Tags** entry in the device context menu for one or many devices (check tags for all / some / none of the selection,
  create tags with one of eight colors; administrators rename, recolor and delete them), applied in one server call.
- **Group by tag** on the Devices toolbar: one collapsible group per tag, a device with several tags under each of
  them, "No tag" last; remembered per client.
- The device export has a **Tags** column; the import reads it and tags the devices it adds.
- Plugins read a device's tags through `IDeviceInfo.Tags`.
- DHCP server: **Automatically add Axis devices that get an address**. When an Axis camera gets its address from
  OADM's DHCP server, OADM adds it like the add page does: it checks that it is an Axis device and logs in with the
  credential list. Cameras without a password yet are added as "Password not set", cameras no credential fits as
  "Credentials required".
- DHCP server: managed devices that get a new address are followed right away (their record moves after OADM checked
  the device at the new address).
- **Set password** in the device context menu for devices in factory default: sets the first password like the add
  page, checked against the device's password policy.

### Changed

- Shorter, plainer texts across the app.

## [1.1.0] - 2026-10-08

### Added

- **Log in** for devices whose password was changed elsewhere: the context menu entry appears for devices in
  "Credentials required", the server checks the new user name and password on each device before it stores them,
  optionally saved to the credential list (administrators).
- **Hardening scan** (rail page, group Security): a read-only check of every managed device against the AXIS OS
  hardening guide, Basic and Extended level, one column per check, CSV export.
- **System report**: toolbar button that downloads the server report of the selected devices into one ZIP file for
  Axis support.
- **Export** of the device list as CSV, and **Import from file** (a CSV or one address per line, optional user name
  and password per device) on the add page.
- **Refresh** in the toolbar and the context menu: reads the selected devices again now.
- **Add** menu on the Devices toolbar: Discovery, Network range, Manual and Import from file in one button.
- **AXIS OS - Release Notes** button on the Devices toolbar (opens help.axis.com).
- Animated **start splash** while the client connects, resumes a remembered login and loads the device list.
- Core plugin pages are **grouped** in the navigation rail: Servers, Automation, Security, Monitoring, Reporting
  (plus Maintenance, Integrations, Utilities and Extensions for future plugins).
- Certificate columns for devices reached over HTTP only (e.g. a port forward): the certificate is read through VAPIX.
- `NOTICE` file with the copyright line, shipped with every installer and shown on the About page.
- `CONTRIBUTING.md` and this changelog.

### Changed

- Linux: two packages, `oadm-server` and `oadm-client`; the metapackage `oadm` is gone (apt removes it on upgrade).
- Minimum window width 1800 px; System report, Export and AXIS OS - Release Notes are buttons with text.
- Certificates without an end date (31.12.9999, e.g. the Axis factory certificate) show "Never".
- VAPIX Commander: no "Started N tasks" message after a rollout; the tasks appear in the tasks pane below.
- Hardening scan: the detail area is only the result table; its Result column sorts failed checks first.
- The plugin SDK gives plugins DHCP, HTTPS and IEEE 802.1X of each device, streamed device answers, a data folder per
  plugin, page-less core plugins and calls from toolbar plugins to their server part.

### Fixed

- The splash logo's lens is centered in its ring.
- The server publish no longer contains an IIS `web.config` and other unused web files.

[1.1.0]: https://github.com/Cacsjep/oadm/releases/tag/v1.1.0
