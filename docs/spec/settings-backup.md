# Settings and backup

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

## Server settings

Stored in the table `Setting` (`Oadm.Core.Settings.ServerSettings`), read and written with `SettingsService.Get` / `Set`
(`ServerSettings` in `settings.proto`; an optional field left out of a partial `Set` keeps the stored value, 0 keeps it
for `zero_conf_seconds`). Settings page labels in quotes.

| Key | Default, range | Settings page, proto |
|---|---|---|
| `Polling.IntervalSeconds` | unused since 4.8 (the status poll pauses 60 s after each round, docs/spec/main-window.md); still read and written for backups and older clients, not on the Settings page | `polling_interval_seconds = 1` |
| `Polling.FullRefreshMinutes` | unused since 4.8 (device details are read in rounds with a 10 min pause, docs/spec/main-window.md); still read and written for backups and older clients, not on the Settings page | `full_refresh_minutes = 6` |
| `Polling.ClockToleranceSeconds` | 2, 1..3600 | Polling "Allowed clock difference (s)", `optional clock_tolerance_seconds = 13`; read live (`ClockTolerance`), see `docs/spec/main-window.md` "Clock" |
| `Vulnerabilities.Update` | "off", "server", "clients" | Vulnerabilities "Update the list online" (Off, By the server, By the clients), `optional vulnerability_update = 14`; who fetches the list of Axis CVEs online, see `docs/spec/vulnerabilities.md` |
| `Scan.Parallelism` | 32 | Discovery "Addresses scanned at once", `scan_parallelism = 2` |
| `Scan.TimeoutMs` | 1500 | Discovery "Scan timeout (ms)" (range scans and the first contact of imported lines), `scan_timeout_ms = 3` |
| `Discovery.ZeroConfSeconds` | 30, 5..300 | Discovery "Discovery duration (s)": Discovery on the add page ends after this time, `zero_conf_seconds = 8` |
| `Devices.WaitingRetryMinutes` | 15, 1..1440 | Discovery "Try waiting devices every (min)", `optional waiting_retry_minutes = 10`; `docs/spec/add-devices.md` "Waiting list" |
| `Tasks.MaxParallelPerPlugin` | 16, 1..256 | Tasks "Parallel tasks per plugin" ("How many devices a task runs on at the same time, e.g. restarts or AXIS OS upgrades."), `optional max_parallel_tasks_per_plugin = 9` |
| `Devices.AllowBasicOverHttp` | false | Security "Device login", check box "Allow Basic login over HTTP" ("Sends passwords unencrypted. Only for devices without HTTPS."), `optional allow_basic_over_http = 11` |
| `Server.Name` | the host name | not on the Settings page: users do not change the server name (decision 2026-11-19); the setting, `server_name = 4` and the code stay, still read and written for backups and older clients |
| `Server.ListenUrl` | `https://0.0.0.0:5080` | Server "Listen URL" ("Takes effect after the server restarts"), `listen_url = 5` |
| `Uploads.MaxMegabytes` | 2048, 1..65536 | server only |
| `Uploads.RetentionHours` | 24, 1..8760 | server only |
| `Tasks.RetentionDays` | 90, 0..3650 (0 = no age limit) | server only |
| `Tasks.MaxHistory` | 50000, 0 or 100..1000000 (0 = no limit) | server only |
| `Events.LogRetentionDays` | 30, 0..3650 | server only |
| `Events.LogMaxEntries` | 100000, 0 or 100..1000000 | server only |

- `Tasks.MaxParallelPerPlugin` is read live by the task engine; a plugin's `MaxParallelDevices` can only lower it.
- `Devices.AllowBasicOverHttp` is read live by every device connection (`docs/spec/vapix.md`); operators read On / Off.
- Task history retention runs one minute after start and then hourly (`TaskRetentionHostedService`: finished tasks only,
  one bulk delete, one Removed change per task).
- `use_host_name = 7` is unused and kept for its field number (the add page chooses DNS names or IP addresses per add,
  `docs/spec/add-devices.md`); 12 is reserved. Backups carry every setting (the whole database), the new ones too. Read only in both replies: `key_replaced = 20`, `server_version = 21`,
  `roles_notice = 22`, `server_id = 23`.

## Settings page

