# Settings and backup

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Server-side in `Setting`. Keys: `Polling.IntervalSeconds` (60, 5..86400),
`Polling.FullRefreshMinutes` (10, 1..1440, Settings page "Read device details every (min)"), `Events.LogRetentionDays` (30, 0..3650) and `Events.LogMaxEntries` (100000, 0 or
100..1000000): event log retention (server-only), `Scan.Parallelism` (32, Settings page "Addresses scanned at once"), `Scan.TimeoutMs` (1500,
Settings page "Scan timeout (ms)": range scans and the first contact of imported lines), `Devices.WaitingRetryMinutes` (15,
1..1440, Settings page "Try waiting devices every (min)", proto `optional int32 waiting_retry_minutes = 10`; see "Waiting
list"), `Devices.AllowBasicOverHttp` (false; Settings page card **Security**, label "Device login", check box "Allow Basic
login over HTTP", hint "Sends passwords unencrypted. Only for devices without HTTPS.", operators read On / Off; proto
`optional bool allow_basic_over_http = 11`; read live by every device connection, see `docs/spec/vapix.md`),
`Discovery.ZeroConfSeconds` (30, 5..300: a zero-conf scan of the add page ends after this time; Settings
page "Discovery duration (s)"), `Server.Name` (hostname),
`Server.ListenUrl`, `Uploads.MaxMegabytes` (2048, 1..65536), `Uploads.RetentionHours` (24,
1..8760; both server-only, not on the settings page yet), `Tasks.RetentionDays` (90, 0..3650, 0 = no age
limit) and `Tasks.MaxHistory` (50000, 0 or 100..1000000, 0 = no limit): task history retention, applied
one minute after start and then hourly by `TaskRetentionHostedService` (finished tasks only, one bulk
delete, one Removed change per task; server-only, not on the settings page yet),
`Tasks.MaxParallelPerPlugin` (16, 1..256: how many tasks (= devices) of one task plugin run at the same
time, read live by the task engine, plugin `MaxParallelDevices` can only lower it; proto
`optional int32 max_parallel_tasks_per_plugin = 9` so a partial `Set` keeps it; Settings page "Parallel
tasks per plugin" with the hint "How many devices a task runs on at the same time, e.g. restarts or
firmware updates."). The former `Devices.UseHostName` is gone: the add page chooses DNS names or IP addresses
per add (see "Add Devices Page"); proto `use_host_name = 7` is kept unused for its field number. Settings page in the client exposes them (five cards Polling, Discovery, Tasks, Security, Server, saved
together by Save and Reload in a row of their own below the five cards; operators see the values as plain text without Save and Reload). Rail page **Credentials** (Admin only, subtitle "Passwords OADM
tries when it adds devices.", `Settings/CredentialsViewModel` + `CredentialsView`, one card without title): entries (key icon, user name, password masked as 8 bullets, eye icon button "Show password" /
"Hide password" that loads it with `RevealCredential` and masks (and forgets) it on the second click, copy icon
button "Copy password" (clipboard of the window, loaded on demand, not shown), added time, Remove as a red icon button), a separator, then the add form under the heading "Add credential" (user
name, password, "Add credential"); stored encrypted on the server (`CredentialListStore`, table
CredentialListEntries), tried on every discovered device (see "Add Devices Page"). Rail page **Plugins** (Admin only)
turns plugin packages on and off (see "Plugins on and off"). Rail page **About** (everyone,
subtitle "Version and licenses.", `Settings/AboutPageView` around the card `Settings/AboutView`, `AboutViewModel`):
terms of use (card title, a scrollable block with wider line spacing), client version, server version (`ServerSettings.server_version`), the
sentence on the Apache-2.0 license, "Show licenses" shows `THIRD-PARTY-NOTICES.txt` (next to the exe, the macOS app's
`Contents/Resources`, or `THIRD-PARTY-NOTICES.md` in a checkout) in a read-only `ui:CodeView` (Plain). The Settings page
starts with the card **Client** (every user, saved at once in the client settings file): "Decode video on the
graphics card" with the test's result as hint, "Show video details over the picture" and "Frame rate (adaptive quality)"
(see "Live view"), "Appearance" (Dark, Light; see "Visual Style"). The page has two columns (each up to 760 px, left-aligned): Client and the server settings left,
the card **Backup and restore** right (administrators, see "Backup and restore") with the card **License** below it (every
user, `docs/spec/licensing.md`) and the card **Support** below that (administrators, see "Support report"); narrower than 1100 px the right column
moves below the server settings. The server
address is set with `--server` or in the
client settings file. Client-side (local JSON in LocalApplicationData): server address, device grid column layout and group
mode (`GroupDevicesByTag`), bottom pane state, `GpuDecoding`, `ShowVideoDetails`, `AdaptiveFps`, `Appearance`.

# Backup and restore

Settings page card **Backup and restore** (Administrator only, never grantable).
Core `Oadm.Core.Backup` (`BackupFile`, `BackupArchive`), server `Oadm.Server.Backup` (`BackupManager`, `BackupGrpcService`,
`BackupScheduleHostedService`, `ServerRestart`), gRPC `BackupService` (`backup.proto`), client `Backup/` (`BackupViewModel` +
`BackupCardView`, `BackupPasswordWindow`, `RestoreWindow`).

- **File** (`.oadmbackup`, `BackupFile`): "OADMBACKUP", format byte, a plain JSON header (OADM version, server name, last
  migration, kind, PBKDF2-SHA256 600,000 iterations, salt, nonce, key check), then the body (a ZIP) in 1 MB chunks, each
  AES-256-GCM with its own nonce and the associated data SHA-256(header) | chunk number | last flag (no reordering, cutting
  or header change unnoticed). The key check names a wrong password apart from a damaged file. Passwords 8..256 characters.
- **Content** (`BackupArchive`): `manifest.json` (counts per area, plugin packages, plugin data folders), `oadm.db` (a
  consistent `VACUUM INTO` copy without the login sessions), `master.key` (only inside the encrypted body; a backup restores
  on another computer and every stored password and plugin secret stays readable), `plugin-data/**` (files in use skipped).
  Not in it: the server's TLS certificate (the target keeps its own), `install.json` (this installation's ID and the OADM
  PRO trials started here, so a running trial never moves to another computer, `docs/spec/licensing.md` "Trial"), uploads,
  logs.
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
  records the report (`Backup.LastRestore`: "Devices: 4,812 restored", plugins of the backup that are not installed), ends
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
  confirmation naming the running tasks), Automatic backups (Save is secondary: one accent button per page), the list with
  Download, Restore, Delete (red, confirmed), the last automatic backup and the last restore report.
