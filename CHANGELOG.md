# Changelog

Notable changes to OADM, newest first. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions:
[Semantic Versioning](https://semver.org/). New changes go under **Unreleased**; a release renames that section to its
version and date, and the release workflow copies it into the GitHub release (the client's new version notice shows it).
Each entry is one short line in plain words (what the user gets, not how); no nested lists, no bold, no code.

## [Unreleased]

### Added

- Clock column and the event Any device: clock off: devices whose clock drifts or loses NTP.
- Compliance check Clock: the device clock matches the server's and NTP is synced.
- Plugin SDK 0.7.0.
- Applications: install one package per processor type at once.
- Applications: change the settings an application registers, on many devices at once.
- Applications page: the applications of many devices with versions, status and licenses, and start, stop, install or remove them.
- Applications page: install applications and licenses on many devices, with a check per device before anything is written.
- Applications page: the log of an application, and its settings or its own page.

### Changed

- Snapshot report is off by default; an administrator turns it on on the Plugins page.
- The DHCP column is hidden by default; turn it on with the columns button.
- Installing an application on AXIS OS 13 warns that the device accepts only signed applications.

## [4.6.0] - 2026-10-28

### Added

- Load test: puts the chosen cameras under load and shows whether the network and the cameras carry it.
- Help: the user docs open from the title bar, also without internet.
- Plugin SDK 0.6.0.

### Changed

- On a new server the 802.1X trusted CA policy of the first CA carries the CA's real name.
- The NTP, DHCP, 802.1X and EST servers say the same thing when another program uses their port.
- A refused firmware downgrade names the checkbox to turn on.

## [4.5.2] - 2026-10-27

### Added

- A notice at client start when a new OADM version is available, with its release notes.
- Upgrade firmware takes one file per model at once.
- Plugin SDK 0.5.0.

### Changed

- New login window, with clearer messages when the server cannot be reached.
- Upgrade firmware skips devices that are up to date or have no file for their model.

### Fixed

- AXIS OS 12.11 cameras log in at the switch after Enable IEEE 802.1X.
- Upgrade firmware commits the new firmware on AXIS OS 12.11.

## [4.5.0] - 2026-10-26

### Added

- PKI page: certificates, policies, 802.1X server and EST server in one place.
- Create root and intermediate CAs.
- Certificates, HTTPS and IEEE 802.1X from AXIS OS 6.53.
- 802.1X server (OADM PRO): only the cameras OADM manages get onto the network.
- 30-day OADM PRO trial in Settings > License.
- Device groups: limit a user to some devices.
- Architecture column in the device list.
- Web View: Apps, Recordings and Security pages.
- Plugin SDK 0.4.0.

### Changed

- The EST and 802.1X servers are tabs of the PKI page.
- Delete replaces Revoke for certificates.
- The Users page is now Permissions.
- Flat toolbar buttons everywhere.

### Fixed

- The built-in Enrollment and Expiry warning policies can change their devices.
- The OADM PRO trial shows its policies at once.
- Settings opens a card with a wrong value.
- Metadata Monitor shows Stopped for a stream that is gone.

## [4.5.0-rc.1] - 2026-10-25

### Added

- PKI page with the tabs Certificates, Policies, 802.1X server and EST server.
- Certificates tab: every CA and certificate OADM uses; create, import, export, delete.
- Policies tab: every certificate rule in one table; 4.1 settings become policies.
- Several CAs can issue certificates; each policy names its CA.
- Enable HTTPS and Enable IEEE 802.1X pick a policy and check the camera's clock first.
- Certificates, HTTPS and IEEE 802.1X from AXIS OS 6.53 (was 11.11).
- 802.1X server (OADM PRO): only the cameras OADM manages get onto the network.
- Start a 30-day OADM PRO trial in Settings > License, no account needed.
- Device groups: limit a user to some devices; existing users keep all devices.
- Architecture column in the device list (off by default).
- Web View: Apps, Recordings and Security pages.
- Plugin SDK 0.4.0.

### Changed

- The EST and 802.1X servers are tabs of the PKI page; a server that was on stays on.
- The 802.1X and EST servers share one certificate from the Certificates list.
- A new IEEE 802.1X policy uses the camera's IP address as identity.
- HTTPS tasks wait for the camera's web server to restart.
- The Users page is now Permissions; This computer in Settings is now Client.
- Buttons look the same everywhere: flat in toolbars, one filled button per dialog.

### Fixed

- Settings opens a card with a wrong value; polling up to 86,400 s is accepted.
- Metadata Monitor shows Stopped for a stream that is gone.

## [4.1.0] - 2026-10-23

### Added

- OADM PRO: License card in Settings to import, remove or try a license.
- OADM PRO plugins carry a chip and stay off without an active license.
- EST server (OADM PRO): cameras from AXIS OS 12.10 get and renew their certificates.
- EST server at sites: cameras get their certificates through their site node.
- EST server page: choose the network card, list enrollments and certificates not renewed.
- Certificate enrollment in the Security menu: set up or remove enrollment profiles.
- Events Certificate enrolled, Enrollment refused and Certificate not renewed.
- Create support report in Settings: logs and a server summary in one ZIP.
- Plugin SDK 0.3.0.

### Changed

- A camera's renewed certificate from the OADM CA no longer shows Certificate changed.
- A renewed certificate is trusted only when it names this camera alone.
- The server ID shows only in Settings > License.
- Turning off Allow Basic login over HTTP ends such streams and live videos at once.
- Updated libraries for email and certificates.

### Fixed

- The OADM Site Node retries the server every 10 s (was 60 s) and logs a failure once.
- Configure devices refuses a host name of spaces only or with a tab.

## [4.0.2] - 2026-10-22

### Added

- Settings: Allow Basic login over HTTP, for devices without HTTPS (off by default).

### Fixed

- Devices that accept only Basic login say so, instead of "wrong user name or password".
- Configure devices accepts several host names separated by spaces, such as NTP servers.

## [4.0.0] - 2026-10-22

### Added

- Sites: manage devices in networks the server cannot reach, through an OADM Site Node.
- OADM Site Node for Windows and Linux; it only connects out, no port to open at the site.
- Sites page: accept site nodes, bind them with a pairing code, rename, replace, remove.
- Discovery, network range, imports and the waiting list find devices at every site.
- Site column in the device list; devices that move between sites follow.
- Devices of an offline site show Site offline and are left alone until it is back.
- Events Site offline and Any site offline for the event rules.
- Site node app shows the link state, the pairing code and the networks it reaches.
- NTP server serves the devices at all sites or at the sites you choose.
- Roles: a start page per role, the page its users see first.

### Changed

- Plugin SDK 0.2.0.
- The Plugins page shows which plugins work at sites.
- The Windows firewall opens the site node port only while Accept site nodes is on.
- Applications (ACAP): wider window, installed applications left, install card right.

### Fixed

- Applications (ACAP): installing failed with "storage is full" on AXIS OS 12 devices.

## [3.6.2] - 2026-10-21

### Added

- VAPIX Commander: Siren and light commands for AXIS strobes and sirens.
- VAPIX Commander: commands can send a file of up to 1 MB.

### Fixed

- The main window returns to full screen after it was minimized.

## [3.6.0] - 2026-10-19

### Added

- Compliance page: which devices need attention at first glance (off by default).
- Compliance checks: certificate, firmware, application running, configuration, SD card.
- A failing compliance check is an event, so it can send an email.
- Compliance column in the device list, and one column per check on request.
- Roles: See compliance and Change compliance checks.
- Plugin SDK: plugins can add their own compliance checks.

### Changed

- Count cards have the same background as every other card.
- Add devices: devices OADM logged in to are checked, so Add works right away.
- Add devices: an import offers to add the devices that do not answer yet later.

## [3.5.1] - 2026-10-18

### Changed

- Select and number fields show the accent border while they have the focus.

### Fixed

- Dialogs open on the monitor of the window they belong to.
- The mouse wheel over a select or number field scrolls the page instead of changing it.

## [3.5.0] - 2026-10-17

### Added

- Event rule action Send TCP message, for building, alarm and video systems.
- Licenses for commercial plugins: enter or remove license keys on the Plugins page.
- Plugin SDK on NuGet: write plugins with the .NET SDK alone, with templates.
- Portable server and client ZIP files that run without installing.
- Server log on the Logs page, with level filter and search.
- Failed event rule actions are written to the server log with their reason.

### Changed

- Plugins built against older SDK versions keep loading.
- A plugin can be rebuilt while the client runs.
- Settings: Full refresh interval is now Read device details every (min).
- Action Rule Editor: the grouping by event is called Event.

### Fixed

- Add devices: a Retry at the end of a search no longer loses other login results.

## [3.4.0] - 2026-10-17

### Added

- Imported devices that are off are added once they come online.
- Waiting devices: see the last try, try now, export or remove them.
- Settings: Try waiting devices every (min), 15 by default.
- Gauge widget on dashboards: a number on an arc with colored thresholds.

### Changed

- Import from file and Import from ADM take up to 30,000 devices per file.
- Imports are faster: 30,000 devices take about 12 to 15 minutes on a LAN.
- Tags can have up to 64 characters; longer imported tags are shortened.
- Dashboards: a new threshold takes a color not used yet.
- Settings: IP range scan timeout is now Scan timeout and also applies to imports.

## [3.3.1] - 2026-10-16

### Fixed

- Windows and dialogs fit on small and scaled screens, such as Full HD at 125 %.
- Toolbar buttons that do not fit go into a More menu.
- The main window can be made as small as 1200 x 640.

## [3.3.0] - 2026-10-16

### Added

- Dashboards (off by default): live event values and live video on saved dashboards.
- Dashboard widgets: On / Off, Text and Number with thresholds; up to 16 cameras.
- Any device events: unreachable, credentials required and certificate warnings.

### Changed

- Event rules: a device trigger's values are listed as placeholders.
- Live View: in edit mode every camera has a thin frame and a red remove button.

## [3.2.0] - 2026-10-16

### Added

- Up to 30,000 devices per server.
- Pages that read many cameras start from the devices you choose.
- Metadata Monitor and I/O Monitor pick their device with a search.
- Loading and progress shown while the device list, tasks and large actions load.

### Changed

- A device that does not answer is asked once more before it shows Unreachable.
- 30,000 devices are checked every minute.
- The server needs much less memory for large device lists.
- System report: at most 50 devices at once.
- Device triggers watch at most 10,000 devices.
- Hardening scan: one Scan button for the chosen devices.
- View certificates and Delete certificates: at most 5,000 devices at once.

### Fixed

- Tagging, sorting and grouping thousands of devices no longer freezes the window.

## [3.1.0] - 2026-10-15

### Added

- Action Rule Editor (off by default): change camera action rules for many at once.
- Action Rule Editor: edit only the checked parts; everything else stays per camera.
- Action Rule Editor: new rule, copy to devices, turn on, turn off, delete.
- Action Rule Editor: undo every change for 30 days.
- Action Rule Editor: Recipients and Schedules tabs.
- Action Rule Editor works on AXIS OS 10, 11 and 12.

### Changed

- VAPIX Commander: read commands show only the values a camera has.

## [3.0.8] - 2026-10-14

### Added

- Import from ADM also reads the newer AXIS Device Manager export format.

## [3.0.7] - 2026-10-14

### Added

- Server and client packages for Linux ARM64, such as Raspberry Pi 4 and 5.

### Fixed

- The release uses the fixed version of an email and PDF library.

## [3.0.6] - 2026-10-14

### Added

- Upgrade firmware warns when an upgrade is off the upgrade path Axis recommends.
- Import from ADM: add the devices of an AXIS Device Manager export with logins and tags.

## [3.0.5] - 2026-10-14

### Added

- Import from file first shows the columns and an example file.

### Changed

- The changelog page on the website has smaller text.

## [3.0.4] - 2026-10-14

### Added

- Live View: a click on a camera opens it alone in its own tab.
- Live View: the camera under the mouse gets a thin frame.

### Fixed

- The client could crash while showing live video of some sizes.

## [3.0.3] - 2026-10-14

### Changed

- Your user name and Log out moved to the top right of the window.

## [3.0.2] - 2026-10-14

### Added

- Web View opens the camera's Overlays and Privacy masks pages.

### Fixed

- Logins saved to the credential list show up without a client restart.
- Web View no longer fails with "Access is denied" on Windows.

## [3.0.0] - 2026-10-13

### Added

- Edge storage page: the SD cards of the cameras and their tools (off by default).
- Light appearance in Settings, next to Dark.
- Add devices by their DNS name instead of their IP address.
- Configure devices: set chosen settings on many devices at once, like ADM.
- Create configuration file: save settings of one device as an ADM configuration file.
- Applications (ACAP) installs license key files, also many at once or as a ZIP.
- Backup and restore of the whole server, also every day and on another computer.
- The server copies its database before every upgrade.

### Fixed

- Configure devices marks ports, HTTPS and login as dangerous; never IP addresses.
- Applications (ACAP): a device gets the license key that fits its installed version.
- Applications (ACAP) is offered on cameras that do not list it (AXIS OS 11.9).
- Applications (ACAP): clearer errors with what to do.
- A restore cut off by a crash is put back at the next start.
- VAPIX Commander: PTZ, privacy mask and application commands work on current cameras.

### Changed

- The add page decides between host name and IP address; the server setting is gone.
- Settings: server settings in four cards, saved together.
- The client asks for the terms of use on its first start; installers no longer do.