The layout of 2026-11-19 (`Settings/SettingsView`, `SettingsViewModel`).

- **Look**: the Windows 11 settings look in OADM's colors. One column up to 980 px, left-aligned. Every setting is a row:
  icon, title and one hint line on the left, its control on the right. Every row has a hint.
- **Top row** below the page title, as wide as the column: the search ("Find a setting") left, the one **Save** right with
  its result ("Saved.") directly left of it. Save writes the server settings and the automatic backups together; it is
  disabled while a field of either has an error and its tooltip says the first one. No Reload, no other Save on the page.
  Client settings are still saved at once. Save sends the automatic backups only when they changed; an edit after
  "Saved." takes the message away.
- **Search**: shows the rows whose title or hint contains the text, also the rows of an expander or group whose title
  matches; groups without a match hide, expanders with one open; nothing found: "No setting matches."
- **Groups** (a heading, rows 4 px apart) and their rows; an expander is a row with a chevron whose rows hang below it,
  closed at the start, opened by the chevron or a click on the row, and by itself while an input inside has an error.
  The rows of a group and the rows inside an expander are in alphabetical order by title; two exceptions: Confirm password
  follows Password, the backups of the server stay newest first.
  - **Client**: Appearance; expander **Live view** ("Decoding, video details and frame rate": Decode video on the graphics
    card, Frame rate (adaptive quality), Show video details over the picture); Tell me about new versions.
  - **General** (server): Allow Basic login over HTTP; Allowed clock difference (NTP); expander **Discovery** (Addresses
    scanned at once, Discovery duration, Scan timeout, Try waiting devices every); Parallel tasks per plugin; expander
    **Vulnerabilities** (the list's size and date on its header; List with Check now, Update the list online).
  - **Server**: expander **Automatic backups** (the switch on its header; Backups kept, Password, Confirm password, Time);
    Backup file (Back up now, Restore from file); expander **Backups on the server** (the folder as hint, the count on its
    header; one row per backup with Download, Restore, Delete); Listen URL; expander **OADM PRO** (the license state on its
    header; License key with Import license, Remove license, Activate license; Try OADM PRO for 30 days with Start 30-day trial);
    Server ID with Copy.
  - **Support**: Support report (Create support report).
- **Not on the page**: Server name (users do not change it, see the table above; AI agents still can through the settings
  tool, decision 2026-11-19), the always visible "Last automatic backup" chip and the last restore lines.
- **Backup results** only when there is something to say (decision 2026-11-19), as a `ui:StatusChip` on the header row,
  left of its control: on **Automatic backups** a red chip with `LastAutomaticText` while the last automatic backup failed
  (`LastAutomaticFailed`), nothing after a good one; on **Backup file** a chip with `LastRestoreText` after a restore
  (green, red when `LastRestoreFailed`), its `LastRestoreLines` as the chip's tooltip.
- **Units**: a number field shows its unit as a badge inside the field at the right (accent background, white text, 3 px
  corners), so labels carry no unit: FPS, sec (clock difference, discovery duration), addresses, ms, min, tasks, backups.
- **Field widths**: an input is only as wide as its longest value needs (a number field: its largest number, the unit
  badge and the up/down buttons; Time five characters), never one common width; the controls share the right edge.
- **Switches**: `ui:OnOffSwitch`, "On" / "Off" written left of the switch (client SDK; also Provisioning management: one
  switch look, `docs/spec/visual-style.md`).
- **Icons**: rows of a group have a 20 px icon in the accent (`Oadm.AccentHoverBrush`); rows inside an expander the normal
  16 px icon in the icon colour. All from `Icons.axaml`; added for this page (Lucide): `moon` (Appearance), `bellRing` (Tell me about new versions), `cpu`
  (Decode video on the graphics card), `timer` (Scan timeout), `star` (OADM PRO), `archive` (Backups kept), `lockKeyhole`
  (Confirm password), `fileArchive` (a backup in the list).
- **Operators** see the server values as plain text with the same hints ("2 sec", "On"), no Save, no backup rows, no
  Support, and "Only administrators can change server settings." under General; OADM PRO shows the state and, inside, the
  trial line for non-administrators (`ProLicenseTexts.TrialAdminLine`) instead of the buttons.
- **Waits**: `ui:ProgressRow` on the Backup file row while a backup, download, upload or restore runs and on the Support
  report row while the report is made, their buttons disabled meanwhile; outcome texts directly left of the buttons they
  report on.
- **Reused**: the theme, `PageTitleBar`, `ui:SearchBox`, `ui:OadmIcon`, `ui:IconLabel`, `ui:ToolbarButton`,
  `ui:StatusChip`, `ui:PasswordBox`, `Button.primary` / `.secondary` / `.toolbar.iconOnly` / `.link`, `NumericUpDown`,
  `ComboBox`, `TextBox`, `ValidatingViewModel` (errors directly below the input through the theme's
  `DataValidationErrors`).
- **Its controls** (`src/Oadm.Client/Controls`, styles in `src/Oadm.Client/Themes/OadmTheme.axaml`, `docs/spec/visual-style.md`
  "Shared controls"):
  - `SettingsList`: the column of groups; `Filter` is the search text, `HasMatches` says whether anything is left; a group
    whose rows the view hides (`IsShown`) hides too.
  - `SettingsGroup`: `Title` and its rows.
  - `SettingsCard`: one row; `Icon`, `Title`, `Hint`, the control as content, `IsShown` for the view's own condition
    (the search uses `IsVisible`).
  - `SettingsExpander`: `Icon`, `Title`, `Hint`, `HeaderContent` (switch or current value), `IsExpanded`, `IsShown`, its
    rows as content (also an `ItemsControl` of rows for a list).
  - `UnitBadge`: `Text`, placed in `NumericUpDown.InnerRightContent`.
- **View models**: `SettingsViewModel` (client, General, Listen URL, `Filter`, the one `SaveCommand`, `SaveBlockedReason` =
  the first error of the server fields, else of the schedule; `ServerName` is loaded and sent back unchanged) with `Video`,
  `Backup` (`BackupViewModel`: `SaveScheduleAsync`, `IsScheduleChanged`, `Keep` 1..365, `BackupCountText`), `License`,
  `Support` as the `DataContext` of their rows.
- AI agents and the Assistant change the server settings with the same checks and texts (`Oadm.Contracts.Settings.ServerSettingsRules`,
  shared by the page and `oadm_settings_set_preview`/`_run`, Administrator; decision 2026-11-15, docs/spec/mcp.md "Server
  settings and services as built"); the credential list, backups and the license key stay in the page.
- Client settings (`client-settings.json` in LocalApplicationData): server address (also `--server`), recent servers,
  pinned servers, remembered logins, device grid column layout and group mode (`GroupDevicesByTag`), bottom pane state,
  `TellAboutNewVersions`, `GpuDecoding`, `ShowVideoDetails`, `AdaptiveFps`, `Appearance`, `AcceptedTerms`.
- **Tests**: `tests/Oadm.Client.Tests/SettingsPageTests` (search, expander opened by an error, the one Save, alphabetical
  order, operators, result chips, progress rows, screenshots dark and light), `FormValidationTests`, `DeviceClockTests`.

## Credentials, Plugins and About pages

- Rail page **Credentials** (Admin only, subtitle "Passwords OADM tries when it adds devices.",
  `Settings/CredentialsViewModel` + `CredentialsView`, one card without title): entries (key icon, user name, password
  masked as 8 bullets, eye icon button "Show password" / "Hide password" that loads it with `RevealCredential` and masks
  and forgets it on the second click (the shared `ui:PasswordRevealButton`; Provisioning's Set password has the same eye), copy icon button "Copy password" (clipboard of the window, loaded on demand, not
  shown), added time, Remove as a red icon button), a separator, then the add form under the heading "Add credential"
  (user name, password, "Add credential"). Stored encrypted on the server (`CredentialListStore`, table
  `CredentialListEntries`), tried on every discovered device (`docs/spec/add-devices.md`).
- Rail page **Plugins** (Admin only) turns plugin packages on and off (`docs/spec/plugins.md` "Plugins on and off").
- Rail page **About** (everyone, subtitle "Version and licenses.", `Settings/AboutPageView` around the card
  `Settings/AboutView`, `AboutViewModel`): terms of use (card title, a scrollable block with wider line spacing), client
  version, server version (`ServerSettings.server_version`), the sentence on the Apache-2.0 license, "Show licenses" shows
  `THIRD-PARTY-NOTICES.txt` (next to the exe, the macOS app's `Contents/Resources`, or `THIRD-PARTY-NOTICES.md` in a
  checkout) in a read-only `ui:CodeView` (Plain).

## Backup and restore

Settings page, group Server: Automatic backups, Backup file, Backups on the server (Administrator only, never grantable).
Core `Oadm.Core.Backup` (`BackupFile`, `BackupArchive`), server `Oadm.Server.Backup` (`BackupManager`, `BackupGrpcService`,
`BackupScheduleHostedService`, `ServerRestart`), gRPC `BackupService` (`backup.proto`), client `Backup/` (`BackupViewModel`,
`BackupPasswordWindow`, `RestoreWindow`; the rows in `Settings/SettingsView`).

- **File** (`.oadmbackup`, `BackupFile`): "OADMBACKUP", format byte, a plain JSON header (OADM version, server name, last
  migration, kind, PBKDF2-SHA256 600,000 iterations, salt, nonce, key check), then the body (a ZIP) in 1 MB chunks, each
  AES-256-GCM with its own nonce and the associated data SHA-256(header) | chunk number | last flag (no reordering, cutting
  or header change unnoticed). The key check names a wrong password apart from a damaged file. Passwords 8..256 characters.
- **Content** (`BackupArchive`): `manifest.json` (counts per area, plugin packages, plugin data folders), `oadm.db` (a
  consistent `VACUUM INTO` copy without the login sessions, the MCP access keys and MyAxis sign-ins), `master.key` (only inside the encrypted body; a backup restores
  on another computer and every stored password and plugin secret stays readable), `plugin-data/**` (files in use skipped).
  Not in it: the server's TLS certificate (the target keeps its own), `install.json` (this installation's ID and the OADM
  PRO trials started here, so a running trial never moves to another computer, `docs/spec/licensing.md` "Trial"), uploads,
  logs, the optional plugins' files (`addons/`: the database holds their list, setting `AddOns.Installed`; after the restore
  their rows offer Install again, `docs/spec/add-ons.md` "Server").