- **Tests**: `tests/Oadm.Core.Tests/Backup/BackupTests` (format, another server, an older OADM upgraded, rollback, entries
  outside the folder), `tests/Oadm.Server.Tests/BackupServiceTests` (create, check, restore with a restart, automatic,
  schedule), `tests/Oadm.Client.Tests/BackupCardTests` (+ screenshots).

# Support report

Settings page card **Support** (right column below License, Administrator only, hidden for other roles): "Logs and server
details for a support request. No passwords are included." and **Create support report** (save dialog, suggested
`oadm-support-<yyyyMMdd-HHmm>.zip`; `ui:ProgressRow` while it runs; "Saved <file>." next to the button). Server
`Oadm.Server.Support` (`SupportReport`, `SupportGrpcService`), gRPC `SupportService.CreateReport` (`support.proto`, audited
"Created support report"), client `Support/` (`SupportViewModel`, `SupportCardView`).

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
- **Client part**: the client downloads the server's ZIP to a temporary file, then writes the chosen file with every
  entry of it plus this computer's `client-*.log` files under `client-logs/` (same 200 MB cap), off the UI thread; a
  failure removes the cut file.
- **Tests**: `tests/Oadm.Server.Tests/SupportReportTests` (contents, no secrets, admin only, audit, the log cap, the file
  name), `tests/Oadm.Client.Tests/SupportCardTests` (combined ZIP, failure, card for administrators + screenshots).