- **Versions**: a backup's last migration must be one of this build's: an older one is upgraded by the normal migrations
  at the next start (**migrations are never deleted**), a newer one is refused before the password ("This backup is from
  OADM x, newer than this server (y). Update OADM to x or later first."). Before every migration of an existing database the
  server copies it to `backups/before-upgrade/oadm-<time>-<last migration>.db` (newest five kept, `DatabaseInitializer`).
- **Restore**: the backup is checked and unpacked to `restore/staged` (only `oadm.db`, `master.key` and `plugin-data/...`
  entries inside the folder; a wrong password or a damaged file changes nothing), then a safety backup of the current state
  with the same password (`backups/oadm-backup-<time>-before-restore`, newest five), running tasks cancelled, audit
  "Started restore", and the server restarts (`ServerRestart`: as a service it exits with code 75 two seconds later and the
  service manager starts it again; on a console `Program` builds a new host in the process). The next start applies it in
  step 0b of `OadmServerHost.StartAsync`, before anything opens the database or the key: the server's database, key and
  plugin data move to `restore/previous-<time>` (newest three kept), the staged files take their place, the TLS key in
  `server-tls.json` (Linux, macOS) is encrypted again with the restored key; any failure puts the old files back. Step 2c
  records the report (`Backup.LastRestore`: "Devices: 4,812 restored", plugins of the backup that are not installed; a
  plugin this version no longer ships: "Plugin <id> is no longer part of OADM: its settings and data were deleted.", its
  state deleted at that start, never when the backup came from a newer OADM, `docs/spec/plugin-removal.md`), ends
  every login session and audits "Restored backup" for the administrator who asked.
- **Automatic backups** (setting `Backup.Schedule`: on, time "HH:mm" server time, keep 1..365 (7), the password encrypted
  with the master key, purpose `oadm:backup-schedule`): daily, checked every 30 s, a missed day made up at the next check,
  into `<datafolder>/backups/oadm-backup-<time>-automatic.oadmbackup`; `Backup.LastAutomatic` keeps the outcome. One backup
  or restore at a time.
- **gRPC** `BackupService` (all Admin): `GetState`, `SetSchedule` (empty password keeps the stored one), `Create` and
  `Download` (stream of `DownloadChunk`: name and length first, 256 KB chunks, uncompressed), `Delete`, `Inspect`
  (`wrong_password`, `problem`, the counts), `Restore` (an upload of `FileService` or a server backup by name; names must
  match `oadm-backup-<date>-<time>-<kind>.oadmbackup`). Audited: Created backup, Downloaded backup, Deleted backup, Changed
  automatic backups, Started restore, Restored backup.
- **Client**: Back up now (password + confirm, then the save picker; the file streams to disk with the progress row), Restore
  from file (uploaded first) or a server backup's Restore (dialog: password, Check shows the counts, Restore after a
  confirmation naming the running tasks), Automatic backups (saved by the page's one Save), the list with Download,
  Restore, Delete (red, confirmed); a failed last automatic backup and the last restore report as chips (see "Settings
  page").
- **AI agents** (decision 2026-11-15, `docs/spec/mcp.md` "Administration as built"): an
  administrator's agent sets the automatic backups, backs up now, lists, downloads and deletes kept backups and restores one,
  each change a preview and a run of these methods. Every backup password comes through the secret channel, typed twice
  where the page asks twice; a backup leaves only as a file of a download link (the Assistant: a file card), a backup to
  restore comes through an upload link. Back up now's file is made when it is fetched (the password held encrypted in memory
  until then). The restore's plan warns that everything is replaced, the tasks are cancelled, the server restarts with every
  client and agent disconnected and every access key ends; its run checks the password with `Inspect` first.
- **Tests**: `tests/Oadm.Core.Tests/Backup/BackupTests` (format, another server, an older OADM upgraded, rollback, entries
  outside the folder), `tests/Oadm.Server.Tests/BackupServiceTests` (create, check, restore with a restart, automatic,
  schedule), `tests/Oadm.Client.Tests/BackupCardTests` and `SettingsPageTests` (+ screenshots).

## Support report

Settings page, group Support, row **Support report** (Administrator only, hidden for other roles): "Logs and server
details for a support request. No passwords are included." and **Create support report** (save dialog, suggested
`oadm-support-<yyyyMMdd-HHmm>.zip`; `ui:ProgressRow` while it runs; "Saved <file>." left of the button). Server
`Oadm.Server.Support` (`SupportReport`, `SupportGrpcService`), gRPC `SupportService.CreateReport` (`support.proto`, audited
"Created support report"), client `Support/` (`SupportViewModel`).

- **Server part** (made in `<datafolder>/support`, deleted after the download; files older than 30 minutes are leftovers and
  go with the next report): `oadm-support-<server name>-<yyyyMMdd-HHmm>.zip` with `logs/` (the server's `*.log` files,
  newest first up to 200 MB, `Oadm.Contracts.Logging.LogArchive`; files in use read shared; the ones left out are named in
  the summary) and `summary.txt`: OADM version, server name, service or console, OS, architecture, .NET, time zone, uptime;
  the data folder and the sizes of database, plugin data, uploads, backups and logs; the server settings of the Settings
  page; device counts by status, model and AXIS OS; counts of waiting devices, tags, event rules and compliance checks;
  tasks of the last 7 days by state and plugin; sites (name, online / offline / not bound, node version, last seen); plugin
  packages (id, version, on or off) and load errors; the PRO license state and plan; the last automatic backup and restore.
- **Never in it**: passwords, credentials, keys, the master key, tokens, the license key text, backup passwords, the
  database file (`SupportReportTests` seeds such values and scans every entry). The summary lists no device addresses or
  serial numbers; the logs are copied as they are and name devices by address and serial number (and the user names OADM
  logs in with), as the Logs page shows them.
- **AI agents** (decision 2026-11-15): `oadm_support_report` answers the server part as a file of a download link (the
  Assistant: a file card); the client part is only in the client's report.
- **Client part**: the client downloads the server's ZIP to a temporary file, then writes the chosen file with every
  entry of it plus this computer's `client-*.log` files under `client-logs/` (same 200 MB cap), off the UI thread; a
  failure removes the cut file.
- **Tests**: `tests/Oadm.Server.Tests/SupportReportTests` (contents, no secrets, admin only, audit, the log cap, the file
  name), `tests/Oadm.Client.Tests/SupportCardTests` (combined ZIP, failure, row for administrators + screenshots).
